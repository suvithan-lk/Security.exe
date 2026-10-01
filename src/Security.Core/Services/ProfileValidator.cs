using Security.Core.Entities;
using Security.Core.Models;

namespace Security.Core.Services;

/// <summary>
/// Validation rules shared by the enrollment UI, the enrollment service, and tests.
/// </summary>
public static class ProfileValidator
{
    public const int MaxDisplayNameLength = 64;

    public static bool IsValidDisplayName(string? name)
        => !string.IsNullOrWhiteSpace(name) && name.Trim().Length is > 0 and <= MaxDisplayNameLength;

    public static string SanitizeDisplayName(string? name, string fallback = "Default User")
    {
        if (string.IsNullOrWhiteSpace(name))
            return fallback;

        var trimmed = name.Trim();
        return trimmed.Length <= MaxDisplayNameLength ? trimmed : trimmed[..MaxDisplayNameLength];
    }

    /// <summary>
    /// A stored embedding is usable only if it decrypts to a non-empty,
    /// finite, uniform-length vector.
    /// </summary>
    public static bool IsValidEmbedding(IReadOnlyList<float>? embedding)
    {
        if (embedding is null || embedding.Count == 0)
            return false;

        foreach (var value in embedding)
        {
            if (!float.IsFinite(value))
                return false;
        }

        return true;
    }

    /// <summary>
    /// Decide whether a detected face is large enough to embed, given the
    /// configured minimums.
    /// </summary>
    public static bool IsFaceLargeEnough(double faceWidthPx, double frameWidth, RecognitionOptions options)
    {
        if (options is null)
            return false;

        if (faceWidthPx < options.MinimumFaceSize)
            return false;

        if (frameWidth <= 0)
            return false;

        var ratio = faceWidthPx / frameWidth;
        return ratio >= options.MinimumFaceRatio;
    }

    /// <summary>Validate a batch of collected embeddings before persisting.</summary>
    public static bool CanFinalize(IReadOnlyList<float[]>? samples, int requiredSamples)
    {
        if (samples is null || samples.Count < requiredSamples)
            return false;

        return samples.All(IsValidEmbedding);
    }
}
