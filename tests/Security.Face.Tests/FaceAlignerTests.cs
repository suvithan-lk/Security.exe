using OpenCvSharp;
using Security.Core.Models;
using Security.Face.Recognition;
using Xunit;

namespace Security.Face.Tests;

/// <summary>
/// The similarity (Umeyama) fit used to align faces to the 112x112 template.
/// Verified against closed-form expectations rather than model output, so the
/// tests stay deterministic and fast.
/// </summary>
public class FaceAlignerTests
{
    private static readonly Point2f[] Source =
    [
        new(0f, 0f),
        new(10f, 0f),
        new(10f, 10f),
        new(0f, 10f),
    ];

    private static double At(Mat m, int row, int col) => m.At<double>(row, col);

    [Fact]
    public void Identity_mapping_returns_identity_matrix()
    {
        using var m = FaceAligner.EstimateSimilarity(Source, Source);

        Assert.NotNull(m);
        Assert.Equal(1.0, At(m!, 0, 0), 6);
        Assert.Equal(0.0, At(m!, 0, 1), 6);
        Assert.Equal(0.0, At(m!, 0, 2), 6);
        Assert.Equal(0.0, At(m!, 1, 0), 6);
        Assert.Equal(1.0, At(m!, 1, 1), 6);
        Assert.Equal(0.0, At(m!, 1, 2), 6);
    }

    [Fact]
    public void Translation_is_recovered_exactly()
    {
        var dst = Source.Select(p => new Point2f(p.X + 5f, p.Y - 3f)).ToArray();

        using var m = FaceAligner.EstimateSimilarity(Source, dst);

        Assert.NotNull(m);
        Assert.Equal(1.0, At(m!, 0, 0), 6);
        Assert.Equal(5.0, At(m!, 0, 2), 6);
        Assert.Equal(-3.0, At(m!, 1, 2), 6);
    }

    [Fact]
    public void Uniform_scale_is_recovered()
    {
        var dst = Source.Select(p => new Point2f(p.X * 2f, p.Y * 2f)).ToArray();

        using var m = FaceAligner.EstimateSimilarity(Source, dst);

        Assert.NotNull(m);
        Assert.Equal(2.0, At(m!, 0, 0), 5);
        Assert.Equal(2.0, At(m!, 1, 1), 5);
        // No shear/rotation => off-diagonals are zero.
        Assert.Equal(0.0, At(m!, 0, 1), 5);
        Assert.Equal(0.0, At(m!, 1, 0), 5);
    }

    [Fact]
    public void Rotation_about_the_origin_is_recovered()
    {
        const double angle = Math.PI / 6; // 30 degrees
        var dst = Source.Select(p => new Point2f(
            (float)(p.X * Math.Cos(angle) - p.Y * Math.Sin(angle)),
            (float)(p.X * Math.Sin(angle) + p.Y * Math.Cos(angle)))).ToArray();

        using var m = FaceAligner.EstimateSimilarity(Source, dst);

        Assert.NotNull(m);
        Assert.Equal(Math.Cos(angle), At(m!, 0, 0), 5);
        Assert.Equal(Math.Sin(angle), At(m!, 1, 0), 5);
    }

    [Fact]
    public void Combined_scale_rotation_translation_maps_every_point()
    {
        const double angle = Math.PI / 5;
        const double scale = 1.7;
        const double tx = 42.5;
        const double ty = -11.25;

        var dst = Source.Select(p => new Point2f(
            (float)(scale * (p.X * Math.Cos(angle) - p.Y * Math.Sin(angle)) + tx),
            (float)(scale * (p.X * Math.Sin(angle) + p.Y * Math.Cos(angle)) + ty))).ToArray();

        using var m = FaceAligner.EstimateSimilarity(Source, dst);
        Assert.NotNull(m);

        for (var i = 0; i < Source.Length; i++)
        {
            var x = Source[i].X;
            var y = Source[i].Y;

            var mappedX = At(m!, 0, 0) * x + At(m!, 0, 1) * y + At(m!, 0, 2);
            var mappedY = At(m!, 1, 0) * x + At(m!, 1, 1) * y + At(m!, 1, 2);

            Assert.Equal(dst[i].X, mappedX, 3);
            Assert.Equal(dst[i].Y, mappedY, 3);
        }
    }

