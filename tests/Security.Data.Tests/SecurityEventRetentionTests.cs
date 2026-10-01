using Security.Core.Entities;
using Security.Core.Enums;
using Security.Core.Services;
using Security.Data.Repositories;
using Xunit;

namespace Security.Data.Tests;

/// <summary>
/// Retention's delete path: only events strictly older than the cutoff are
/// removed, the removed rows come back (so snapshot files can be cleaned),
/// and live events are never touched.
/// </summary>
public class SecurityEventRetentionTests : IDisposable
{
    private readonly TestDatabase _db = new();
    private readonly SecurityEventRepository _events;

    public SecurityEventRetentionTests()
        => _events = new SecurityEventRepository(_db.Factory);

    public void Dispose() => _db.Dispose();

    private Task AddAsync(DateTime timestampUtc, string description = "event")
        => _events.AddAsync(SecurityEventFactory.Create(
            SecurityEventType.FaceDetected, SecurityEventResult.Info, description,
            null, timestampUtc));

    [Fact]
    public async Task DeleteOlderThan_removes_only_events_past_the_cutoff()
    {
        var now = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        await AddAsync(now.AddDays(-31), "expired-1");
        await AddAsync(now.AddDays(-45), "expired-2");
        await AddAsync(now.AddDays(-1), "fresh");

        var deleted = await _events.DeleteOlderThanAsync(now.AddDays(-30));

        Assert.Equal(2, deleted.Count);
        Assert.All(deleted, e => Assert.True(e.Timestamp < now.AddDays(-30)));

        var remaining = await _events.GetRecentAsync(10);
        Assert.Single(remaining);
        Assert.Equal("fresh", remaining[0].Description);
    }

    [Fact]
    public async Task DeleteOlderThan_on_an_empty_window_deletes_nothing_and_returns_empty()
    {
        await AddAsync(DateTime.UtcNow, "kept");

        var deleted = await _events.DeleteOlderThanAsync(DateTime.UtcNow.AddDays(-30));

        Assert.Empty(deleted);
        Assert.Equal(1, await _events.CountAsync());
    }

    [Fact]
    public async Task DeleteOlderThan_with_nothing_old_returns_empty()
    {
        var deleted = await _events.DeleteOlderThanAsync(DateTime.UtcNow.AddDays(-30));

        Assert.Empty(deleted);
        Assert.Equal(0, await _events.CountAsync());
    }

    [Fact]
    public async Task DeleteOlderThan_is_idempotent()
    {
        var now = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        await AddAsync(now.AddDays(-40));

        var first = await _events.DeleteOlderThanAsync(now.AddDays(-30));
        var second = await _events.DeleteOlderThanAsync(now.AddDays(-30));

        Assert.Single(first);
        Assert.Empty(second);
    }

    [Fact]
    public async Task Events_on_the_cutoff_boundary_are_kept()
    {
        var cutoff = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        await AddAsync(cutoff, "exactly-at-cutoff");      // not strictly older
        await AddAsync(cutoff.AddSeconds(-1), "strictly-older");

        var deleted = await _events.DeleteOlderThanAsync(cutoff);

        Assert.Single(deleted);
        Assert.Equal("strictly-older", deleted[0].Description);

        var remaining = await _events.GetRecentAsync(10);
        Assert.Equal("exactly-at-cutoff", remaining[0].Description);
    }

    [Fact]
    public async Task DeleteOlderThan_returns_the_snapshot_paths_of_removed_rows()
    {
        var now = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var expired = SecurityEventFactory.Create(
            SecurityEventType.UnknownFaceDetected, SecurityEventResult.Warning,
            "unknown", 0.4, now.AddDays(-40));
        expired.SnapshotPath = "data/events/unknown_20260901_090000_deadbeef.jpg";
        await _events.AddAsync(expired);

        var deleted = await _events.DeleteOlderThanAsync(now.AddDays(-30));

        Assert.Single(deleted);
        Assert.Equal("data/events/unknown_20260901_090000_deadbeef.jpg", deleted[0].SnapshotPath);
    }
}
