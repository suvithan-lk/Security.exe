using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using Microsoft.Extensions.Logging;
using Security.App.Mvvm;
using Security.App.Services;
using Security.Core.Entities;
using Security.Core.Enums;
using Security.Core.Interfaces;

namespace Security.App.ViewModels;

/// <summary>One icon entry in the SECURITY TIMELINE (spec §20).</summary>
public sealed class TimelineEntry
{
    public required string Glyph { get; init; }

    public required string Time { get; init; }

    public required string Text { get; init; }

    /// <summary>Tone token for <c>ToneToBrushConverter</c>.</summary>
    public required string Tone { get; init; }
}

/// <summary>One subsystem row in SYSTEM HEALTH (spec §21).</summary>
public sealed class HealthRow
{
    public required string Name { get; init; }

    /// <summary>OK / DEGRADED / UNAVAILABLE.</summary>
    public required string Status { get; init; }

    /// <summary>Concrete fault text from the component — never a guess.</summary>
    public required string Detail { get; init; }

    public required string Tone { get; init; }
}

/// <summary>
/// Landing screen: system status banner, status cards, the icon-based
/// security timeline, system health and the most recent events. All values
/// are projections of the shared <see cref="CameraCoordinator"/>,
/// <see cref="SecurityMonitorService"/>, <see cref="IHealthMonitor"/> and the
/// event store — this view model owns no capture or monitoring logic.
/// </summary>
public sealed class DashboardViewModel : ViewModelBase
{
    private readonly CameraCoordinator _coordinator;
    private readonly ISecurityEventService _events;
    private readonly INavigationService _navigation;
    private readonly SecurityMonitorService _monitor;
    private readonly IHealthMonitor _health;
    private readonly ISettingsService _settings;
    private readonly ILogger<DashboardViewModel>? _logger;

    private string _lastEvent = "No events yet";
    private string _lastEventTime = "—";
    private string _lastEventTone = "idle";
    private string _recentEventsHeader = "Recent events";
    private string _healthSummary = "No health data published yet.";
    private string _monitoringCardDetail = string.Empty;

    public DashboardViewModel(
        CameraCoordinator coordinator,
        ISecurityEventService events,
        INavigationService navigation,
        SecurityMonitorService monitor,
        IHealthMonitor health,
        ISettingsService settings,
        ILogger<DashboardViewModel>? logger = null)
    {
        _coordinator = coordinator;
        _events = events;
        _navigation = navigation;
        _monitor = monitor;
        _health = health;
        _settings = settings;
        _logger = logger;

        RecentEvents = new ObservableCollection<SecurityEvent>();
        Timeline = new ObservableCollection<TimelineEntry>();
        HealthRows = new ObservableCollection<HealthRow>();

        RefreshCommand = new AsyncRelayCommand(RefreshAsync);
        GoToProfileCommand = new RelayCommand(() => _navigation.NavigateTo("profile"));
        GoToCameraCommand = new RelayCommand(() => _navigation.NavigateTo("camera"));
        GoToEventsCommand = new RelayCommand(() => _navigation.NavigateTo("events"));

        _coordinator.PropertyChanged += OnCoordinatorPropertyChanged;
        _events.EventRecorded += OnEventRecorded;

        // Raised from background loops — every handler marshals to the UI thread.
        _monitor.StateChanged += OnMonitorStateChanged;
        _health.HealthChanged += OnHealthChanged;
        _settings.SettingsChanged += OnSettingsChanged;

        RefreshHealth();
    }

    #region Bindings — status banner (spec §19)

    /// <summary>MONITORING ACTIVE / MONITORING PAUSED / MONITORING OFF, verbatim from the monitor.</summary>
    public string BannerStatus => _monitor.MonitoringStatusText;

    public string BannerTone => _monitor.IsMonitoringActive
        ? "ok"
        : _monitor.IsPaused ? "warn" : "idle";

