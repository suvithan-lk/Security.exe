using Security.Core.Entities;
using Security.Core.Enums;
using Security.Core.Interfaces;
using Security.Core.Services;
using Security.Data.Repositories;
using Xunit;

namespace Security.Data.Tests;

public class SecurityEventRepositoryTests : IDisposable
{
    private readonly TestDatabase _db = new();
    private readonly SecurityEventRepository _events;

    public SecurityEventRepositoryTests()
        => _events = new SecurityEventRepository(_db.Factory);

    public void Dispose() => _db.Dispose();

    private Task AddAsync(SecurityEventType type, DateTime timestampUtc, double? confidence = null)
        => _events.AddAsync(SecurityEventFactory.Create(
            type, SecurityEventResult.Info, $"{type} occurred", confidence, timestampUtc));

    [Fact]
    public async Task Added_events_are_counted()
    {
        Assert.Equal(0, await _events.CountAsync());

        await AddAsync(SecurityEventType.ApplicationStarted, DateTime.UtcNow);
        await AddAsync(SecurityEventType.CameraStarted, DateTime.UtcNow);

        Assert.Equal(2, await _events.CountAsync());
    }

    [Fact]
    public async Task GetRecent_returns_newest_first()
    {
        var baseTime = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        await AddAsync(SecurityEventType.CameraStarted, baseTime);
        await AddAsync(SecurityEventType.FaceDetected, baseTime.AddMinutes(5));
        await AddAsync(SecurityEventType.ApplicationStopped, baseTime.AddMinutes(10));

        var recent = await _events.GetRecentAsync(10);

        Assert.Equal(3, recent.Count);
        Assert.Equal(SecurityEventType.ApplicationStopped, recent[0].EventType);
        Assert.Equal(SecurityEventType.CameraStarted, recent[2].EventType);
    }

    [Fact]
    public async Task GetRecent_respects_the_limit()
    {
        var baseTime = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        for (var i = 0; i < 25; i++)
            await AddAsync(SecurityEventType.FaceDetected, baseTime.AddSeconds(i));

        var recent = await _events.GetRecentAsync(10);

        Assert.Equal(10, recent.Count);
    }

