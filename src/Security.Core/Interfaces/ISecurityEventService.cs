using Security.Core.Entities;
using Security.Core.Enums;

namespace Security.Core.Interfaces;

/// <summary>
/// Records security events (database) and mirrors them to structured logs.
/// Never accepts biometric payloads.
/// </summary>
public interface ISecurityEventService
{
    /// <summary>Most recent events, newest first.</summary>
    Task<IReadOnlyList<SecurityEvent>> GetRecentAsync(int count = 100, CancellationToken cancellationToken = default);

    /// <summary>Filtered query for the Events screen.</summary>
    Task<IReadOnlyList<SecurityEvent>> QueryAsync(
        SecurityEventType? type = null,
        DateTime? fromUtc = null,
        DateTime? toUtc = null,
        int limit = 500,
        CancellationToken cancellationToken = default);

    Task<int> CountAsync(CancellationToken cancellationToken = default);

    Task ClearAsync(CancellationToken cancellationToken = default);

    /// <summary>Record an event. Safe to call from any thread; never throws.</summary>
    /// <param name="sessionState">
    /// Windows session state at the time of the event. Null lets the service
    /// fill in the session service's last known state (or leave it empty when
    /// session monitoring has not produced one yet).
    /// </param>
    /// <param name="snapshotPath">Optional local snapshot path (relative, never a URL).</param>
    Task<SecurityEvent> RecordAsync(
        SecurityEventType eventType,
        SecurityEventResult result,
        string description,
        double? confidence = null,
        CancellationToken cancellationToken = default,
        SessionState? sessionState = null,
        string? snapshotPath = null);

    /// <summary>Raised after an event has been persisted (for live UI updates).</summary>
    event EventHandler<SecurityEvent>? EventRecorded;
}