    public string BannerDetail
    {
        get
        {
            if (!_monitor.IsMonitoringActive)
                return _monitor.PauseReason ?? "Background monitoring is off.";

            // Honest subsystem caveats while monitoring IS active.
            if (!_coordinator.EngineReady)
                return "Recognition models are not available — events still record, faces do not.";
            if (!_coordinator.ProfileExists)
                return "No face profile enrolled yet — unknown-face detection is active.";
            if (_monitor.CurrentSessionState == SessionState.Locked)
                return "Windows is locked — camera capture is paused until unlock.";
            return "Session, camera and event recording are running.";
        }
    }

    #endregion

    #region Bindings — status cards

    public string SessionStatus => _monitor.CurrentSessionState.ToDisplayText();

    public string SessionDetail => _monitor.CurrentSessionState switch
    {
        SessionState.Locked => "Windows session locked — camera paused.",
        SessionState.Unlocked => "Windows session unlocked.",
        SessionState.LoggedOn => "A user logged on to this session.",
        SessionState.LoggedOff => "A user logged off from this session.",
        SessionState.Connected => "The session connected.",
        SessionState.Disconnected => "The session disconnected.",
        _ => "Session state not observed yet (session monitoring may be off).",
    };

    public string SessionTone => _monitor.CurrentSessionState switch
    {
        SessionState.Unlocked or SessionState.Locked or SessionState.LoggedOn => "ok",
        _ => "warn",
    };

    /// <summary>Active / Paused / Off for the MONITORING card.</summary>
    public string MonitoringCardStatus => _monitor.IsMonitoringActive
        ? "Active"
        : _monitor.IsPaused ? "Paused" : "Off";

    public string MonitoringCardDetail
    {
        get => _monitoringCardDetail;
        private set => SetProperty(ref _monitoringCardDetail, value);
    }

    public string MonitoringCardTone => _monitor.IsMonitoringActive
        ? "ok"
        : _monitor.IsPaused ? "warn" : "idle";

    public string FaceProfileStatus => _coordinator.ProfileExists ? "Enrolled" : "Not enrolled";

    public string FaceProfileDetail => _coordinator.ProfileExists
        ? "Biometric template stored encrypted (DPAPI)."
        : "Run enrollment to enable recognition.";

    public string FaceProfileTone => _coordinator.ProfileExists ? "ok" : "warn";

    public string CameraStatus => _coordinator.IsRunning
        ? $"{_coordinator.CameraStatus}"
        : "Camera stopped";

    public string CameraDetail => _coordinator.SelectedCamera?.Name ?? "No camera selected";

    public string CameraTone => _coordinator.IsRunning ? "ok" : "idle";

    public string EngineStatus => _coordinator.EngineReady ? "Recognition engine ready" : "Recognition engine not ready";

    public string EngineDetail => _coordinator.EngineReady
        ? "SFace + YuNet models loaded locally."
        : "Models missing or failed to load.";

    public string EngineTone => _coordinator.EngineReady ? "ok" : "bad";

    /// <summary>Most recent event of ANY type (spec §19 card LAST EVENT).</summary>
    public string LastEvent
    {
        get => _lastEvent;
        private set => SetProperty(ref _lastEvent, value);
    }

    public string LastEventTime
    {
        get => _lastEventTime;
        private set => SetProperty(ref _lastEventTime, value);
    }

    public string LastEventTone
    {
        get => _lastEventTone;
        private set => SetProperty(ref _lastEventTone, value);
    }

    #endregion

    #region Bindings — timeline, health, events

    public ObservableCollection<TimelineEntry> Timeline { get; }

    public ObservableCollection<HealthRow> HealthRows { get; }

    public string HealthSummary
    {
        get => _healthSummary;
        private set => SetProperty(ref _healthSummary, value);
    }

    public string RecentEventsHeader
    {
        get => _recentEventsHeader;
        private set => SetProperty(ref _recentEventsHeader, value);
    }

    public ObservableCollection<SecurityEvent> RecentEvents { get; }

    public ICommand RefreshCommand { get; }

    public ICommand GoToProfileCommand { get; }

    public ICommand GoToCameraCommand { get; }

    public ICommand GoToEventsCommand { get; }

    #endregion

