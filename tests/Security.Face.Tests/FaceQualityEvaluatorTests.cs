using OpenCvSharp;
using Security.Core.Enums;
using Security.Core.Models;
using Security.Face.Detection;
using Xunit;

namespace Security.Face.Tests;

/// <summary>
/// Quality gates that decide whether an enrollment sample is accepted.
/// Images are synthesised so every rejection reason can be provoked exactly.
/// </summary>
public class FaceQualityEvaluatorTests
{
    private const int FrameWidth = 640;
    private const int FrameHeight = 480;

    private static Mat NewColorFrame() => new(FrameHeight, FrameWidth, MatType.CV_8UC3, Scalar.All(120));

    /// <summary>High-frequency vertical stripes: sharply focused, mean ~128.</summary>
    private static Mat SharpGray()
    {
        var gray = new Mat(FrameHeight, FrameWidth, MatType.CV_8UC1, Scalar.Black);
        for (var x = 0; x < FrameWidth; x += 2)
            Cv2.Rectangle(gray, new Rect(x, 0, 1, FrameHeight), Scalar.White, -1);
        return gray;
    }

    private static Mat FlatGray(byte value) => new(FrameHeight, FrameWidth, MatType.CV_8UC1, Scalar.All(value));

    private static DetectedFace LargeFace() => new()
    {
        X = 200,
        Y = 100,
        Width = 240,
        Height = 300,
    };

    private static RecognitionOptions DefaultOptions() => new();

    [Fact]
    public void Brightness_of_black_and_white_images_is_exact()
    {
        using var black = FlatGray(0);
        using var white = FlatGray(255);

        Assert.Equal(0, FaceQualityEvaluator.ComputeBrightness(black), 3);
        Assert.Equal(255, FaceQualityEvaluator.ComputeBrightness(white), 3);
    }

    [Fact]
    public void Blur_score_of_a_uniform_image_is_near_zero()
    {
        using var flat = FlatGray(127);
        Assert.True(FaceQualityEvaluator.ComputeBlurScore(flat) < 1.0);
    }

    [Fact]
    public void Blur_score_of_a_sharp_image_is_much_higher_than_a_flat_one()
    {
        using var sharp = SharpGray();
        using var flat = FlatGray(127);

        var sharpScore = FaceQualityEvaluator.ComputeBlurScore(sharp);
        var flatScore = FaceQualityEvaluator.ComputeBlurScore(flat);

        Assert.True(sharpScore > flatScore * 100,
            $"Expected sharp ({sharpScore}) to far exceed flat ({flatScore})");
    }

    [Fact]
    public void Blur_score_is_computed_inside_the_roi_only()
    {
        // Flat everywhere except a striped band at the left edge.
        using var gray = FlatGray(127);
        for (var x = 0; x < 100; x += 2)
            Cv2.Rectangle(gray, new Rect(x, 0, 1, FrameHeight), Scalar.White, -1);

        var stripedRoi = new Rect(0, 0, 100, FrameHeight);
        var flatRoi = new Rect(200, 0, 200, FrameHeight);

        Assert.True(FaceQualityEvaluator.ComputeBlurScore(gray, stripedRoi) >
                    FaceQualityEvaluator.ComputeBlurScore(gray, flatRoi) + 1);
    }

    [Fact]
    public void Null_arguments_are_rejected()
    {
        using var gray = FlatGray(100);
        Assert.Throws<ArgumentNullException>(() => FaceQualityEvaluator.ComputeBlurScore(null!));
        Assert.Throws<ArgumentNullException>(() => FaceQualityEvaluator.ComputeBrightness(null!));
    }

    // ---------------- Evaluate: rejection reasons ----------------

    [Fact]
    public void Missing_face_is_rejected_as_NoFaceDetected()
    {
        using var frame = NewColorFrame();
        using var gray = SharpGray();

        var result = FaceQualityEvaluator.Evaluate(frame, gray, null, DefaultOptions());

        Assert.False(result.IsAcceptable);
        Assert.Equal(SampleQualityIssue.NoFaceDetected, result.Issue);
        Assert.False(string.IsNullOrWhiteSpace(result.Message));
    }

