using OpenCvSharp;
using Security.Core.Enums;
using Security.Core.Models;

namespace Security.Face.Detection;

/// <summary>
/// Frame quality checks used to refuse bad samples (blur, lighting, size).
/// Pure image math — no model required, so it is cheap per frame and testable.
/// </summary>
public static class FaceQualityEvaluator
{
    /// <summary>
    /// Variance of the Laplacian over a grayscale ROI. Lower = blurrier.
    /// </summary>
    public static double ComputeBlurScore(Mat gray, Rect? roi = null)
    {
        ArgumentNullException.ThrowIfNull(gray);

        using var region = roi is { } r && IsInside(r, gray) ? new Mat(gray, r) : gray;
        using var laplacian = new Mat();
        Cv2.Laplacian(region, laplacian, MatType.CV_64FC1);
        Cv2.MeanStdDev(laplacian, out _, out var stddev);
        return stddev.Val0 * stddev.Val0;
    }

    /// <summary>Mean luminance of a grayscale image or ROI, 0..255.</summary>
    public static double ComputeBrightness(Mat gray, Rect? roi = null)
    {
        ArgumentNullException.ThrowIfNull(gray);

        using var region = roi is { } r && IsInside(r, gray) ? new Mat(gray, r) : gray;
        var mean = Cv2.Mean(region);
        return mean.Val0;
    }

    /// <summary>
    /// Run the full quality gate for an enrollment/recognition candidate.
    /// </summary>
    public static SampleQualityResult Evaluate(
        Mat frame,
        Mat gray,
        DetectedFace? face,
        RecognitionOptions options)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(gray);
        ArgumentNullException.ThrowIfNull(options);

        if (face is null)
            return SampleQualityResult.Reject(
                SampleQualityIssue.NoFaceDetected,
                "No face detected. Position your face in the frame.");

        var frameWidth = frame.Width;
        var faceRect = ToRect(face, frame.Width, frame.Height);

        if (frameWidth <= 0)
            return SampleQualityResult.Reject(
                SampleQualityIssue.ProcessingError,
                "Frame could not be processed.");

        // Size gates first — they are the cheapest and most actionable.
        if (face.Width < options.MinimumFaceSize)
            return SampleQualityResult.Reject(
                SampleQualityIssue.FaceTooSmall,
                $"Face is too small ({(int)face.Width}px). Move closer to the camera.",
                faceSize: face.Width);

        if (face.Width / (double)frameWidth < options.MinimumFaceRatio)
            return SampleQualityResult.Reject(
                SampleQualityIssue.FaceTooFar,
                "Move closer so your face fills more of the frame.",
                faceSize: face.Width);

        var brightness = ComputeBrightness(gray, faceRect);
        if (brightness < options.MinimumBrightness)
            return SampleQualityResult.Reject(
                SampleQualityIssue.TooDark,
                "Lighting is too dark. Improve the light on your face.",
                brightness: brightness,
                faceSize: face.Width);

        if (brightness > options.MaximumBrightness)
            return SampleQualityResult.Reject(
                SampleQualityIssue.TooBright,
                "Lighting is too bright. Reduce glare or move out of direct light.",
                brightness: brightness,
                faceSize: face.Width);

        var blur = ComputeBlurScore(gray, faceRect);
        if (blur < options.MinimumBlurScore)
            return SampleQualityResult.Reject(
                SampleQualityIssue.Blurry,
                "Image is blurry. Hold still and keep the camera focused.",
                blur: blur,
                brightness: brightness,
                faceSize: face.Width);

        return SampleQualityResult.Accept(blur, brightness, face.Width);
    }

    private static Rect ToRect(DetectedFace face, int frameWidth, int frameHeight)
    {
        var x = (int)Math.Clamp(Math.Floor(face.X), 0, Math.Max(0, frameWidth - 1));
        var y = (int)Math.Clamp(Math.Floor(face.Y), 0, Math.Max(0, frameHeight - 1));
        var w = (int)Math.Clamp(Math.Ceiling(face.Width), 1, frameWidth - x);
        var h = (int)Math.Clamp(Math.Ceiling(face.Height), 1, frameHeight - y);
        return new Rect(x, y, w, h);
    }

    private static bool IsInside(Rect r, Mat image)
        => r.X >= 0 && r.Y >= 0 && r.Width > 0 && r.Height > 0 &&
           r.X + r.Width <= image.Width && r.Y + r.Height <= image.Height;
}