    public override async Task OnNavigatedAsync()
    {
        await RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        IsBusy = true;
        ClearError();

        try
        {
            await _coordinator.RefreshEngineStatusAsync();

            var recent = await _events.GetRecentAsync(8);
            RecentEvents.Clear();
            foreach (var evt in recent)
                RecentEvents.Add(evt);

            if (RecentEvents.Count > 0)
                ApplyLastEvent(RecentEvents[0]);

            RecentEventsHeader = RecentEvents.Count == 0
                ? "No security events recorded yet."
                : "Recent events";

            RebuildTimeline();
            RefreshHealth();
            RaiseAll();
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Dashboard refresh failed");
            ReportError("Could not refresh the dashboard. See logs for details.", ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void OnCoordinatorPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Only re-project when one of the card inputs changes.
        switch (e.PropertyName)
        {
            case nameof(CameraCoordinator.EngineReady):
            case nameof(CameraCoordinator.ProfileExists):
            case nameof(CameraCoordinator.IsRunning):
            case nameof(CameraCoordinator.CameraStatus):
            case nameof(CameraCoordinator.SelectedCamera):
            case nameof(CameraCoordinator.EngineStatus):
                System.Windows.Application.Current?.Dispatcher.BeginInvoke(
                    new Action(RaiseAll));
                break;
        }
    }

    private void OnMonitorStateChanged(object? sender, EventArgs e)
        => System.Windows.Application.Current?.Dispatcher.BeginInvoke(new Action(RaiseAll));

    private void OnHealthChanged(object? sender, EventArgs e)
        => System.Windows.Application.Current?.Dispatcher.BeginInvoke(new Action(RefreshHealth));

    private void OnSettingsChanged(object? sender, EventArgs e)
        => System.Windows.Application.Current?.Dispatcher.BeginInvoke(new Action(RaiseAll));

    private void OnEventRecorded(object? sender, SecurityEvent e)
    {
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
        {
            // Keep newest first and bounded.
            RecentEvents.Insert(0, e);
            while (RecentEvents.Count > 8)
                RecentEvents.RemoveAt(RecentEvents.Count - 1);

            if (RecentEvents.Count == 1)
                RecentEventsHeader = "Recent events";

            ApplyLastEvent(e);
            RebuildTimeline();
        }));
    }

    private void ApplyLastEvent(SecurityEvent e)
    {
        LastEvent = string.IsNullOrWhiteSpace(e.Description) ? e.EventType.ToString() : e.Description;
        LastEventTime = e.Timestamp.ToLocalTime().ToString("HH:mm:ss");
        LastEventTone = e.Result switch
        {
            SecurityEventResult.Unknown or SecurityEventResult.Denied or SecurityEventResult.Failure => "bad",
            SecurityEventResult.Warning => "warn",
            SecurityEventResult.Known or SecurityEventResult.Success => "ok",
            _ => "accent",
        };
    }

    /// <summary>Rebuild the icon timeline from the newest events (bounded).</summary>
    private void RebuildTimeline()
    {
        Timeline.Clear();

        var count = 0;
        foreach (var evt in RecentEvents)
        {
            if (count++ >= 6)
                break;

            Timeline.Add(new TimelineEntry
            {
                Glyph = GlyphFor(evt.EventType),
                Time = evt.Timestamp.ToLocalTime().ToString("HH:mm:ss"),
                Text = string.IsNullOrWhiteSpace(evt.Description) ? evt.EventType.ToString() : evt.Description,
                Tone = ToneFor(evt.Result),
            });
        }
    }

    /// <summary>
    /// Refresh SYSTEM HEALTH from the monitor's published component states
    /// and summarise how many subsystems are healthy.
    /// </summary>
    private void RefreshHealth()
    {
        HealthRows.Clear();

        var components = _health.Components;
        var healthy = 0;

        foreach (var component in components)
        {
            if (component.Status == HealthStatus.Healthy)
                healthy++;

            HealthRows.Add(new HealthRow
            {
                Name = ComponentName(component.Component),
                Status = component.Status switch
                {
                    HealthStatus.Healthy => "OK",
                    HealthStatus.Degraded => "DEGRADED",
                    _ => "UNAVAILABLE",
                },
                Detail = component.Detail,
                Tone = component.Status switch
                {
                    HealthStatus.Healthy => "ok",
                    HealthStatus.Degraded => "warn",
                    _ => "bad",
                },
            });
        }

        HealthSummary = components.Count == 0
            ? "No health data published yet."
            : $"{healthy} of {components.Count} subsystems healthy.";

        OnPropertyChanged(nameof(HealthSummary));
    }

