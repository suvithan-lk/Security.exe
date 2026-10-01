using OpenCvSharp;
using Security.Core.Models;

namespace Security.Face.Recognition;

/// <summary>
/// Aligns a detected face to the canonical 112x112 SFace template using the
/// five YuNet landmarks, the same approach OpenCV's FaceRecognizerSF uses.
///
/// Alignment matters: recognition quality drops sharply on unaligned crops.
/// </summary>
public static class FaceAligner
{
    public const int OutputSize = 112;

    /// <summary>Standard five-point template for 112x112 face recognition models.</summary>
    private static readonly Point2f[] Template =
    [
        new Point2f(38.2946f, 51.6963f),  // right eye
        new Point2f(73.5318f, 51.5014f),  // left eye
        new Point2f(56.0252f, 71.7366f),  // nose
        new Point2f(41.5493f, 92.3655f),  // right mouth corner
        new Point2f(70.7299f, 92.2041f),  // left mouth corner
    ];

    /// <summary>
    /// Produce an aligned 112x112 BGR face image.
    /// Falls back to a bounding-box crop when landmarks are unusable.
    /// </summary>
    public static Mat Align(Mat frame, DetectedFace face)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(face);

        if (face.Landmarks.Count >= 5)
        {
            var aligned = TrySimilarityAlign(frame, face);
            if (aligned is not null)
                return aligned;
        }

        return CropAndResize(frame, face);
    }

    private static Mat? TrySimilarityAlign(Mat frame, DetectedFace face)
    {
        try
        {
            var src = new Point2f[5];
            for (var i = 0; i < 5; i++)
            {
                var (x, y) = face.Landmarks[i];
                if (!float.IsFinite(x) || !float.IsFinite(y))
                    return null;

                src[i] = new Point2f(x, y);
            }

            // Least-squares similarity (rotation + uniform scale + translation)
            // fitted directly to the five correspondences. Done in managed code
            // so the math is deterministic and unit-testable, with no reliance
            // on optional OpenCV estimation APIs.
            var m = EstimateSimilarity(src, Template);
            if (m is null)
                return null;

            var output = new Mat();
            Cv2.WarpAffine(
                frame,
                output,
                m,
                new Size(OutputSize, OutputSize),
                InterpolationFlags.Linear,
                BorderTypes.Replicate);

            m.Dispose();

            // A degenerate transform (all-black or wildly scaled) is worse than
            // the box crop fallback, so reject it.
            if (IsDegenerate(output))
            {
                output.Dispose();
                return null;
            }

            return output;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Fit the 2x3 affine matrix mapping <paramref name="src"/> onto
    /// <paramref name="dst"/> using a closed-form similarity transform
    /// (Umeyama's method for the 2-D, no-reflection case).
    ///
    /// Returns null when the points are degenerate (all coincident), which
    /// would otherwise produce a divide-by-zero.
    ///
    /// Mappings:  x' = a*x - b*y + tx
    ///            y' = b*x + a*y + ty
    /// where (a, b) encodes scale *and* rotation together, so the transform can
    /// never shear the face — important because sheared crops degrade matching.
    /// </summary>
    public static Mat? EstimateSimilarity(IReadOnlyList<Point2f> src, IReadOnlyList<Point2f> dst)
    {
        if (src is null || dst is null)
            return null;

        var n = Math.Min(src.Count, dst.Count);
        if (n < 2)
            return null;

        // Centroids.
        double sx = 0, sy = 0, dx = 0, dy = 0;
        for (var i = 0; i < n; i++)
        {
            if (!IsFinite(src[i]) || !IsFinite(dst[i]))
                return null;

            sx += src[i].X; sy += src[i].Y;
            dx += dst[i].X; dy += dst[i].Y;
        }

        sx /= n; sy /= n;
        dx /= n; dy /= n;

        // Covariance terms of the centered point sets.
        double sigmaXX = 0, sigmaXY = 0, sigmaYX = 0, sigmaYY = 0;
        double varSrc = 0;

        for (var i = 0; i < n; i++)
        {
            var sxi = src[i].X - sx;
            var syi = src[i].Y - sy;
            var dxi = dst[i].X - dx;
            var dyi = dst[i].Y - dy;

            sigmaXX += sxi * dxi;
            sigmaXY += sxi * dyi;
            sigmaYX += syi * dxi;
            sigmaYY += syi * dyi;
            varSrc += sxi * sxi + syi * syi;
        }

        // No spread in the source => scale is undefined.
        if (varSrc < 1e-9)
            return null;

        // Mean rotation angle from the cross/dot covariance.
        var theta = Math.Atan2(sigmaXY - sigmaYX, sigmaXX + sigmaYY);

        // Least-squares scale for a similarity fit.
        var scale = ((sigmaXX + sigmaYY) * Math.Cos(theta) +
                     (sigmaXY - sigmaYX) * Math.Sin(theta)) / varSrc;

        if (!double.IsFinite(scale) || scale < 1e-6 || scale > 1000)
            return null;

        var a = scale * Math.Cos(theta);
        var b = scale * Math.Sin(theta);
        var tx = dx - (a * sx - b * sy);
        var ty = dy - (b * sx + a * sy);

        if (!IsFinite(a) || !IsFinite(b) || !IsFinite(tx) || !IsFinite(ty))
            return null;

        var matrix = new Mat(2, 3, MatType.CV_64FC1);
        matrix.Set(0, 0, a);
        matrix.Set(0, 1, -b);
        matrix.Set(0, 2, tx);
        matrix.Set(1, 0, b);
        matrix.Set(1, 1, a);
        matrix.Set(1, 2, ty);
        return matrix;
    }

    private static bool IsFinite(Point2f p) => float.IsFinite(p.X) && float.IsFinite(p.Y);

    private static bool IsFinite(double v) => double.IsFinite(v);

    private static Mat CropAndResize(Mat frame, DetectedFace face)
    {
        var box = ClampBox(face, frame.Width, frame.Height);

        using var roi = new Mat(frame, box);
        var output = new Mat();
        Cv2.Resize(roi, output, new Size(OutputSize, OutputSize));
        return output;
    }

    private static Rect ClampBox(DetectedFace face, int width, int height)
    {
        // Expand slightly so the crop includes forehead/chin context.
        var expandX = face.Width * 0.10f;
        var expandY = face.Height * 0.10f;

        var x = (int)Math.Clamp(face.X - expandX, 0, Math.Max(0, width - 1));
        var y = (int)Math.Clamp(face.Y - expandY, 0, Math.Max(0, height - 1));
        var w = (int)Math.Clamp(face.Width + expandX * 2, 1, width - x);
        var h = (int)Math.Clamp(face.Height + expandY * 2, 1, height - y);

        return new Rect(x, y, Math.Max(1, w), Math.Max(1, h));
    }

    private static bool IsDegenerate(Mat image)
    {
        if (image is null || image.Empty())
            return true;

        using var gray = new Mat();
        if (image.Channels() == 3)
            Cv2.CvtColor(image, gray, ColorConversionCodes.BGR2GRAY);
        else
            image.CopyTo(gray);

        Cv2.MeanStdDev(gray, out var mean, out var stddev);

        // Uniform (all black/white) output means the transform collapsed.
        return stddev.Val0 < 1.0 || mean.Val0 < 2.0 || mean.Val0 > 253.0;
    }
}
