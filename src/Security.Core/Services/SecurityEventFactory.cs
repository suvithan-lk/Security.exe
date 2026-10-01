using Security.Core.Entities;
using Security.Core.Enums;

namespace Security.Core.Services;

/// <summary>
/// Factory for security events. Pure — used by both the event service and tests.
/// </summary>
public static class SecurityEventFactory
{
    public static SecurityEvent Create(
        SecurityEventType eventType,
        SecurityEventResult result,
        string description,
        double? confidence = null,
        DateTime? timestampUtc = null,
        SessionState? sessionState = null,
        string? snapshotPath = null)
    {
        return new SecurityEvent
        {
            EventType = eventType,
            Result = result,
            Description = string.IsNullOrWhiteSpace(description) ? string.Empty : description.Trim(),
            Confidence = NormalizeConfidence(confidence),
            Timestamp = timestampUtc ?? DateTime.UtcNow,
            SessionState = sessionState,
            SnapshotPath = NormalizeSnapshotPath(snapshotPath),
        };
    }

    /// <summary>
    /// Snapshots are local, relative paths only. Anything that looks like a
    /// URL, a UNC path, or escapes the data folder is dropped rather than
    /// stored — a persisted path must never become a network fetch.
    /// </summary>
    public static string? NormalizeSnapshotPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        var trimmed = path.Trim();

        if (trimmed.Contains("://", StringComparison.Ordinal) ||
            trimmed.StartsWith(@"\\", StringComparison.Ordinal) ||
            trimmed.Length > 260)
        {
            return null;
        }

        return trimmed;
    }

    /// <summary>Confidence must be 0..1 or null. Anything else is null.</summary>
    public static double? NormalizeConfidence(double? confidence)
    {
        if (confidence is null)
            return null;

        if (!double.IsFinite(confidence.Value))
            return null;

        return Math.Clamp(confidence.Value, 0, 1);
    }

    /// <summary>Map a recognition status onto the event result shown in the UI.</summary>
    public static SecurityEventResult ResultFor(Enums.RecognitionStatus status) => status switch
    {
        Enums.RecognitionStatus.Known => SecurityEventResult.Known,
        Enums.RecognitionStatus.Unknown => SecurityEventResult.Unknown,
        _ => SecurityEventResult.Info,
    };
}