    private void RaiseAll()
    {
        OnPropertyChanged(nameof(BannerStatus));
        OnPropertyChanged(nameof(BannerTone));
        OnPropertyChanged(nameof(BannerDetail));
        OnPropertyChanged(nameof(SessionStatus));
        OnPropertyChanged(nameof(SessionDetail));
        OnPropertyChanged(nameof(SessionTone));
        OnPropertyChanged(nameof(MonitoringCardStatus));
        OnPropertyChanged(nameof(MonitoringCardDetail));
        OnPropertyChanged(nameof(MonitoringCardTone));
        OnPropertyChanged(nameof(FaceProfileStatus));
        OnPropertyChanged(nameof(FaceProfileDetail));
        OnPropertyChanged(nameof(FaceProfileTone));
        OnPropertyChanged(nameof(CameraStatus));
        OnPropertyChanged(nameof(CameraDetail));
        OnPropertyChanged(nameof(CameraTone));
        OnPropertyChanged(nameof(EngineStatus));
        OnPropertyChanged(nameof(EngineDetail));
        OnPropertyChanged(nameof(EngineTone));
        OnPropertyChanged(nameof(LastEvent));
        OnPropertyChanged(nameof(LastEventTime));
        OnPropertyChanged(nameof(LastEventTone));
        MonitoringCardDetail = BuildMonitoringCardDetail();
    }

    private string BuildMonitoringCardDetail()
    {
        var s = _settings.Current;
        return $"Session {OnOff(s.SessionMonitoring)} · Camera {OnOff(s.MonitorCameraWhenUnlocked)} " +
               $"· Faces {OnOff(s.UnknownFaceDetection)} · Alerts {OnOff(s.DesktopNotifications)}";

        static string OnOff(bool value) => value ? "on" : "off";
    }

    private static string ComponentName(HealthComponent component) => component switch
    {
        HealthComponent.Database => "Database",
        HealthComponent.Camera => "Camera",
        HealthComponent.RecognitionEngine => "Recognition engine",
        HealthComponent.BackgroundService => "Background service",
        HealthComponent.Notifications => "Notifications",
        _ => component.ToString(),
    };

    /// <summary>Only Segoe MDL2 glyphs already proven elsewhere in the app.</summary>
    private static string GlyphFor(SecurityEventType type) => type switch
    {
        SecurityEventType.SessionLocked or SecurityEventType.SessionUnlocked
            or SecurityEventType.SessionLogon or SecurityEventType.SessionLogoff
            or SecurityEventType.SessionConnected or SecurityEventType.SessionDisconnected
            => "\uE72E",

        SecurityEventType.CameraStarted or SecurityEventType.CameraStopped
            or SecurityEventType.CameraError or SecurityEventType.SnapshotCaptured
            or SecurityEventType.SnapshotDeleted
            => "\uE722",

        SecurityEventType.FaceDetected or SecurityEventType.KnownFaceDetected
            or SecurityEventType.EnrollmentStarted or SecurityEventType.EnrollmentCompleted
            or SecurityEventType.EnrollmentFailed
            => "\uE77B",

        SecurityEventType.UnknownFaceDetected => "\uE711",

        SecurityEventType.MonitoringStarted or SecurityEventType.MonitoringStopped
            or SecurityEventType.MonitoringPaused or SecurityEventType.MonitoringResumed
            or SecurityEventType.SettingChanged or SecurityEventType.ApplicationStarted
            or SecurityEventType.ApplicationStopped
            => "\uE713",

        SecurityEventType.NotificationSent or SecurityEventType.RecognitionFailed
            => "\uE946",

        _ => "\uE7BA",
    };

    private static string ToneFor(SecurityEventResult result) => result switch
    {
        SecurityEventResult.Unknown or SecurityEventResult.Denied or SecurityEventResult.Failure => "bad",
        SecurityEventResult.Warning => "warn",
        SecurityEventResult.Known or SecurityEventResult.Success => "ok",
        _ => "accent",
    };
}
