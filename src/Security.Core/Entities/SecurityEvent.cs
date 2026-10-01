namespace Security.Core.Entities;

using Security.Core.Enums;

/// <summary>
/// A persisted security event. Contains NO biometric data.
/// </summary>
public class SecurityEvent
{
    public int Id { get; set; }

    public SecurityEventType EventType { get; set; }

    public SecurityEventResult Result { get; set; }

    /// <summary>Optional confidence 0..1. Null for events that carry no confidence.</summary>
    public double? Confidence { get; set; }

    public DateTime Timestamp { get; set; } = DateTime.UtcNow;

    public string Description { get; set; } = string.Empty;
}
