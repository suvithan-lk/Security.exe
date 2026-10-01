using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Security.Core.Interfaces;
using Security.Core.Models;

namespace Security.Infrastructure.Services;

/// <summary>
/// Loads defaults from appsettings.json (injected as <see cref="RecognitionOptions"/>
/// / <see cref="AppSettings"/>) and overlays user overrides persisted in SQLite.
/// </summary>
public sealed class SettingsService : ISettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        // Written as camelCase, so reading must match case-insensitively or
        // every persisted override silently falls back to the defaults.
        PropertyNameCaseInsensitive = true,
    };

    private readonly IApplicationSettingRepository _repository;
    private readonly ILogger<SettingsService>? _logger;
    private readonly object _gate = new();

    private AppSettings _current;
    private RecognitionOptions _recognition;

    public SettingsService(
        AppSettings defaults,
        RecognitionOptions recognitionDefaults,
        IApplicationSettingRepository repository,
        ILogger<SettingsService>? logger = null)
    {
        _current = defaults ?? new AppSettings();
        _recognition = recognitionDefaults ?? new RecognitionOptions();
        _repository = repository;
        _logger = logger;
    }

    public AppSettings Current
    {
        get { lock (_gate) return Clone(_current); }
    }

    public RecognitionOptions Recognition
    {
        get { lock (_gate) return CloneRecognition(_recognition); }
    }

    public event EventHandler? SettingsChanged;

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var all = await _repository.GetAllAsync(cancellationToken);

            lock (_gate)
            {
                if (all.TryGetValue(AppSettingsKey, out var appJson) && !string.IsNullOrWhiteSpace(appJson))
                {
                    var loaded = Deserialize<AppSettings>(appJson);
                    if (loaded is not null)
                        _current = SanitizeApp(loaded);
                }

                if (all.TryGetValue(RecognitionKey, out var recJson) && !string.IsNullOrWhiteSpace(recJson))
                {
                    var loadedRec = Deserialize<RecognitionOptions>(recJson);
                    if (loadedRec is not null)
                        _recognition = Sanitize(loadedRec);
                }

                // Single-value convenience keys (written by older paths / simple edits).
                if (all.TryGetValue(ThresholdKey, out var thresholdText) &&
                    double.TryParse(thresholdText, NumberStyles.Float, CultureInfo.InvariantCulture, out var threshold))
                {
                    _recognition.Threshold = RecognitionDeciderNormalize(threshold);
                }
            }

            _logger?.LogInformation(
                "Settings loaded (threshold={Threshold}, recognitionEnabled={Enabled})",
                Recognition.Threshold, Current.RecognitionEnabled);
        }
        catch (Exception ex)
        {
            // Settings must never prevent the application from starting.
            _logger?.LogError(ex, "Failed to load settings from the database; using defaults");
        }

        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);

        lock (_gate)
        {
            _current = SanitizeApp(Clone(settings));
        }

        await PersistAsync(AppSettingsKey, JsonSerializer.Serialize(_current, JsonOptions), cancellationToken);
        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task SaveRecognitionAsync(RecognitionOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        var sanitized = Sanitize(options);
        lock (_gate)
        {
            _recognition = sanitized;
        }

        await PersistAsync(RecognitionKey, JsonSerializer.Serialize(sanitized, JsonOptions), cancellationToken);
        await PersistAsync(ThresholdKey, sanitized.Threshold.ToString(CultureInfo.InvariantCulture), cancellationToken);
        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    private async Task PersistAsync(string key, string value, CancellationToken cancellationToken)
    {
        try
        {
            await _repository.SetAsync(key, value, cancellationToken);
        }
        catch (Exception ex)
        {
            // Never let a settings save crash the app.
            _logger?.LogError(ex, "Failed to persist setting {Key}", key);
        }
    }

    /// <summary>
    /// Clamp a persisted <see cref="AppSettings"/> so a hand-edited row (or a
    /// value written by a future version) cannot request an impossible capture
    /// size and make the camera silently fail to open.
    ///
    /// Phase 3 retention/cooldown fields are clamped to their documented value
    /// sets: a retention of 0 or 9999 days is never valid (0 would mean
    /// "delete immediately", 9999 "never clean up" — the UI only offers
    /// 1/3/7/14/30 days, so a bogus value snaps to the default instead).
    /// </summary>
    private static AppSettings SanitizeApp(AppSettings settings)
    {
        // Must cover every documented preset plus a little headroom for
        // whatever the device actually accepts.
        settings.CameraWidth = Math.Clamp(settings.CameraWidth, 160, 3840);
        settings.CameraHeight = Math.Clamp(settings.CameraHeight, 120, 2160);

        settings.NotificationCooldownSeconds = Math.Clamp(settings.NotificationCooldownSeconds, 0, 3600);
        settings.CameraRetryIntervalSeconds = Math.Clamp(settings.CameraRetryIntervalSeconds, 5, 3600);

        settings.SnapshotRetentionDays = settings.SnapshotRetentionDays is 1 or 3 or 7 or 14 or 30
            ? settings.SnapshotRetentionDays
            : 7;

        settings.EventRetentionDays = Math.Clamp(settings.EventRetentionDays, 1, 3650);

        // The one-time Phase 3 migration: MinimizeToTray was never exposed to
        // the operator before (TrayAvailable was hard-coded false), so a value
        // of 0 is "a Phase 2 database", not an operator choice. Bump it to the
        // Phase 3 default exactly once; SettingsVersion then never matches
        // again, so an operator who turns the toggle off keeps it off.
        if (settings.SettingsVersion < 1)
        {
            settings.MinimizeToTray = true;
            settings.SettingsVersion = 1;
        }

        settings.SettingsVersion = Math.Clamp(settings.SettingsVersion, 1, 1_000_000);

        return settings;
    }

    private static RecognitionOptions Sanitize(RecognitionOptions options)
    {
        options.Threshold = Core.Services.RecognitionDecider.NormalizeThreshold(options.Threshold);
        options.MinimumFaceSize = Math.Clamp(options.MinimumFaceSize, 32, 2000);
        options.RecognitionCooldownSeconds = Math.Clamp(options.RecognitionCooldownSeconds, 0, 600);
        options.DetectionFps = Math.Clamp(options.DetectionFps, 1, 30);
        options.MinimumBlurScore = Math.Clamp(options.MinimumBlurScore, 0, 1000);
        options.MinimumBrightness = Math.Clamp(options.MinimumBrightness, 0, 255);
        options.MaximumBrightness = Math.Clamp(options.MaximumBrightness, 0, 255);
        options.EnrollmentSampleCount = Math.Clamp(options.EnrollmentSampleCount, 5, 100);
        options.MinimumFaceRatio = Math.Clamp(options.MinimumFaceRatio, 0.01, 1.0);
        options.StableFaceFrames = Math.Clamp(options.StableFaceFrames, 1, 30);
        options.UnknownFaceCooldownSeconds = Math.Clamp(options.UnknownFaceCooldownSeconds, 0, 3600);

        if (options.MaximumBrightness <= options.MinimumBrightness)
            options.MaximumBrightness = options.MinimumBrightness + 1;

        return options;
    }

    private static double RecognitionDeciderNormalize(double value)
        => Core.Services.RecognitionDecider.NormalizeThreshold(value);

    private static T? Deserialize<T>(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return default;
        }
    }

    private static AppSettings Clone(AppSettings source) => new()
    {
        RecognitionEnabled = source.RecognitionEnabled,
        LivenessCheckEnabled = source.LivenessCheckEnabled,
        StoreSnapshots = source.StoreSnapshots,
        StoreSecurityEvents = source.StoreSecurityEvents,
        StartWithWindows = source.StartWithWindows,
        MinimizeToTray = source.MinimizeToTray,
        // Phase 3: every monitoring field must round-trip. A field missing from
        // this list silently reverts to its default on the next save.
        BackgroundMonitoring = source.BackgroundMonitoring,
        SessionMonitoring = source.SessionMonitoring,
        MonitorCameraWhenUnlocked = source.MonitorCameraWhenUnlocked,
        UnknownFaceDetection = source.UnknownFaceDetection,
        DesktopNotifications = source.DesktopNotifications,
        NotificationCooldownSeconds = source.NotificationCooldownSeconds,
        SnapshotRetentionDays = source.SnapshotRetentionDays,
        EventRetentionDays = source.EventRetentionDays,
        CameraRetryIntervalSeconds = source.CameraRetryIntervalSeconds,
        SettingsVersion = source.SettingsVersion,
        SelectedCamera = source.SelectedCamera,
        CameraWidth = source.CameraWidth,
        CameraHeight = source.CameraHeight,
    };

    private static RecognitionOptions CloneRecognition(RecognitionOptions source) => new()
    {
        Threshold = source.Threshold,
        MinimumFaceSize = source.MinimumFaceSize,
        RecognitionCooldownSeconds = source.RecognitionCooldownSeconds,
        DetectionFps = source.DetectionFps,
        MinimumBlurScore = source.MinimumBlurScore,
        MinimumBrightness = source.MinimumBrightness,
        MaximumBrightness = source.MaximumBrightness,
        EnrollmentSampleCount = source.EnrollmentSampleCount,
        MinimumFaceRatio = source.MinimumFaceRatio,
        StableFaceFrames = source.StableFaceFrames,
        UnknownFaceCooldownSeconds = source.UnknownFaceCooldownSeconds,
    };

    private const string AppSettingsKey = "app.settings.v1";
    private const string RecognitionKey = "app.recognition.v1";
    private const string ThresholdKey = "recognition.threshold";
}