    [Fact]
    public void Transform_is_similarity_only_never_shears()
    {
        // A similarity transform keeps the two basis columns orthogonal and
        // equal in length. Sheared faces would degrade matching badly.
        const double angle = 0.9;
        const double scale = 0.6;
        var dst = Source.Select(p => new Point2f(
            (float)(scale * (p.X * Math.Cos(angle) - p.Y * Math.Sin(angle))),
            (float)(scale * (p.X * Math.Sin(angle) + p.Y * Math.Cos(angle))))).ToArray();

        using var m = FaceAligner.EstimateSimilarity(Source, dst);
        Assert.NotNull(m);

        var a = At(m!, 0, 0);
        var b = At(m!, 1, 0);
        var c = At(m!, 0, 1);
        var d = At(m!, 1, 1);

        // Columns have equal length (uniform scale).
        Assert.Equal(a * a + b * b, c * c + d * d, 6);
        // Columns are orthogonal (no shear).
        Assert.Equal(0.0, a * c + b * d, 6);
    }

    [Fact]
    public void Degenerate_coincident_points_return_null_instead_of_dividing_by_zero()
    {
        var same = Enumerable.Range(0, 4).Select(_ => new Point2f(5f, 5f)).ToArray();

        Assert.Null(FaceAligner.EstimateSimilarity(same, Source));
    }

    [Fact]
    public void Too_few_points_return_null()
    {
        Assert.Null(FaceAligner.EstimateSimilarity(new[] { new Point2f(1f, 2f) }, new[] { new Point2f(3f, 4f) }));
    }

    [Fact]
    public void Null_or_mismatched_inputs_return_null()
    {
        Assert.Null(FaceAligner.EstimateSimilarity(null!, Source));
        Assert.Null(FaceAligner.EstimateSimilarity(Source, null!));
    }

    [Fact]
    public void Non_finite_coordinates_return_null()
    {
        var bad = new[] { new Point2f(float.NaN, 0f), new Point2f(1f, 1f), new Point2f(2f, 2f) };
        Assert.Null(FaceAligner.EstimateSimilarity(bad, Source));
    }

    [Fact]
    public void Align_falls_back_to_box_crop_when_landmarks_are_missing()
    {
        using var frame = new Mat(480, 640, MatType.CV_8UC3, Scalar.All(100));
        var face = new DetectedFace
        {
            X = 200,
            Y = 120,
            Width = 160,
            Height = 200,
            Landmarks = new[] { (1f, 2f) }, // fewer than five => no similarity fit
        };

        using var aligned = FaceAligner.Align(frame, face);

        Assert.NotNull(aligned);
        Assert.Equal(FaceAligner.OutputSize, aligned.Width);
        Assert.Equal(FaceAligner.OutputSize, aligned.Height);
        Assert.Equal(3, aligned.Channels());
    }

    [Fact]
    public void Align_produces_the_expected_output_geometry()
    {
        using var frame = new Mat(480, 640, MatType.CV_8UC3, Scalar.All(90));
        var face = new DetectedFace
        {
            X = 200,
            Y = 120,
            Width = 160,
            Height = 200,
            Landmarks =
            [
                (250f, 190f),
                (330f, 190f),
                (290f, 240f),
                (255f, 290f),
                (325f, 290f),
            ],
        };

        using var aligned = FaceAligner.Align(frame, face);

        Assert.NotNull(aligned);
        Assert.Equal(FaceAligner.OutputSize, aligned.Width);
        Assert.Equal(FaceAligner.OutputSize, aligned.Height);
        Assert.Equal(MatType.CV_8UC3, aligned.Type());
    }

    [Fact]
    public void Align_rejects_null_arguments()
    {
        using var frame = new Mat(10, 10, MatType.CV_8UC3);
        var face = new DetectedFace { X = 0, Y = 0, Width = 5, Height = 5 };

        Assert.Throws<ArgumentNullException>(() => FaceAligner.Align(null!, face));
        Assert.Throws<ArgumentNullException>(() => FaceAligner.Align(frame, null!));
    }
}
