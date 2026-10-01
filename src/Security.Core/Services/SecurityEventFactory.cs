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
        DateTime? timestampUtc = null)
    {
        return new SecurityEvent
        {
            EventType = eventType,
            Result = result,
            Description = string.IsNullOrWhiteSpace(description) ? string.Empty : description.Trim(),
            Confidence = NormalizeConfidence(confidence),
            Timestamp = timestampUtc ?? DateTime.UtcNow,
        };
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
