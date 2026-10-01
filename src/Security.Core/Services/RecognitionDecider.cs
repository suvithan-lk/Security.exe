using Security.Core.Enums;
using Security.Core.Models;

namespace Security.Core.Services;

/// <summary>
/// Pure decision logic for turning a similarity score into a verdict.
/// Kept free of I/O so it is trivially unit testable.
/// </summary>
public static class RecognitionDecider
{
    public const double DefaultThreshold = 0.60;

    /// <summary>
    /// Clamp and validate a threshold. Values outside (0,1] fall back to the default.
    /// </summary>
    public static double NormalizeThreshold(double threshold)
        => double.IsFinite(threshold) && threshold > 0.0 && threshold <= 1.0
            ? threshold
            : DefaultThreshold;

    /// <summary>
    /// Decide between KNOWN and UNKNOWN. Callers must have already ruled out
    /// the "unable to determine" conditions (no face / multiple faces / no model).
    /// </summary>
    public static RecognitionStatus Decide(double similarity, double threshold)
    {
        var t = NormalizeThreshold(threshold);
        var s = double.IsFinite(similarity) ? Math.Clamp(similarity, 0, 1) : 0;
        return s >= t ? RecognitionStatus.Known : RecognitionStatus.Unknown;
    }

    /// <summary>
    /// Cosine similarity between two equal-length vectors. Returns 0 for any
    /// malformed input rather than throwing — recognition must not crash.
    /// </summary>
    public static double CosineSimilarity(IReadOnlyList<float> a, IReadOnlyList<float> b)
    {
        if (a is null || b is null || a.Count == 0 || a.Count != b.Count)
            return 0;

        double dot = 0, na = 0, nb = 0;
        for (var i = 0; i < a.Count; i++)
        {
            var x = a[i];
            var y = b[i];
            if (!float.IsFinite(x) || !float.IsFinite(y))
                return 0;

            dot += x * y;
            na += x * x;
            nb += y * y;
        }

        if (na <= 0 || nb <= 0)
            return 0;

        var value = dot / (Math.Sqrt(na) * Math.Sqrt(nb));
        return double.IsFinite(value) ? Math.Clamp(value, 0, 1) : 0;
    }

    /// <summary>L2-normalize a vector. Zero vectors are returned unchanged.</summary>
    public static float[] L2Normalize(IReadOnlyList<float> vector)
    {
        var result = new float[vector.Count];
        double sum = 0;
        for (var i = 0; i < vector.Count; i++)
        {
            var value = float.IsFinite(vector[i]) ? vector[i] : 0f;
            result[i] = value;
            sum += value * (double)value;
        }

        var norm = Math.Sqrt(sum);
        if (norm <= 1e-12)
            return result;

        for (var i = 0; i < result.Length; i++)
            result[i] = (float)(result[i] / norm);

        return result;
    }

    /// <summary>
    /// Average several same-length embeddings then L2-normalize the mean.
    /// Used to build one stable profile representation from many samples.
    /// </summary>
    public static float[] AverageAndNormalize(IReadOnlyList<float[]> embeddings)
    {
        if (embeddings is null || embeddings.Count == 0)
            return Array.Empty<float>();

        var dim = embeddings[0].Length;
        if (dim == 0)
            return Array.Empty<float>();

        var acc = new double[dim];
        foreach (var e in embeddings)
        {
            if (e is null || e.Length != dim)
                continue;

            for (var i = 0; i < dim; i++)
                acc[i] += float.IsFinite(e[i]) ? e[i] : 0;
        }

        var mean = new float[dim];
        for (var i = 0; i < dim; i++)
            mean[i] = (float)(acc[i] / embeddings.Count);

        return L2Normalize(mean);
    }
}
