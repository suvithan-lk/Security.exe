using OpenCvSharp;
using Security.Core.Enums;
using Security.Core.Interfaces;
using Security.Core.Models;
using Security.Face.Detection;

namespace Security.Face.Quality;

/// <summary>
/// Live frame quality gate.
///
/// The blur / exposure / face-size checks are delegated verbatim to
/// <see cref="FaceQualityEvaluator"/> — the same pure-image math enrollment has
/// always used — so "the wizard says this frame is fine" and "enrollment
/// accepts this frame" can never disagree. This class owns only the checks the
/// evaluator has no opinion about (face count, centring) and packages the
/// outcome as the operator-facing <see cref="FaceQualityResult"/>.
///
/// Gates run most → least actionable so the operator is told the ONE thing to
/// fix next rather than every possible complaint at once.
/// </summary>
public sealed class FaceQualityService : IFaceQualityService
{
    /// <summary>Face centre may drift this far from frame centre (0..1 of half-diagonal) before we object.</summary>
    private const double MaxCenteringOffset = 0.50;

    /// <summary>Face filling more than this fraction of the frame width is too close.</summary>
    private const double MaxFaceRatio = 0.65;

    private readonly IFaceDetectionService _detection;
    private readonly ISettingsService _settings;

    public FaceQualityService(IFaceDetectionService detection, ISettingsService settings)
    {
        _detection = detection;
        _settings = settings;
    }

    public FaceQualityResult Evaluate(Mat frame)
    {
        ArgumentNullException.ThrowIfNull(frame);

        // Reject an unusable frame before paying for (or feeding garbage into)
        // the detection model. Evaluate(frame, detection) re-checks this, but
        // only after the detector has already run on the bad frame.
        if (!IsUsableFrame(frame))
            return FaceQualityResult.Reject("Image quality is too low.", 0);

        return Evaluate(frame, _detection.Detect(frame));
    }

    public FaceQualityResult Evaluate(Mat frame, FaceDetectionResult detection)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(detection);

        if (!IsUsableFrame(frame))
            return FaceQualityResult.Reject("Image quality is too low.", 0);

        // --- Face count ------------------------------------------------------

        // YuNet occasionally reports a ~15px blip (a high-confidence false
        // positive on texture or a hand). Counting that as a person made half
        // of all frames fail with "Only one person should be visible" while a
        // perfectly good face sat in the frame — the single largest source of
        // enrollment rejections on real hardware.
        //
        // A detection below half the configured minimum face size is smaller
        // than this application could ever recognise, so it is treated as
        // detector noise rather than as a second person.
        var countable = detection.CountableFaces(frame.Width, _settings.Recognition.MinimumFaceSize);

        if (countable.Count == 0)
        {
            return FaceQualityResult.Reject(
                "No face detected. Position your face inside the frame.",
                0, faceCount: 0);
        }

        if (countable.Count > 1)
        {
            return FaceQualityResult.Reject(
                "Only one person should be visible.",
                0, faceCount: countable.Count);
        }

        var face = countable[0];
        var ratio = face.Width / (double)frame.Width;
        var offset = CenteringOffset(face, frame.Width, frame.Height);

        SampleQualityResult quality;
        try
        {
            using var gray = new Mat();
            Cv2.CvtColor(frame, gray, ColorConversionCodes.BGR2GRAY);

            // Size / too-close / too-far gate first: it is the most common and
            // the most useful instruction, and it avoids paying for blur maths
            // on a face that is obviously the wrong size.
            quality = FaceQualityEvaluator.Evaluate(frame, gray, face, _settings.Recognition);
        }
        catch (Exception)
        {
            // A malformed frame must never be mistaken for a bad face.
            return FaceQualityResult.Reject(
                "Image quality is too low.", 0, ratio: ratio, faceCount: 1, offset: offset);
        }

        if (quality.Issue == SampleQualityIssue.FaceTooSmall || quality.Issue == SampleQualityIssue.FaceTooFar)
            return Reject(quality.Message, ratio, offset, quality, countable.Count);

        // --- Centring (not covered by the evaluator) --------------------------
        if (offset > MaxCenteringOffset)
        {
            return FaceQualityResult.Reject(
                "Please face the camera.",
                CenterScore(offset),
                quality.BlurScore, quality.Brightness, ratio, 1, offset);
        }

        // --- Second-close face must not crowd a "one person" session -----------
        if (ratio > MaxFaceRatio)
        {
            return FaceQualityResult.Reject(
                "Move further away from the camera.",
                score: SizeScore(ratio),
                blur: quality.BlurScore,
                brightness: quality.Brightness,
                ratio: ratio,
                faceCount: 1,
                offset: offset);
        }

        // --- Everything else the evaluator owns (exposure, blur) --------------
        if (!quality.IsAcceptable)
            return Reject(quality.Message, ratio, offset, quality, countable.Count);

        var score =
            (0.35 * SharpnessScore(quality.BlurScore)) +
            (0.35 * ExposureScore(quality.Brightness)) +
            (0.15 * SizeScore(ratio)) +
            (0.15 * CenterScore(offset));

        return FaceQualityResult.Accept(
            score, quality.BlurScore, quality.Brightness, ratio, offset);
    }

    private static FaceQualityResult Reject(
        string message, double ratio, double offset, SampleQualityResult quality, int faceCount)
        => FaceQualityResult.Reject(
            message,
            SharpnessScore(quality.BlurScore),
            quality.BlurScore,
            quality.Brightness,
            ratio,
            faceCount,
            offset);

    /// <summary>False for an empty or zero-sized Mat, which can never be scored.</summary>
    private static bool IsUsableFrame(Mat frame)
        => !frame.Empty() && frame.Width > 0 && frame.Height > 0;

    // --- Scoring -------------------------------------------------------------

    private static double SharpnessScore(double blur)
        => Math.Clamp(blur / 200.0, 0, 1);

    private static double ExposureScore(double brightness)
        => Math.Clamp(1.0 - Math.Abs(brightness - 127.5) / 127.5, 0, 1);

    private static double SizeScore(double ratio)
        => Math.Clamp(1.0 - Math.Abs(ratio - 0.28) / 0.28, 0, 1);

    private static double CenterScore(double offset)
        => Math.Clamp(1.0 - offset / MaxCenteringOffset, 0, 1);

    /// <summary>Distance of the face centre from frame centre, as a 0..1 fraction of the half-diagonal.</summary>
    private static double CenteringOffset(DetectedFace face, int width, int height)
    {
        if (width <= 0 || height <= 0)
            return 0;

        var dx = face.CenterX - (width / 2.0);
        var dy = face.CenterY - (height / 2.0);
        var halfDiagonal = Math.Sqrt((width * width) + (height * height)) / 2.0;

        return halfDiagonal <= 0
            ? 0
            : Math.Sqrt((dx * dx) + (dy * dy)) / halfDiagonal;
    }
}
