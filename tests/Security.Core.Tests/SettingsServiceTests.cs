using Security.Core.Interfaces;
using Security.Core.Models;
using Security.Infrastructure.Services;
using Xunit;

namespace Security.Core.Tests;

/// <summary>In-memory stand-in so settings tests never touch the real database.</summary>
internal sealed class FakeSettingRepository : IApplicationSettingRepository
{
    private readonly Dictionary<string, string> _store = new();

    /// <summary>When set, every write throws — used to prove saves never crash.</summary>
    public bool FailWrites { get; set; }

    public IReadOnlyDictionary<string, string> Snapshot => _store;

    public Task<string?> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        _store.TryGetValue(key, out var value);
        return Task.FromResult(value);
    }

    public Task SetAsync(string key, string value, CancellationToken cancellationToken = default)
    {
        if (FailWrites)
            throw new InvalidOperationException("simulated store failure");

        _store[key] = value;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyDictionary<string, string>> GetAllAsync(CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string>(_store));
}

public class SettingsServiceTests
{
    private static (SettingsService Service, FakeSettingRepository Repository) Create(
        AppSettings? app = null,
        RecognitionOptions? recognition = null)
    {
        var repository = new FakeSettingRepository();
        var service = new SettingsService(
            app ?? new AppSettings(),
            recognition ?? new RecognitionOptions(),
            repository);
        return (service, repository);
    }

    [Fact]
    public void Defaults_match_the_documented_privacy_posture()
    {
        var (service, _) = Create();

        Assert.True(service.Current.StoreSecurityEvents);  // ON by default
        Assert.False(service.Current.StoreSnapshots);      // OFF by default
        Assert.True(service.Current.RecognitionEnabled);
        Assert.False(service.Current.LivenessCheckEnabled);
        Assert.False(service.Current.StartWithWindows);
        Assert.True(service.Current.MinimizeToTray);   // Phase 3: close-to-tray ON by default
        Assert.Equal(0.60, service.Recognition.Threshold, 6);
    }

    [Fact]
    public async Task SaveAsync_updates_current_and_persists()
    {
        var (service, repository) = Create();

        var settings = service.Current;
        settings.RecognitionEnabled = false;
        settings.StoreSnapshots = true;
        await service.SaveAsync(settings);

        Assert.False(service.Current.RecognitionEnabled);
        Assert.True(service.Current.StoreSnapshots);
        Assert.Contains("app.settings.v1", repository.Snapshot.Keys);
    }

    [Fact]
    public async Task SaveAsync_clones_so_later_caller_mutation_cannot_leak()
    {
        var (service, _) = Create();

        var settings = service.Current;
        settings.StoreSnapshots = true;
        await service.SaveAsync(settings);

        // Mutating the original object afterwards must not change what is stored.
        settings.StoreSnapshots = false;

        Assert.True(service.Current.StoreSnapshots);
    }

    [Theory]
    [InlineData(0.00)]     // out of range -> default
    [InlineData(-1.0)]
    [InlineData(5.0)]
    public async Task Invalid_threshold_is_sanitized_on_save(double threshold)
    {
        var (service, _) = Create();

        var options = service.Recognition;
        options.Threshold = threshold;
        await service.SaveRecognitionAsync(options);

        Assert.Equal(RecognitionDeciderDefault, service.Recognition.Threshold, 6);
    }

    [Theory]
    [InlineData(0.65)]
    [InlineData(0.35)]
    [InlineData(1.0)]
    public async Task Valid_threshold_is_kept(double threshold)
    {
        var (service, _) = Create();

        var options = service.Recognition;
        options.Threshold = threshold;
        await service.SaveRecognitionAsync(options);

        Assert.Equal(threshold, service.Recognition.Threshold, 6);
    }

    [Fact]
    public async Task Out_of_range_numeric_settings_are_clamped()
    {
        var (service, _) = Create();

        var options = service.Recognition;
        options.DetectionFps = 999;
        options.RecognitionCooldownSeconds = -20;
        options.MinimumFaceSize = 1;
        options.EnrollmentSampleCount = 0;
        options.StableFaceFrames = 0;

        await service.SaveRecognitionAsync(options);

        Assert.InRange(service.Recognition.DetectionFps, 1, 30);
        Assert.InRange(service.Recognition.RecognitionCooldownSeconds, 0, 600);
        Assert.InRange(service.Recognition.MinimumFaceSize, 32, 2000);
        Assert.InRange(service.Recognition.EnrollmentSampleCount, 5, 100);
        Assert.InRange(service.Recognition.StableFaceFrames, 1, 30);
    }

