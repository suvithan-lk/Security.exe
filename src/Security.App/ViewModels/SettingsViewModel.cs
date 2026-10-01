using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows.Input;
using Microsoft.Extensions.Logging;
using Security.App.Mvvm;
using Security.App.Services;
using Security.Core.Interfaces;
using Security.Core.Models;

namespace Security.App.ViewModels;

/// <summary>
/// Settings screen. Edits a working copy of the effective settings and only
/// persists on Save, so an abandoned edit cannot half-apply.
/// </summary>
public sealed class SettingsViewModel : ViewModelBase
{
    private readonly ISettingsService _settings;
    private readonly CameraCoordinator _coordinator;
    private readonly IUserDialogService _dialogs;
    private readonly IToastService _toasts;
    private readonly ISecurityEventService _events;
    private readonly CameraOptions _cameraOptions;
    private readonly ILogger<SettingsViewModel>? _logger;

    private AppSettings _app;
    private RecognitionOptions _recognition;
    private string _savedMessage = string.Empty;
    private int _eventCount = -1;

    public SettingsViewModel(
        ISettingsService settings,
        CameraCoordinator coordinator,
        IUserDialogService dialogs,
        IToastService toasts,
        ISecurityEventService events,
        CameraOptions cameraOptions,
        ILogger<SettingsViewModel>? logger = null)
    {
        _settings = settings;
        _coordinator = coordinator;
        _dialogs = dialogs;
        _toasts = toasts;
        _events = events;
        _cameraOptions = cameraOptions;
        _logger = logger;

        _app = Clone(settings.Current);
        _recognition = Clone(settings.Recognition);

        SaveCommand = new AsyncRelayCommand(SaveAsync);
        ResetCommand = new RelayCommand(Reset);
        RefreshCamerasCommand = new AsyncRelayCommand(RefreshCamerasAsync);
        ClearEventsCommand = new AsyncRelayCommand(ClearEventsAsync);

        _settings.SettingsChanged += OnSettingsChanged;
    }

    #region Bindings — recognition

