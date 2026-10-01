using Microsoft.Extensions.Logging;
using Security.Core.Interfaces;

namespace Security.Infrastructure.Services;

/// <summary>
/// Result of one retention sweep — concrete counts, logged to the application
/// log only (never written into the event table, which would make cleanup
/// itself generate more rows to clean up).
/// </summary>
public sealed record RetentionSummary(int EventsDeleted, int SnapshotsDeleted);

/// <summary>
/// Applies the two retention policies:
///  - events older than <c>EventRetentionDays</c> are deleted, together with
///    the snapshot files those deleted events pointed at;
///  - snapshot files older than <c>SnapshotRetentionDays</c> are deleted even
///    when their event row still exists (the row then reports "No snapshot
///    stored." / missing file honestly in the UI).
///
/// GUARANTEES:
///  - never deletes events inside the retention window;
///  - snapshot deletion goes through <see cref="ISnapshotStore.TryDelete"/>,
///    which refuses any path outside <c>data/events/</c>;
///  - performs no I/O beyond local disk and the local database.
/// </summary>
public interface IRetentionService
{
    /// <summary>Run one sweep with the given retention windows. Safe to call repeatedly.</summary>
    Task<RetentionSummary> RunAsync(CancellationToken cancellationToken = default);
}

public sealed class RetentionService : IRetentionService
{
    private readonly ISecurityEventRepository _events;
    private readonly ISnapshotStore _snapshots;
    private readonly ISettingsService _settings;
    private readonly ILogger<RetentionService>? _logger;

    public RetentionService(
        ISecurityEventRepository events,
        ISnapshotStore snapshots,
        ISettingsService settings,
        ILogger<RetentionService>? logger = null)
    {
        _events = events;
        _snapshots = snapshots;
        _settings = settings;
        _logger = logger;
    }

    public async Task<RetentionSummary> RunAsync(CancellationToken cancellationToken = default)
    {
        var settings = _settings.Current;
        var now = DateTime.UtcNow;

        // 1. Expired events (and the snapshot paths they referenced).
        var eventCutoff = now - TimeSpan.FromDays(Math.Max(1, settings.EventRetentionDays));
        var deletedEvents = await _events.DeleteOlderThanAsync(eventCutoff, cancellationToken)
            .ConfigureAwait(false);

        var snapshotsFromEvents = 0;
        foreach (var securityEvent in deletedEvents)
        {
            if (securityEvent.SnapshotPath is not null
                && _snapshots.TryDelete(securityEvent.SnapshotPath, cancellationToken))
            {
                snapshotsFromEvents++;
            }
        }

        // 2. Orphaned / standalone snapshot files past their own window.
        //    These are files under data/events/ whose retention clock is the
        //    file's write time, independent of any event row.
        var snapshotCutoffDays = settings.SnapshotRetentionDays;
        var orphanedSnapshots = _snapshots.DeleteOlderThan(
            TimeSpan.FromDays(Math.Max(1, snapshotCutoffDays)), cancellationToken);

        var summary = new RetentionSummary(deletedEvents.Count, snapshotsFromEvents + orphanedSnapshots);

        // App log only — cleanup must not create new security events.
        if (summary.EventsDeleted > 0 || summary.SnapshotsDeleted > 0)
        {
            _logger?.LogInformation(
                "Retention sweep: removed {Events} event(s) older than {EventDays} day(s) and "
                + "{Snapshots} snapshot(s) older than {SnapshotDays} day(s)",
                summary.EventsDeleted, settings.EventRetentionDays,
                summary.SnapshotsDeleted, settings.SnapshotRetentionDays);
        }

        return summary;
    }
}