    [Fact]
    public void Undersized_face_is_rejected_as_FaceTooSmall()
    {
        using var frame = NewColorFrame();
        using var gray = SharpGray();

        var tiny = new DetectedFace { X = 300, Y = 200, Width = 60, Height = 70 };
        var result = FaceQualityEvaluator.Evaluate(frame, gray, tiny, DefaultOptions());

        Assert.False(result.IsAcceptable);
        Assert.Equal(SampleQualityIssue.FaceTooSmall, result.Issue);
    }

    [Fact]
    public void Distant_face_is_rejected_as_FaceTooFar()
    {
        var wideFrame = new Mat(480, 2000, MatType.CV_8UC3, Scalar.All(120));
        var gray = SharpGray2(2000, 480);

        try
        {
            // 150px clears the absolute minimum but fills only 7.5% of the frame.
            var face = new DetectedFace { X = 900, Y = 150, Width = 150, Height = 190 };

            var result = FaceQualityEvaluator.Evaluate(wideFrame, gray, face, DefaultOptions());

            Assert.False(result.IsAcceptable);
            Assert.Equal(SampleQualityIssue.FaceTooFar, result.Issue);
        }
        finally
        {
            wideFrame.Dispose();
            gray.Dispose();
        }
    }

    [Fact]
    public void Dark_face_is_rejected_as_TooDark()
    {
        using var frame = NewColorFrame();
        using var gray = FlatGray(10);

        var result = FaceQualityEvaluator.Evaluate(frame, gray, LargeFace(), DefaultOptions());

        Assert.False(result.IsAcceptable);
        Assert.Equal(SampleQualityIssue.TooDark, result.Issue);
    }

    [Fact]
    public void Overexposed_face_is_rejected_as_TooBright()
    {
        using var frame = NewColorFrame();
        using var gray = FlatGray(250);

        var result = FaceQualityEvaluator.Evaluate(frame, gray, LargeFace(), DefaultOptions());

        Assert.False(result.IsAcceptable);
        Assert.Equal(SampleQualityIssue.TooBright, result.Issue);
    }

    [Fact]
    public void Out_of_focus_face_is_rejected_as_Blurry()
    {
        using var frame = NewColorFrame();
        using var gray = FlatGray(128); // correct exposure, no detail

        var result = FaceQualityEvaluator.Evaluate(frame, gray, LargeFace(), DefaultOptions());

        Assert.False(result.IsAcceptable);
        Assert.Equal(SampleQualityIssue.Blurry, result.Issue);
    }

    [Fact]
    public void Well_lit_sharp_large_face_is_accepted()
    {
        using var frame = NewColorFrame();
        using var gray = SharpGray();

        var result = FaceQualityEvaluator.Evaluate(frame, gray, LargeFace(), DefaultOptions());

        Assert.True(result.IsAcceptable, $"Expected accept but got {result.Issue}: {result.Message}");
        Assert.Equal(SampleQualityIssue.None, result.Issue);
        Assert.Equal(LargeFace().Width, result.FaceSize, 3);
    }

    [Fact]
    public void Every_rejection_carries_actionable_guidance()
    {
        using var frame = NewColorFrame();
        var cases = new (DetectedFace Face, Mat Gray, SampleQualityIssue Issue)[]
        {
            (null!, SharpGray(), SampleQualityIssue.NoFaceDetected),
            (new DetectedFace { X = 300, Y = 200, Width = 60, Height = 70 }, SharpGray(), SampleQualityIssue.FaceTooSmall),
            (LargeFace(), FlatGray(10), SampleQualityIssue.TooDark),
            (LargeFace(), FlatGray(250), SampleQualityIssue.TooBright),
            (LargeFace(), FlatGray(128), SampleQualityIssue.Blurry),
        };

        foreach (var (face, gray, issue) in cases)
        {
            using (gray)
            {
                var result = FaceQualityEvaluator.Evaluate(frame, gray, face, DefaultOptions());
                Assert.Equal(issue, result.Issue);
                Assert.False(string.IsNullOrWhiteSpace(result.Message));
                Assert.True(result.Message.Length > 10, "Guidance should be a full sentence.");
            }
        }
    }

    private static Mat SharpGray2(int width, int height)
    {
        var gray = new Mat(height, width, MatType.CV_8UC1, Scalar.Black);
        for (var x = 0; x < width; x += 2)
            Cv2.Rectangle(gray, new Rect(x, 0, 1, height), Scalar.White, -1);
        return gray;
    }
}