    public double Threshold
    {
        get => _recognition.Threshold;
        set
        {
            var clamped = Math.Clamp(value, 0, 1);
            if (Math.Abs(_recognition.Threshold - clamped) < double.Epsilon)
                return;

            _recognition.Threshold = clamped;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ThresholdHint));
        }
    }

    public string ThresholdHint => _recognition.Threshold switch
    {
        < 0.35 => "Very low — expect false matches. Not recommended.",
        > 0.85 => "Very high — expect missed matches. Not recommended.",
        _ => "Starting default 0.60. Calibrate against your own camera and model.",
    };

    public int MinimumFaceSize
    {
        get => _recognition.MinimumFaceSize;
        set
        {
            var clamped = Math.Clamp(value, 40, 640);
            if (_recognition.MinimumFaceSize == clamped)
                return;

            _recognition.MinimumFaceSize = clamped;
            OnPropertyChanged();
        }
    }

    public int CooldownSeconds
    {
        get => _recognition.RecognitionCooldownSeconds;
        set
        {
            var clamped = Math.Clamp(value, 0, 120);
            if (_recognition.RecognitionCooldownSeconds == clamped)
                return;

            _recognition.RecognitionCooldownSeconds = clamped;
            OnPropertyChanged();
        }
    }

    public int DetectionFps
    {
        get => _recognition.DetectionFps;
        set
        {
            var clamped = Math.Clamp(value, 1, 30);
            if (_recognition.DetectionFps == clamped)
                return;

            _recognition.DetectionFps = clamped;
            OnPropertyChanged();
        }
    }

    public int EnrollmentSamples
    {
        get => _recognition.EnrollmentSampleCount;
        set
        {
            var clamped = Math.Clamp(value, 10, 20);
            if (_recognition.EnrollmentSampleCount == clamped)
                return;

            _recognition.EnrollmentSampleCount = clamped;
            OnPropertyChanged();
        }
    }

    #endregion

    #region Bindings — toggles

    public bool RecognitionEnabled
    {
        get => _app.RecognitionEnabled;
        set
        {
            if (_app.RecognitionEnabled == value)
                return;

            _app.RecognitionEnabled = value;
            OnPropertyChanged();
        }
    }

    public bool LivenessCheckEnabled
    {
        get => _app.LivenessCheckEnabled;
        set
        {
            if (_app.LivenessCheckEnabled == value)
                return;

            _app.LivenessCheckEnabled = value;
            OnPropertyChanged();
        }
    }

    public bool StoreSnapshots
    {
        get => _app.StoreSnapshots;
        set
        {
            if (_app.StoreSnapshots == value)
                return;

            _app.StoreSnapshots = value;
            OnPropertyChanged();
        }
    }

    public bool StoreSecurityEvents
    {
        get => _app.StoreSecurityEvents;
        set
        {
            if (_app.StoreSecurityEvents == value)
                return;

            _app.StoreSecurityEvents = value;
            OnPropertyChanged();
        }
    }

    public bool StartWithWindows
    {
        get => _app.StartWithWindows;
        set
        {
            if (_app.StartWithWindows == value)
                return;

            _app.StartWithWindows = value;
            OnPropertyChanged();
        }
    }

    public bool MinimizeToTray
    {
        get => _app.MinimizeToTray;
        set
        {
            if (_app.MinimizeToTray == value)
                return;

            _app.MinimizeToTray = value;
            OnPropertyChanged();
        }
    }

    /// <summary>Tray behaviour needs a shell notification icon — still planned.</summary>
    public bool TrayAvailable => false;

    #endregion

    #region Bindings — camera

    /// <summary>
    /// Presets offered by the resolution picker. The currently persisted value
    /// is always present even if it predates this list, so a stored custom size
    /// never renders the combo box blank.
    /// </summary>
    public IReadOnlyList<string> CameraResolutions
    {
        get
        {
            var presets = new List<string> { "640 x 480", "1280 x 720", "1920 x 1080" };
            var current = CameraResolution;

            if (!presets.Contains(current))
                presets.Insert(0, current);

            return presets;
        }
    }

    public string CameraResolution
    {
        get => $"{_app.CameraWidth} x {_app.CameraHeight}";
        set
        {
            if (string.IsNullOrWhiteSpace(value))
                return;

            var parts = value.Split('x', '×');
            if (parts.Length != 2
                || !int.TryParse(parts[0].Trim(), out var width)
                || !int.TryParse(parts[1].Trim(), out var height))
            {
                return;
            }

            if (_app.CameraWidth == width && _app.CameraHeight == height)
                return;

            _app.CameraWidth = width;
            _app.CameraHeight = height;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CameraResolutions));
            OnPropertyChanged(nameof(CameraResolutionNote));
        }
    }

    public string CameraResolutionNote =>
        $"Applies the next time the camera is started (currently {_cameraOptions.Width} x {_cameraOptions.Height} live).";

    /// <summary>What the device list reports right now — never an invented figure.</summary>
    public string DetectedDeviceText
    {
        get
        {
            var count = _coordinator.Cameras.Count;
            return count switch
            {
                0 => "No capture device detected. Connect a camera and choose Refresh.",
                1 => "1 capture device detected.",
                _ => $"{count} capture devices detected.",
            };
        }
    }

    /// <summary>The device preview, enrollment and recognition currently use.</summary>
    public string SelectedDeviceText =>
        _coordinator.SelectedCamera?.Name ?? "Not selected";

    public AsyncRelayCommand RefreshCamerasCommand { get; }

    private async Task RefreshCamerasAsync()
    {
        ClearError();
        try
        {
            await _coordinator.RefreshCamerasAsync();
            OnPropertyChanged(nameof(DetectedDeviceText));
            OnPropertyChanged(nameof(SelectedDeviceText));
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Camera refresh from Settings failed");
            ReportError("Could not list cameras.", ex);
        }
    }

    #endregion

    #region Bindings — security

    /// <summary>How many events are currently on disk (-1 before the first read).</summary>
    public int EventCount
    {
        get => _eventCount;
        private set
        {
            if (SetProperty(ref _eventCount, value))
            {
                OnPropertyChanged(nameof(EventCountText));
                OnPropertyChanged(nameof(CanClearEvents));
            }
        }
    }

    public string EventCountText => EventCount switch
    {
        < 0 => "Counting…",
        0 => "The event log is empty.",
        1 => "1 event recorded.",
        _ => $"{EventCount} events recorded.",
    };

    /// <summary>Nothing to clear until the count is known and non-zero.</summary>
    public bool CanClearEvents => EventCount > 0;

    public AsyncRelayCommand ClearEventsCommand { get; }

    private async Task ClearEventsAsync()
    {
        var confirmed = await _dialogs.ConfirmAsync(
            "Clear security event log",
            $"{EventCountText}\n\n" +
            "This permanently removes every recorded security event from the local database.\n\n" +
            "Your enrolled face profile is NOT affected.\n\nContinue?");

        if (!confirmed)
            return;

        try
        {
            await _events.ClearAsync();
            EventCount = 0;
            _toasts.Success("Event log cleared", "All recorded security events were removed.");
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Clearing the security event log failed");
            ReportError("Could not clear the security event log. See logs for details.", ex);
        }
    }

    /// <summary>Short status values shown in the control column.</summary>
    public string EncryptionLine => "Windows DPAPI (CurrentUser)";

    public string CredentialLine => "None stored";

    public string StorageLine => "Local only";

    #endregion

    public string SavedMessage
    {
        get => _savedMessage;
        private set => SetProperty(ref _savedMessage, value);
    }

    public string LivenessNotice =>
        "Liveness is still a non-functional placeholder. It performs NO anti-spoofing and always " +
        "returns \"unable to determine\" — do not rely on it. Real liveness detection remains planned.";

    public ICommand SaveCommand { get; }

    public ICommand ResetCommand { get; }

    public override async Task OnNavigatedAsync()
    {
        // Pull the latest persisted values in case another screen changed them.
        _app = Clone(_settings.Current);
        _recognition = Clone(_settings.Recognition);
        RaiseAll();

        // Read-only status rows: report what is actually on disk right now.
        try
        {
            EventCount = await _events.CountAsync();
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Could not count security events for the Settings screen");
            EventCount = -1;
        }

        OnPropertyChanged(nameof(DetectedDeviceText));
        OnPropertyChanged(nameof(SelectedDeviceText));
    }

    private async Task SaveAsync()
    {
        ClearError();
        SavedMessage = string.Empty;

        try
        {
            // Persist the Windows startup entry first so the stored flag always
            // reflects reality rather than a failed registry write.
            if (_app.StartWithWindows != _settings.Current.StartWithWindows)
            {
                try
                {
                    StartupRegistration.SetEnabled(_app.StartWithWindows);
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "Could not update the Windows startup entry");
                    ReportError("Could not update the 'start with Windows' entry. Other settings were not saved.", ex);
                    return;
                }
            }

            await _settings.SaveAsync(_app);
            await _settings.SaveRecognitionAsync(_recognition);

            // CameraOptions is a mutable singleton read by CameraService each
            // time it opens a device, so applying here takes effect on the next
            // start without rebuilding the container.
            _cameraOptions.Width = _app.CameraWidth;
            _cameraOptions.Height = _app.CameraHeight;
            OnPropertyChanged(nameof(CameraResolutionNote));

            SavedMessage = $"Settings saved at {DateTime.Now:HH:mm:ss}.";
            OnPropertyChanged(nameof(ThresholdHint));
            _toasts.Success("Settings saved", "Changes are active for new camera sessions and recognition runs.");

            await _coordinator.RefreshEngineStatusAsync();
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Saving settings failed");
            ReportError("Could not save settings. See logs for details.", ex);
        }
    }

    private void Reset()
    {
        _app = new AppSettings();
        _recognition = new RecognitionOptions();
        RaiseAll();

        SavedMessage = string.Empty;
        ClearError();
    }

    private void OnSettingsChanged(object? sender, EventArgs e)
        => System.Windows.Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
        {
            // Another screen changed settings; re-sync if we are not mid-edit.
            if (IsBusy)
                return;

            _app = Clone(_settings.Current);
            _recognition = Clone(_settings.Recognition);
            RaiseAll();
        }));

    private void RaiseAll()
    {
        OnPropertyChanged(nameof(Threshold));
        OnPropertyChanged(nameof(ThresholdHint));
        OnPropertyChanged(nameof(MinimumFaceSize));
        OnPropertyChanged(nameof(CooldownSeconds));
        OnPropertyChanged(nameof(DetectionFps));
        OnPropertyChanged(nameof(EnrollmentSamples));
        OnPropertyChanged(nameof(RecognitionEnabled));
        OnPropertyChanged(nameof(LivenessCheckEnabled));
        OnPropertyChanged(nameof(StoreSnapshots));
        OnPropertyChanged(nameof(StoreSecurityEvents));
        OnPropertyChanged(nameof(StartWithWindows));
        OnPropertyChanged(nameof(MinimizeToTray));
        OnPropertyChanged(nameof(CameraResolutions));
        OnPropertyChanged(nameof(CameraResolution));
        OnPropertyChanged(nameof(CameraResolutionNote));
        OnPropertyChanged(nameof(DetectedDeviceText));
        OnPropertyChanged(nameof(SelectedDeviceText));
    }

    private static AppSettings Clone(AppSettings source) => new()
    {
        RecognitionEnabled = source.RecognitionEnabled,
        LivenessCheckEnabled = source.LivenessCheckEnabled,
        StoreSnapshots = source.StoreSnapshots,
        StoreSecurityEvents = source.StoreSecurityEvents,
        StartWithWindows = source.StartWithWindows,
        MinimizeToTray = source.MinimizeToTray,
        SelectedCamera = source.SelectedCamera,
        CameraWidth = source.CameraWidth,
        CameraHeight = source.CameraHeight,
    };

    private static RecognitionOptions Clone(RecognitionOptions source) => new()
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
    };
}