    [Fact]
    public async Task Query_filters_by_event_type()
    {
        var baseTime = new DateTime(2026, 2, 1, 8, 0, 0, DateTimeKind.Utc);
        await AddAsync(SecurityEventType.CameraStarted, baseTime);
        await AddAsync(SecurityEventType.UnknownFaceDetected, baseTime.AddSeconds(1));
        await AddAsync(SecurityEventType.UnknownFaceDetected, baseTime.AddSeconds(2));

        var filter = new SecurityEventTypeFilter { EventType = SecurityEventType.UnknownFaceDetected };
        var rows = await _events.QueryAsync(filter, 100);

        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.Equal(SecurityEventType.UnknownFaceDetected, r.EventType));
    }

    [Fact]
    public async Task Query_filters_by_inclusive_date_range()
    {
        var day1 = new DateTime(2026, 3, 1, 9, 0, 0, DateTimeKind.Utc);
        var day2 = new DateTime(2026, 3, 2, 9, 0, 0, DateTimeKind.Utc);
        var day3 = new DateTime(2026, 3, 3, 9, 0, 0, DateTimeKind.Utc);
        await AddAsync(SecurityEventType.FaceDetected, day1);
        await AddAsync(SecurityEventType.FaceDetected, day2);
        await AddAsync(SecurityEventType.FaceDetected, day3);

        var rows = await _events.QueryAsync(
            new SecurityEventTypeFilter
            {
                FromUtc = day2,
                ToUtc = day3,
            },
            100);

        Assert.Equal(2, rows.Count);
        Assert.DoesNotContain(rows, r => r.Timestamp < day2);
        Assert.DoesNotContain(rows, r => r.Timestamp > day3);
    }

    [Fact]
    public async Task Query_combines_type_and_range_filters()
    {
        var now = new DateTime(2026, 4, 1, 10, 0, 0, DateTimeKind.Utc);
        await AddAsync(SecurityEventType.CameraStarted, now);
        await AddAsync(SecurityEventType.UnknownFaceDetected, now);
        await AddAsync(SecurityEventType.UnknownFaceDetected, now.AddDays(30));

        var rows = await _events.QueryAsync(
            new SecurityEventTypeFilter
            {
                EventType = SecurityEventType.UnknownFaceDetected,
                FromUtc = now,
                ToUtc = now.AddDays(1),
            },
            100);

        Assert.Single(rows);
        Assert.Equal(SecurityEventType.UnknownFaceDetected, rows[0].EventType);
    }

    [Fact]
    public async Task Query_orders_newest_first()
    {
        var baseTime = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc);
        await AddAsync(SecurityEventType.FaceDetected, baseTime.AddHours(1));
        await AddAsync(SecurityEventType.FaceDetected, baseTime.AddHours(3));
        await AddAsync(SecurityEventType.FaceDetected, baseTime.AddHours(2));

        var rows = await _events.QueryAsync(new SecurityEventTypeFilter(), 100);

        Assert.Equal(3, rows.Count);
        Assert.True(rows[0].Timestamp > rows[1].Timestamp);
        Assert.True(rows[1].Timestamp > rows[2].Timestamp);
    }

    [Fact]
    public async Task Clear_empties_the_log_without_failing_on_empty_store()
    {
        await _events.ClearAsync();
        Assert.Equal(0, await _events.CountAsync());

        await AddAsync(SecurityEventType.CameraStarted, DateTime.UtcNow);
        await AddAsync(SecurityEventType.CameraStopped, DateTime.UtcNow);
        Assert.Equal(2, await _events.CountAsync());

        await _events.ClearAsync();
        Assert.Equal(0, await _events.CountAsync());
    }

    [Fact]
    public async Task Stored_events_carry_no_biometric_fields()
    {
        await _events.AddAsync(SecurityEventFactory.Create(
            SecurityEventType.KnownFaceDetected,
            SecurityEventResult.Known,
            "Face matched the enrolled profile.",
            confidence: 0.91,
            timestampUtc: DateTime.UtcNow));

        var stored = (await _events.GetRecentAsync(1))[0];

        var propertyNames = typeof(SecurityEvent).GetProperties().Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
        Assert.DoesNotContain("Embedding", propertyNames);
        Assert.DoesNotContain("Image", propertyNames);
        Assert.DoesNotContain("Template", propertyNames);
        Assert.DoesNotContain("Frame", propertyNames);
        Assert.Equal(0.91, stored.Confidence!.Value, 6);
    }
}

public class ApplicationSettingRepositoryTests : IDisposable
{
    private readonly TestDatabase _db = new();
    private readonly ApplicationSettingRepository _settings;

    public ApplicationSettingRepositoryTests()
        => _settings = new ApplicationSettingRepository(_db.Factory);

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task Missing_key_returns_null()
    {
        Assert.Null(await _settings.GetAsync("does.not.exist"));
    }

    [Fact]
    public async Task Set_then_get_round_trips()
    {
        await _settings.SetAsync("recognition.threshold", "0.72");
        Assert.Equal("0.72", await _settings.GetAsync("recognition.threshold"));
    }

    [Fact]
    public async Task Set_overwrites_an_existing_key_instead_of_duplicating()
    {
        await _settings.SetAsync("k", "v1");
        await _settings.SetAsync("k", "v2");

        Assert.Equal("v2", await _settings.GetAsync("k"));

        var all = await _settings.GetAllAsync();
        Assert.Equal(1, all.Count(k => k.Key == "k"));
    }

    [Fact]
    public async Task Empty_string_is_a_valid_value()
    {
        await _settings.SetAsync("camera.name", string.Empty);
        Assert.Equal(string.Empty, await _settings.GetAsync("camera.name"));
    }

    [Fact]
    public async Task GetAll_returns_every_stored_key()
    {
        await _settings.SetAsync("a", "1");
        await _settings.SetAsync("b", "2");
        await _settings.SetAsync("c", "3");

        var all = await _settings.GetAllAsync();

        Assert.Equal(3, all.Count);
        Assert.Equal("1", all["a"]);
        Assert.Equal("2", all["b"]);
        Assert.Equal("3", all["c"]);
    }
}
