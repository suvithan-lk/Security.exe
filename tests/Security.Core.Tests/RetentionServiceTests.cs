using Security.Core.Entities;
using Security.Core.Enums;
using Security.Core.Interfaces;
using Security.Core.Models;
using Security.Core.Services;
using Security.Infrastructure.Services;
using Xunit;

namespace Security.Core.Tests;

/// <summary>
/// Retention policy: expired events + their snapshot files are deleted,
/// snapshot files have their own age window, nothing inside either window is
/// ever touched, and the sweep never writes new event rows (its summary is
/// logged, not recorded).
/// </summary>
public class RetentionServiceTests
{
    private class FakeEventRepository : ISecurityEventRepository
    {
        public List<SecurityEvent> Stored { get; } = new();

        public DateTime? LastCutoff { get; private set; }

        public Task AddAsync(SecurityEvent securityEvent, CancellationToken cancellationToken = default)
        {
            Stored.Add(securityEvent);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<SecurityEvent>> GetRecentAsync(int count = 100, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<SecurityEvent>>(
                Stored.OrderByDescending(e => e.Timestamp).Take(count).ToList());

        public Task<IReadOnlyList<SecurityEvent>> QueryAsync(
            SecurityEventTypeFilter filter, int limit, CancellationToken cancellationToken = default)
            => GetRecentAsync(limit, cancellationToken);

        public Task<int> CountAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(Stored.Count);

        public virtual Task<IReadOnlyList<SecurityEvent>> DeleteOlderThanAsync(
            DateTime cutoffUtc, CancellationToken cancellationToken = default)
        {
            LastCutoff = cutoffUtc;
            var expired = Stored.Where(e => e.Timestamp < cutoffUtc).ToList();
            foreach (var item in expired)
                Stored.Remove(item);

            return Task.FromResult<IReadOnlyList<SecurityEvent>>(expired);
        }

        public Task ClearAsync(CancellationToken cancellationToken = default)
        {
            Stored.Clear();
            return Task.CompletedTask;
        }
    }

    private sealed class FakeSnapshotStore : ISnapshotStore
    {
        public List<string> Files { get; } = new();

        public List<string> Deleted { get; } = new();

        public TimeSpan? LastMaxAge { get; private set; }

        public string DirectoryPath => "data/events";

        public string? Save(OpenCvSharp.Mat frame, CancellationToken cancellationToken = default)
            => throw new NotSupportedException("Retention never captures.");

        public int DeleteOlderThan(TimeSpan maxAge, CancellationToken cancellationToken = default)
        {
            LastMaxAge = maxAge;
            // Simulate files whose age maps to the requested window: remove
            // everything the sweep considers stale.
            var count = Files.Count;
            Deleted.AddRange(Files);
            Files.Clear();
            return count;
        }

        public bool TryDelete(string? relativePath, CancellationToken cancellationToken = default)
        {
            if (relativePath is null || !Files.Remove(relativePath))
                return false;

            Deleted.Add(relativePath);
            return true;
        }
    }

    private sealed class FakeSettingsService : ISettingsService
    {
        public FakeSettingsService(AppSettings? settings = null)
            => Current = settings ?? new AppSettings();

        public AppSettings Current { get; }

        public RecognitionOptions Recognition { get; } = new();

        public event EventHandler? SettingsChanged
        {
            add { }
            remove { }
        }

        public Task LoadAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task SaveRecognitionAsync(RecognitionOptions options, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private static SecurityEvent EventAt(DateTime timestampUtc, string? snapshotPath = null)
        => SecurityEventFactory.Create(
            SecurityEventType.FaceDetected, SecurityEventResult.Info, "event", null, timestampUtc);

    [Fact]
    public async Task Expired_events_are_deleted_and_fresh_ones_kept()
    {
        var events = new FakeEventRepository();
        var now = DateTime.UtcNow;
        events.Stored.Add(EventAt(now.AddDays(-31)));
        events.Stored.Add(EventAt(now.AddDays(-2)));

        var service = new RetentionService(events, new FakeSnapshotStore(), new FakeSettingsService());

        var summary = await service.RunAsync();

        Assert.Equal(1, summary.EventsDeleted);
        var remaining = await events.GetRecentAsync();
        Assert.Single(remaining);
        Assert.True(remaining[0].Timestamp > now.AddDays(-30));
    }

    [Fact]
    public async Task Cutoff_is_computed_from_EventRetentionDays()
    {
        var events = new FakeEventRepository();
        var settings = new AppSettings { EventRetentionDays = 14 };
        var before = DateTime.UtcNow;

        var service = new RetentionService(events, new FakeSnapshotStore(), new FakeSettingsService(settings));
        await service.RunAsync();

        var after = DateTime.UtcNow;
        Assert.NotNull(events.LastCutoff);
        // Cutoff ≈ now - 14 days; assert inside a small tolerance.
        Assert.InRange(
            events.LastCutoff!.Value,
            before.AddDays(-14).AddSeconds(-5),
            after.AddDays(-14).AddSeconds(5));
    }

    [Fact]
    public async Task Snapshot_files_of_deleted_events_are_removed()
    {
        var events = new FakeEventRepository();
        var snapshots = new FakeSnapshotStore();
        var path = "data/events/unknown_20260901_090000_ab12cd34.jpg";
        snapshots.Files.Add(path);

        var expired = SecurityEventFactory.Create(
            SecurityEventType.UnknownFaceDetected, SecurityEventResult.Warning,
            "unknown", 0.4, DateTime.UtcNow.AddDays(-40));
        expired.SnapshotPath = path;
        events.Stored.Add(expired);

        var service = new RetentionService(events, snapshots, new FakeSettingsService());
        var summary = await service.RunAsync();

        Assert.Equal(1, summary.EventsDeleted);
        Assert.Equal(1, summary.SnapshotsDeleted);
        Assert.Contains(path, snapshots.Deleted);
    }

    [Fact]
    public async Task Standalone_snapshot_files_past_their_window_are_deleted()
    {
        var snapshots = new FakeSnapshotStore();
        snapshots.Files.Add("data/events/orphaned.jpg");

        var service = new RetentionService(new FakeEventRepository(), snapshots, new FakeSettingsService());
        await service.RunAsync();

        // The sweep always applies the snapshot window, event or no event.
        Assert.Equal(TimeSpan.FromDays(7), snapshots.LastMaxAge); // default SnapshotRetentionDays
        Assert.Empty(snapshots.Files);
    }

    [Fact]
    public async Task Snapshot_window_follows_SnapshotRetentionDays()
    {
        var snapshots = new FakeSnapshotStore();
        var settings = new AppSettings { SnapshotRetentionDays = 30 };

        var service = new RetentionService(new FakeEventRepository(), snapshots, new FakeSettingsService(settings));
        await service.RunAsync();

        Assert.Equal(TimeSpan.FromDays(30), snapshots.LastMaxAge);
    }

    [Fact]
    public async Task The_sweep_never_writes_new_event_rows()
    {
        var events = new FakeEventRepository();

        var service = new RetentionService(events, new FakeSnapshotStore(), new FakeSettingsService());
        var summary = await service.RunAsync();

        Assert.Equal(0, summary.EventsDeleted);
        Assert.Empty(events.Stored); // cleanup logs, it does not record
    }

    [Fact]
    public async Task A_repository_failure_propagates_instead_of_silently_passing()
    {
        var events = new ThrowingRepository();

        var service = new RetentionService(events, new FakeSnapshotStore(), new FakeSettingsService());

        // The monitor catches and logs this; the service itself must not hide it.
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RunAsync());
    }

    private sealed class ThrowingRepository : FakeEventRepository
    {
        public override Task<IReadOnlyList<SecurityEvent>> DeleteOlderThanAsync(
            DateTime cutoffUtc, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("database gone");
    }
}
