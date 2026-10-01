using Security.Core.Enums;

namespace Security.Core.Models;

/// <summary>
/// Result of a single recognition attempt.
/// </summary>
public sealed class FaceRecognitionResult
{
    public RecognitionStatus Status { get; init; }

    /// <summary>True only when <see cref="Status"/> is <see cref="RecognitionStatus.Known"/>.</summary>
    public bool IsMatch { get; init; }

    /// <summary>
    /// Confidence 0..1 derived from the similarity and the configured threshold.
    /// For <see cref="RecognitionStatus.UnableToDetermine"/> this is 0.
    /// </summary>
    public double Confidence { get; init; }

    /// <summary>Raw cosine similarity 0..1 between the observed face and the profile.</summary>
    public double Similarity { get; init; }

    public int? ProfileId { get; init; }

    public DateTime Timestamp { get; init; } = DateTime.UtcNow;

    /// <summary>Human readable reason. Empty for a normal verdict.</summary>
    public string Reason { get; init; } = string.Empty;

    public static FaceRecognitionResult UnableToDetermine(string reason) => new()
    {
        Status = RecognitionStatus.UnableToDetermine,
        IsMatch = false,
        Confidence = 0,
        Similarity = 0,
        Reason = reason,
        Timestamp = DateTime.UtcNow,
    };

    public static FaceRecognitionResult Create(
        RecognitionStatus status,
        double similarity,
        double threshold,
        int? profileId,
        DateTime timestamp)
    {
        var match = status == RecognitionStatus.Known;
        // Confidence is 0 at the threshold and rises toward 1 as similarity rises,
        // so operators can rank alerts even when everything crosses the bar.
        var confidence = match
            ? Math.Clamp((similarity - threshold) / Math.Max(1e-6, 1.0 - threshold), 0, 1)
            : 0;

        return new FaceRecognitionResult
        {
            Status = status,
            IsMatch = match,
            Confidence = confidence,
            Similarity = similarity,
            ProfileId = profileId,
            Timestamp = timestamp,
        };
    }
}
