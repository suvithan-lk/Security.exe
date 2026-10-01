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

    /// <summary>
    /// Windows session state when the event fired (Locked/Unlocked/…).
    /// Nullable: events recorded before Phase 3, or before the session probe
    /// has run, carry no session state rather than a guessed one.
    /// Stored as a string via EF conversion; contains no credentials.
    /// </summary>
    public SessionState? SessionState { get; set; }

    /// <summary>
    /// Relative path of an optional snapshot for this event
    /// (e.g. <c>data/events/unknown_20261001_095012_ab12cd34.jpg</c>).
    /// Null when snapshots are disabled or none was taken. Never a URL —
    /// snapshots are local-only and never uploaded.
    /// </summary>
    public string? SnapshotPath { get; set; }
}
