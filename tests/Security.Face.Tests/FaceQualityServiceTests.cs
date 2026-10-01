using OpenCvSharp;
using Security.Core.Interfaces;
using Security.Core.Models;
using Security.Face.Quality;
using Xunit;

namespace Security.Face.Tests;

/// <summary>
/// The live frame-quality gate that drives enrollment guidance and suppresses
/// recognition on unusable frames.
///
/// Images are synthesised so each rejection can be provoked deterministically;
/// no camera and no detection model is required (the detector is faked), which
/// is what makes these tests runnable on a machine with no webcam.
/// </summary>
public class FaceQualityServiceTests
{
    private const int FrameWidth = 640;
    private const int FrameHeight = 480;

    /// <summary>
    /// The complete vocabulary the UI is allowed to show. Every rejection this
    /// service can produce must start with one of these, so a raw exception
    /// message or a path can never reach the operator.
    /// </summary>
    private static readonly string[] AllowedReasonPrefixes =
    {
        "No face detected",
        "Only one person should be visible",
        "Please face the camera",
        "Move closer",
        "Move further away",
        "Face is too small",
        "Lighting is too",
        "Image is blurry",
        "Image quality is too low",
        "Frame could not be processed",
    };

    // --- Fixture images -----------------------------------------------------

    /// <summary>
    /// Alternating one-pixel columns: perfectly sharp, mean luminance ~127, and
    /// a plausible face-level exposure — i.e. the ideal sample.
    /// </summary>
    private static Mat StripedFrame()
    {
        var frame = new Mat(FrameHeight, FrameWidth, MatType.CV_8UC3, Scalar.All(0));
        for (var x = 0; x < FrameWidth; x += 2)
            Cv2.Rectangle(frame, new Rect(x, 0, 1, FrameHeight), Scalar.White, -1);
        return frame;
    }

    private static Mat FlatFrame(byte value)
        => new(FrameHeight, FrameWidth, MatType.CV_8UC3, Scalar.All(value));

    /// <summary>240px wide, sitting on the frame centre — inside every gate.</summary>
    private static DetectedFace CentredFace() => new() { X = 200, Y = 90, Width = 240, Height = 300 };

    private static FaceDetectionResult With(params DetectedFace[] faces)
        => new() { Faces = faces };

    private static FaceQualityService CreateService(IFaceDetectionService? detection = null)
        => new(detection ?? new UnusedDetection(), new UnusedSettings());

    private static void AssertReasonIsAllowedAndScoreInRange(FaceQualityResult result)
    {
        Assert.InRange(result.Score, 0, 1);

        if (result.IsAcceptable)
        {
            Assert.Equal(string.Empty, result.Reason);
            return;
        }

        Assert.False(string.IsNullOrWhiteSpace(result.Reason));
        Assert.Contains(
            AllowedReasonPrefixes,
            prefix => result.Reason.StartsWith(prefix, StringComparison.Ordinal));
    }

    // --- Face count ---------------------------------------------------------

    [Fact]
    public void Frame_without_a_face_is_rejected_and_reports_a_zero_count()
    {
        using var frame = StripedFrame();
        var result = CreateService().Evaluate(frame, With());

        Assert.False(result.IsAcceptable);
        Assert.Equal(0, result.FaceCount);
        Assert.StartsWith("No face detected", result.Reason, StringComparison.Ordinal);
        AssertReasonIsAllowedAndScoreInRange(result);
    }

    [Fact]
    public void Frame_with_two_faces_is_rejected_before_any_other_check()
    {
        using var frame = StripedFrame();
        var two = With(
            new DetectedFace { X = 40, Y = 90, Width = 240, Height = 300 },
            new DetectedFace { X = 360, Y = 90, Width = 240, Height = 300 });

        var result = CreateService().Evaluate(frame, two);

        Assert.False(result.IsAcceptable);
        Assert.Equal(2, result.FaceCount);
        Assert.Equal("Only one person should be visible.", result.Reason);
        AssertReasonIsAllowedAndScoreInRange(result);
    }

    // --- Detector noise -----------------------------------------------------

    [Fact]
    public void A_tiny_false_positive_does_not_block_a_perfectly_good_face()
    {
        // YuNet reports a ~15px blip alongside the subject on real hardware.
        // Before this rule it produced "Only one person should be visible" on
        // half of all frames and enrollment could not complete.
        using var frame = StripedFrame();
        var real = new DetectedFace { X = 200, Y = 90, Width = 240, Height = 300 };
        var blip = new DetectedFace { X = 560, Y = 440, Width = 15, Height = 20 };

        var result = CreateService().Evaluate(frame, With(real, blip));

        Assert.Equal(1, result.FaceCount);
        Assert.True(result.IsAcceptable, $"expected the subject to win, got: {result.Reason}");
        AssertReasonIsAllowedAndScoreInRange(result);
    }

    [Fact]
    public void A_frame_containing_only_detector_noise_reports_no_face()
    {
        using var frame = StripedFrame();
        var blip = new DetectedFace { X = 560, Y = 440, Width = 15, Height = 20 };

        var result = CreateService().Evaluate(frame, With(blip));

        Assert.False(result.IsAcceptable);
        Assert.Equal(0, result.FaceCount);
        Assert.StartsWith("No face detected", result.Reason, StringComparison.Ordinal);
        AssertReasonIsAllowedAndScoreInRange(result);
    }