    [Fact]
    public async Task Brightness_bounds_are_ordered_after_sanitize()
    {
        var (service, _) = Create();

        var options = service.Recognition;
        options.MinimumBrightness = 200;
        options.MaximumBrightness = 10; // inverted on purpose

        await service.SaveRecognitionAsync(options);

        Assert.True(service.Recognition.MaximumBrightness > service.Recognition.MinimumBrightness);
    }

    [Fact]
    public async Task LoadAsync_restores_persisted_values()
    {
        var (first, repository) = Create();
        var settings = first.Current;
        settings.RecognitionEnabled = false;
        settings.StartWithWindows = true;
        await first.SaveAsync(settings);

        // Fresh service over the same backing store = application restart.
        var second = new SettingsService(new AppSettings(), new RecognitionOptions(), repository);
        await second.LoadAsync();

        Assert.False(second.Current.RecognitionEnabled);
        Assert.True(second.Current.StartWithWindows);
    }

    [Fact]
    public async Task LoadAsync_tolerates_corrupt_store_contents()
    {
        var repository = new FakeSettingRepository();
        await repository.SetAsync("app.settings.v1", "{ not valid json ]");

        var service = new SettingsService(new AppSettings(), new RecognitionOptions(), repository);

        // Must not throw and must keep defaults.
        await service.LoadAsync();

        Assert.True(service.Current.StoreSecurityEvents);
        Assert.Equal(0.60, service.Recognition.Threshold, 6);
    }

    [Fact]
    public async Task LoadAsync_tolerates_repository_failure()
    {
        var repository = new FakeSettingRepository { FailWrites = true };
        var service = new SettingsService(new AppSettings(), new RecognitionOptions(), repository);

        // Reads don't throw in this fake, but Save must not propagate either.
        await service.SaveRecognitionAsync(new RecognitionOptions { Threshold = 0.7 });
        await service.LoadAsync();

        Assert.True(service.Recognition.Threshold is > 0 and <= 1);
    }

    [Fact]
    public async Task SettingsChanged_is_raised_on_save()
    {
        var (service, _) = Create();
        var raised = 0;
        service.SettingsChanged += (_, _) => raised++;

        await service.SaveAsync(service.Current);
        await service.SaveRecognitionAsync(service.Recognition);

        Assert.True(raised >= 2);
    }

    [Fact]
    public async Task Null_settings_are_rejected_rather_than_silently_accepted()
    {
        var (service, _) = Create();
        await Assert.ThrowsAsync<ArgumentNullException>(() => service.SaveAsync(null!));
        await Assert.ThrowsAsync<ArgumentNullException>(() => service.SaveRecognitionAsync(null!));
    }

    // --- Camera resolution (persisted in the settings JSON blob) -----------

    [Fact]
    public void Camera_resolution_defaults_to_the_documented_1280x720()
    {
        var (service, _) = Create();

        Assert.Equal(1280, service.Current.CameraWidth);
        Assert.Equal(720, service.Current.CameraHeight);
    }

    [Fact]
    public async Task Camera_resolution_survives_a_save_and_restart_roundtrip()
    {
        var (first, repository) = Create();
        var settings = first.Current;
        settings.CameraWidth = 640;
        settings.CameraHeight = 480;
        await first.SaveAsync(settings);

        // Fresh service over the same backing store = application restart.
        var second = new SettingsService(new AppSettings(), new RecognitionOptions(), repository);
        await second.LoadAsync();

        Assert.Equal(640, second.Current.CameraWidth);
        Assert.Equal(480, second.Current.CameraHeight);
    }

    [Theory]
    [InlineData(0)]        // a device cannot open a zero-size capture
    [InlineData(-1920)]
    [InlineData(99999)]    // beyond any camera this hardware will ever expose
    public async Task Camera_width_is_clamped_to_a_sane_capture_size(int requested)
    {
        var (service, _) = Create();

        var settings = service.Current;
        settings.CameraWidth = requested;
        await service.SaveAsync(settings);

        Assert.InRange(service.Current.CameraWidth, 160, 3840);
    }

    [Fact]
    public async Task Camera_height_is_clamped_to_a_sane_capture_size()
    {
        var (service, _) = Create();

        var settings = service.Current;
        settings.CameraHeight = 0;
        await service.SaveAsync(settings);

        Assert.InRange(service.Current.CameraHeight, 120, 2160);
    }

    private const double RecognitionDeciderDefault = 0.60;
}
