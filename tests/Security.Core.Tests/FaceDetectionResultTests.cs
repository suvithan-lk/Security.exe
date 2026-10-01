using Security.Core.Models;
using Xunit;

namespace Security.Core.Tests;

/// <summary>
/// Selection of which detection the rest of the pipeline treats as "the face".
/// </summary>
public class FaceDetectionResultTests
{
    private static DetectedFace Face(float width, float height)
        => new() { X = 0, Y = 0, Width = width, Height = height };

    [Fact]
    public void PrimaryFace_is_null_when_no_face_was_detected()
    {
        var result = new FaceDetectionResult();

        Assert.Null(result.PrimaryFace);
        Assert.False(result.HasFace);
        Assert.False(result.IsSingleFace);
    }

    [Fact]
    public void PrimaryFace_is_the_only_face_when_there_is_one()
    {
        var only = Face(240, 300);

        var result = new FaceDetectionResult { Faces = new[] { only } };

        Assert.Same(only, result.PrimaryFace);
        Assert.True(result.IsSingleFace);
    }

    [Fact]
    public void PrimaryFace_is_the_largest_face_no_matter_what_order_the_detector_used()
    {
        var background = Face(80, 100);
        var subject = Face(320, 400);

        // Detector output order is not a contract; the subject is the largest.
        var swapped = new FaceDetectionResult { Faces = new[] { background, subject } };
        var inOrder = new FaceDetectionResult { Faces = new[] { subject, background } };

        Assert.Same(subject, swapped.PrimaryFace);
        Assert.Same(subject, inOrder.PrimaryFace);
    }

    [Fact]
    public void FaceCount_and_status_text_describe_the_raw_detector_output()
    {
        var crowd = new FaceDetectionResult { Faces = new[] { Face(100, 100), Face(90, 90), Face(80, 80) } };

        Assert.Equal(3, crowd.FaceCount);
        Assert.Contains("Multiple faces", crowd.StatusText, StringComparison.Ordinal);

        var single = new FaceDetectionResult { Faces = new[] { Face(100, 100) } };
        Assert.Equal("Face detected", single.StatusText);

        var none = new FaceDetectionResult();
        Assert.Equal("No face detected", none.StatusText);
    }

    // --- Noise floor --------------------------------------------------------

    [Fact]
    public void Countable_faces_drop_detector_noise_but_keep_real_people()
    {
        // The ~15px false positive YuNet emits on texture, next to a subject.
        var noise = Face(15, 20);
        var subject = Face(300, 380);
        var detection = new FaceDetectionResult { Faces = new[] { subject, noise } };

        var countable = detection.CountableFaces(frameWidth: 1280, minimumFaceSize: 120);

        Assert.Single(countable);
        Assert.Same(subject, countable[0]);

        // And the same frame genuinely still has two people when both are real.
        var other = Face(200, 260);
        var twoPeople = new FaceDetectionResult { Faces = new[] { subject, other } };
        Assert.Equal(2, twoPeople.CountableFaces(1280, 120).Count);
    }

    [Fact]
    public void Countable_faces_returns_the_original_list_when_nothing_is_noise()
    {
        var detection = new FaceDetectionResult { Faces = new[] { Face(300, 380), Face(200, 260) } };

        var countable = detection.CountableFaces(1280, 120);

        Assert.Same(detection.Faces, countable);
    }

    [Fact]
    public void Countable_faces_of_an_empty_result_is_empty()
        => Assert.Empty(new FaceDetectionResult().CountableFaces(1280, 120));

    [Fact]
    public void The_noise_floor_stays_sensible_across_capture_sizes()
    {
        // Defaults (MinimumFaceSize 120): half of that, but never past 5% of
        // the frame and never below 24px.
        Assert.Equal(60, FaceDetectionResult.NoiseFloorWidth(1280, 120));
        Assert.Equal(32, FaceDetectionResult.NoiseFloorWidth(640, 120));   // capped by 5% of width
        Assert.Equal(24, FaceDetectionResult.NoiseFloorWidth(320, 120));   // floored at 24

        // A huge configured minimum never shrinks the floor below 24 either.
        Assert.True(FaceDetectionResult.NoiseFloorWidth(640, 4000) >= 24);
    }
}