    [Fact]
    public void A_second_face_that_is_large_enough_still_blocks_enrollment()
    {
        // The floor is about detector noise, not about ignoring real people:
        // a genuinely-sized second face must still stop a single-person session.
        using var frame = StripedFrame();
        var subject = new DetectedFace { X = 60, Y = 90, Width = 200, Height = 250 };
        var other = new DetectedFace { X = 420, Y = 140, Width = 150, Height = 180 };

        var result = CreateService().Evaluate(frame, With(subject, other));

        Assert.False(result.IsAcceptable);
        Assert.Equal(2, result.FaceCount);
        Assert.Equal("Only one person should be visible.", result.Reason);
    }

    // --- Guidance for the operator -----------------------------------------

    [Fact]
    public void Face_off_to_one_side_tells_the_operator_to_face_the_camera()
    {
        using var frame = StripedFrame();
        // Centre at (545, 240): 225px from frame centre = 0.56 of the
        // half-diagonal, past the 0.50 tolerance, but still inside the frame
        // and inside every size gate, so centring is the only thing wrong.
        var side = new DetectedFace { X = 465, Y = 140, Width = 160, Height = 200 };

        var result = CreateService().Evaluate(frame, With(side));

        Assert.False(result.IsAcceptable);
        Assert.Equal("Please face the camera.", result.Reason);
        AssertReasonIsAllowedAndScoreInRange(result);
    }

    [Fact]
    public void Face_filling_most_of_the_frame_tells_the_operator_to_move_further_away()
    {
        using var frame = StripedFrame();
        // 500/640 = 0.78 of the frame width, perfectly centred, so only the
        // "too close" rule can be responsible.
        var veryClose = new DetectedFace { X = 70, Y = 90, Width = 500, Height = 300 };

        var result = CreateService().Evaluate(frame, With(veryClose));

        Assert.False(result.IsAcceptable);
        Assert.Equal("Move further away from the camera.", result.Reason);
        AssertReasonIsAllowedAndScoreInRange(result);
    }

    [Fact]
    public void Undersized_face_tells_the_operator_to_move_closer()
    {
        using var frame = StripedFrame();
        var far = new DetectedFace { X = 280, Y = 200, Width = 80, Height = 100 };

        var result = CreateService().Evaluate(frame, With(far));

        Assert.False(result.IsAcceptable);
        Assert.Contains("Move closer", result.Reason, StringComparison.Ordinal);
        AssertReasonIsAllowedAndScoreInRange(result);
    }

    [Fact]
    public void Dark_frame_is_rejected_for_lighting()
    {
        using var frame = FlatFrame(10);

        var result = CreateService().Evaluate(frame, With(CentredFace()));

        Assert.False(result.IsAcceptable);
        Assert.StartsWith("Lighting is too", result.Reason, StringComparison.Ordinal);
        AssertReasonIsAllowedAndScoreInRange(result);
    }

    [Fact]
    public void Empty_frame_is_rejected_as_low_quality_without_touching_the_detector()
    {
        using var frame = new Mat();
        var detection = new UnusedDetection();

        // UnusedDetection throws if Detect is ever called, so reaching this
        // assert also proves the empty-frame guard runs first.
        var result = CreateService(detection).Evaluate(frame);

        Assert.False(result.IsAcceptable);
        Assert.Equal("Image quality is too low.", result.Reason);
        AssertReasonIsAllowedAndScoreInRange(result);
    }

    // --- Acceptance ---------------------------------------------------------

    [Fact]
    public void Well_lit_sharp_centred_face_is_accepted_with_a_non_zero_score()
    {
        using var frame = StripedFrame();

        var result = CreateService().Evaluate(frame, With(CentredFace()));

        Assert.True(result.IsAcceptable);
        Assert.Equal(string.Empty, result.Reason);
        Assert.Equal(1, result.FaceCount);
        Assert.True(result.Score > 0);
        AssertReasonIsAllowedAndScoreInRange(result);
    }

    // --- Wiring -------------------------------------------------------------

    [Fact]
    public void Single_argument_overload_delegates_to_the_injected_detector()
    {
        using var frame = StripedFrame();
        var detection = new StubDetection(With(CentredFace()));

        var result = CreateService(detection).Evaluate(frame);

        Assert.True(result.IsAcceptable);
        Assert.Equal(1, detection.DetectCalls);
    }

    [Fact]
    public void Null_frame_is_a_programming_error_and_throws_rather_than_faking_a_verdict()
    {
        // A null frame can never be scored honestly, and inventing
        // "image quality too low" would hide the bug that produced it.
        Assert.Throws<ArgumentNullException>(() => CreateService().Evaluate(null!));
    }

    // --- Doubles ------------------------------------------------------------

    private sealed class UnusedDetection : IFaceDetectionService
    {
        public bool IsReady => false;

        public FaceDetectionResult Detect(Mat frame)
            => throw new InvalidOperationException("Detection is not expected in these tests.");

        public FaceDetectionResult DetectPrimary(Mat frame)
            => throw new InvalidOperationException("Detection is not expected in these tests.");

        public void Dispose()
        {
        }
    }

    private sealed class StubDetection : IFaceDetectionService
    {
        private readonly FaceDetectionResult _result;

        public StubDetection(FaceDetectionResult result) => _result = result;

        public int DetectCalls { get; private set; }

        public bool IsReady => true;

        public FaceDetectionResult Detect(Mat frame)
        {
            DetectCalls++;
            return _result;
        }

        public FaceDetectionResult DetectPrimary(Mat frame) => Detect(frame);

        public void Dispose()
        {
        }
    }

    private sealed class UnusedSettings : ISettingsService
    {
        public AppSettings Current { get; } = new();

        public RecognitionOptions Recognition { get; } = new();

        public event EventHandler? SettingsChanged
        {
            add { }
            remove { }
        }

        public Task LoadAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task SaveRecognitionAsync(RecognitionOptions options, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
