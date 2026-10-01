using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Security.Core.Enums;
using Security.Core.Interfaces;
using Security.Infrastructure.Services;

namespace Security.App.Services;

/// <summary>
/// Background orchestration of the Phase 3 monitoring loop.
///
/// Responsibilities:
///  - observe Windows session transitions (via <see cref="IWindowsSessionService"/>)
///    and record SessionLocked / Unlocked / Logon / Logoff / Connected /
///    Disconnected events;
///  - decide when the camera runs: it ALWAYS stops on lock, resumes after
///    unlock only when monitoring is active and "monitor camera when unlocked"
///    is on, retries after failures, and never churns start/stop;
///  - record the MonitoringStarted / Stopped / Paused / Resumed lifecycle;
///  - apply the unknown-face detection gate to the frame pipeline;
///  - apply the notification cooldown before a desktop notification is sent;
///  - publish component health (Database / Camera / RecognitionEngine /
///    BackgroundService / Notifications) to <see cref="IHealthMonitor"/>.
///
/// SECURITY BOUNDARY: this service only *observes* state Windows already
/// publishes. It never touches winlogon, credentials, authentication, the
/// Secure Desktop, or the lock screen, and it never displays UI itself —
/// notifications are raised as <see cref="NotificationRequested"/> for the
/// tray and alert window to render in the normal desktop session only.
///
/// THREADING: camera lifecycle calls are marshalled onto the WPF dispatcher
/// (the coordinator's collection state is UI-affine); in unit tests, where no
/// <see cref="Application"/> exists, they run inline. Session callbacks arrive
/// on a system broadcast thread and are handled fire-and-forget with logging —
/// nothing they do can throw back into Windows.
/// </summary>
public sealed class SecurityMonitorService : BackgroundService
{
    private static readonly TimeSpan CameraTickInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan StartupSettleDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan DatabaseProbeInterval = TimeSpan.FromSeconds(60);

    /// <summary>How often the retention sweep repeats while monitoring is active.</summary>
    private static readonly TimeSpan RetentionInterval = TimeSpan.FromHours(6);

    private readonly IWindowsSessionService _session;
    private readonly CameraCoordinator _camera;
    private readonly ISettingsService _settings;
    private readonly ISecurityEventService _events;
    private readonly IHealthMonitor _health;
    private readonly IFrameProcessor _processor;
    private readonly IRetentionService? _retention;
    private readonly ILogger<SecurityMonitorService>? _logger;

    private bool _sessionRunning;
    private bool _trayPaused;

    /// <summary>True while a MonitoringStarted event has been recorded (its Stop counterpart is owed).</summary>
    private bool _monitoringStarted;

    /// <summary>
    /// True when the monitor owes the camera a start: it either expressed
    /// monitoring intent at launch / on a settings re-enable, or it stopped a
    /// running camera itself and policy now allows capture again. A camera the
    /// OPERATOR stopped never sets this flag, so the monitor never overrides
    /// an explicit Stop from the Camera page.
    /// </summary>
    private bool _cameraStartPending;

    /// <summary>Last settings-level policy snapshot (background monitoring + camera when unlocked), used to detect transitions.</summary>
    private bool _prevSettingsPolicy;

    private DateTime? _lastCameraAttemptUtc;
    private DateTime? _lastNotificationUtc;
    private DateTime? _lastDatabaseProbeUtc;
    private DateTime? _lastRetentionUtc;

    public SecurityMonitorService(
        IWindowsSessionService session,
        CameraCoordinator camera,
        ISettingsService settings,
        ISecurityEventService events,
        IHealthMonitor health,
        IFrameProcessor processor,
        IRetentionService? retention = null,
        ILogger<SecurityMonitorService>? logger = null)
    {
        _session = session;
        _camera = camera;
        _settings = settings;
        _events = events;
        _health = health;
        _processor = processor;
        _retention = retention;
        _logger = logger;
    }

    /// <summary>Raised after any monitoring-state change (tray status line, dashboard).</summary>
    public event EventHandler? StateChanged;

    /// <summary>
    /// Raised when the notification cooldown allows a new notification for an
    /// unknown-face alert. Subscribers (tray balloon, alert window) decide how
    /// to render it and must only do so in the normal desktop session.
    /// </summary>
    public event EventHandler<SecurityAlertEventArgs>? NotificationRequested;

    /// <summary>True while the tray's Pause Monitoring action is in effect.</summary>
    public bool IsPaused => _trayPaused;

    /// <summary>True while background monitoring is enabled in Settings and not paused by the operator.</summary>
    public bool IsMonitoringActive => _settings.Current.BackgroundMonitoring && !_trayPaused;

    /// <summary>Last known Windows session state (Unknown when session monitoring is off or the probe failed).</summary>
    public SessionState CurrentSessionState => _session.CurrentState;

    /// <summary>Honest status line for the tray and the dashboard header.</summary>
    public string MonitoringStatusText => IsMonitoringActive
        ? "MONITORING ACTIVE"
        : _trayPaused ? "MONITORING PAUSED" : "MONITORING OFF";

    /// <summary>Why monitoring is not active, or null while it is.</summary>
    public string? PauseReason => IsMonitoringActive
        ? null
        : _trayPaused ? "Paused by the operator." : "Background monitoring is off in Settings.";

    // ---------------------------------------------------------------------
    // BackgroundService
    // ---------------------------------------------------------------------

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await InitializeAsync(stoppingToken).ConfigureAwait(false);

            // Give the shell's first refresh a moment before the monitor
            // touches the camera, so the coordinator is never asked to start
            // and enumerate from two directions at once.
            await Task.Delay(StartupSettleDelay, stoppingToken).ConfigureAwait(false);

            await ApplyCameraPolicyAsync(stoppingToken).ConfigureAwait(false);

            // First retention sweep shortly after startup, then periodically.
            await RunRetentionIfDueAsync(stoppingToken).ConfigureAwait(false);

            using var timer = new PeriodicTimer(CameraTickInterval);
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                await ReconcileCameraAsync(stoppingToken).ConfigureAwait(false);
                await RefreshHealthAsync(stoppingToken).ConfigureAwait(false);
                await RunRetentionIfDueAsync(stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        finally
        {
            await ShutdownAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Wire up subscriptions, start session observation, record
    /// MonitoringStarted and arm the camera auto-start. Public so tests can
    /// exercise the lifecycle without running the timer loop.
    /// </summary>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        var settings = _settings.Current;

        // The subscription lives for the whole session; SessionMonitoring only
        // controls whether the underlying service is listening. That way a
        // settings toggle does not have to re-subscribe and cannot race.
        _session.SessionStateChanged += OnSessionStateChanged;
        _sessionRunning = false;
        if (settings.SessionMonitoring)
        {
            _session.Start();
            _sessionRunning = true;
        }

        _settings.SettingsChanged += OnSettingsChanged;
        _camera.SecurityAlert += OnSecurityAlert;

        _prevSettingsPolicy = settings.BackgroundMonitoring && settings.MonitorCameraWhenUnlocked;

        // Intent: monitoring settings that allow capture mean the monitor owes
        // the camera a start as soon as the session state positively allows it.
        _cameraStartPending = _prevSettingsPolicy;

        _monitoringStarted = settings.BackgroundMonitoring;
        if (_monitoringStarted)
        {
            await RecordAsync(SecurityEventType.MonitoringStarted, SecurityEventResult.Info,
                "Background monitoring started.", cancellationToken).ConfigureAwait(false);
        }

        UpdateDetectionGate();
        await RefreshHealthAsync(cancellationToken).ConfigureAwait(false);
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Unsubscribe, stop session observation, close out the lifecycle event.</summary>
    public async Task ShutdownAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            _session.SessionStateChanged -= OnSessionStateChanged;
            _settings.SettingsChanged -= OnSettingsChanged;
            _camera.SecurityAlert -= OnSecurityAlert;

            if (_sessionRunning)
            {
                _session.Stop();
                _sessionRunning = false;
            }

            if (_monitoringStarted)
            {
                _monitoringStarted = false;
                await RecordAsync(SecurityEventType.MonitoringStopped, SecurityEventResult.Info,
                    "Background monitoring stopped.", cancellationToken).ConfigureAwait(false);
            }

            _health.ReportUnavailable(HealthComponent.BackgroundService, "Background monitoring service is stopped.");
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Monitor shutdown reporting failed");
        }
    }

    // ---------------------------------------------------------------------
    // Session transitions
    // ---------------------------------------------------------------------

    private void OnSessionStateChanged(object? sender, SessionStateChangedEventArgs e)
        => _ = HandleSessionStateChangeSafeAsync(e);

    private async Task HandleSessionStateChangeSafeAsync(SessionStateChangedEventArgs e)
    {
        try
        {
            await HandleSessionStateChangeAsync(e, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // A failure here must never propagate into the Windows broadcast.
            _logger?.LogWarning(ex, "Session state handling failed");
        }
    }

    /// <summary>
    /// Record a session event (when session monitoring is on), then let the
    /// camera policy react: lock always pauses capture, an unlocked session
    /// resumes it only when monitoring is active. Transient states (logon,
    /// logoff, connect, disconnect) are recorded but never flip the camera on
    /// their own — only a positively allowed session state may start capture.
    /// </summary>
    public async Task HandleSessionStateChangeAsync(
        SessionStateChangedEventArgs e,
        CancellationToken cancellationToken = default)
    {
        if (e.Current == e.Previous)
            return;

        var settings = _settings.Current;

        if (settings.SessionMonitoring)
        {
            var type = e.Current switch
            {
                SessionState.Locked => SecurityEventType.SessionLocked,
                SessionState.Unlocked => SecurityEventType.SessionUnlocked,
                SessionState.LoggedOn => SecurityEventType.SessionLogon,
                SessionState.LoggedOff => SecurityEventType.SessionLogoff,
                SessionState.Connected => SecurityEventType.SessionConnected,
                SessionState.Disconnected => SecurityEventType.SessionDisconnected,
                _ => (SecurityEventType?)null,
            };

            if (type is not null)
            {
                await RecordAsync(type.Value, SecurityEventResult.Info, DescribeSession(e.Current),
                    cancellationToken, sessionState: e.Current).ConfigureAwait(false);
            }
        }

        // Applies lock pause, unlock resume and pending starts in one place.
        await ApplyCameraPolicyAsync(cancellationToken).ConfigureAwait(false);
        await RefreshHealthAsync(cancellationToken).ConfigureAwait(false);
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private static string DescribeSession(SessionState state) => state switch
    {
        SessionState.Locked => "Windows session locked.",
        SessionState.Unlocked => "Windows session unlocked.",
        SessionState.LoggedOn => "A user logged on to the session.",
        SessionState.LoggedOff => "A user logged off from the session.",
        SessionState.Connected => "The session connected.",
        SessionState.Disconnected => "The session disconnected.",
        _ => "Session state changed.",
    };

    // ---------------------------------------------------------------------
    // Settings transitions
    // ---------------------------------------------------------------------

    private void OnSettingsChanged(object? sender, EventArgs e)
        => _ = HandleSettingsChangedSafeAsync();

    private async Task HandleSettingsChangedSafeAsync()
    {
        try
        {
            await HandleSettingsChangedAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Settings change handling failed");
        }
    }

    /// <summary>
    /// Re-apply everything a settings change can move: session observation,
    /// the MonitoringStarted / Stopped lifecycle, the camera policy (a setting
    /// that turns back on re-arms the camera), the unknown-detection gate and
    /// health. Public so tests can drive it directly — the settings service in
    /// tests raises no events.
    /// </summary>
    public async Task HandleSettingsChangedAsync(CancellationToken cancellationToken = default)
    {
        var settings = _settings.Current;

        if (settings.SessionMonitoring && !_sessionRunning)
        {
            _session.Start();
            _sessionRunning = true;
        }
        else if (!settings.SessionMonitoring && _sessionRunning)
        {
            _session.Stop();
            _sessionRunning = false;
        }

        if (settings.BackgroundMonitoring && !_monitoringStarted)
        {
            _monitoringStarted = true;
            await RecordAsync(SecurityEventType.MonitoringStarted, SecurityEventResult.Info,
                "Background monitoring started.", cancellationToken).ConfigureAwait(false);
        }
        else if (!settings.BackgroundMonitoring && _monitoringStarted)
        {
            _monitoringStarted = false;
            await RecordAsync(SecurityEventType.MonitoringStopped, SecurityEventResult.Info,
                "Background monitoring stopped.", cancellationToken).ConfigureAwait(false);
        }

        var settingsPolicy = settings.BackgroundMonitoring && settings.MonitorCameraWhenUnlocked;
        if (settingsPolicy && !_prevSettingsPolicy)
        {
            // Turning monitoring back on is a monitoring intent: the camera
            // may start again as soon as the session allows capture.
            _cameraStartPending = true;
        }
        _prevSettingsPolicy = settingsPolicy;

        UpdateDetectionGate();
        await ApplyCameraPolicyAsync(cancellationToken).ConfigureAwait(false);
        await RefreshHealthAsync(cancellationToken).ConfigureAwait(false);
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    // ---------------------------------------------------------------------
    // Tray pause / resume
    // ---------------------------------------------------------------------

    /// <summary>
    /// Pause or resume monitoring from the tray. Pausing stops a running
    /// camera (the monitor remembers it owes a restart), closes unknown-face
    /// detection and silences notifications; it records MonitoringPaused /
    /// MonitoringResumed while background monitoring is enabled.
    /// </summary>
    public async Task SetMonitoringPausedAsync(bool paused, CancellationToken cancellationToken = default)
    {
        if (_trayPaused == paused)
            return;

        _trayPaused = paused;

        if (_settings.Current.BackgroundMonitoring)
        {
            await RecordAsync(
                paused ? SecurityEventType.MonitoringPaused : SecurityEventType.MonitoringResumed,
                SecurityEventResult.Info,
                paused ? "Background monitoring paused by the operator."
                       : "Background monitoring resumed by the operator.",
                cancellationToken).ConfigureAwait(false);
        }

        UpdateDetectionGate();
        await ApplyCameraPolicyAsync(cancellationToken).ConfigureAwait(false);
        await RefreshHealthAsync(cancellationToken).ConfigureAwait(false);
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    // ---------------------------------------------------------------------
    // Camera policy
    // ---------------------------------------------------------------------

    /// <summary>
    /// Reconcile camera capture with policy and session state. Safe to call
    /// from any thread and at any time — every branch is idempotent:
    ///
    ///  1. Locked session   → pause capture (unconditional safety rule; no
    ///                        setting keeps the camera alive on a locked desk).
    ///  2. Policy off       → stop a running camera and remember a restart is
    ///                        owed (this is how the monitor "stops" — an
    ///                        operator's Stop is never converted into a start).
    ///  3. Capture running  → adopt it; nothing to do.
    ///  4. Session unknown  → never start (Unknown is never treated as Unlocked).
    ///  5. Session paused   → resume what was running before the lock.
    ///  6. Start owed       → start (launch auto-start, settings re-enable,
    ///                        restart after a policy stop).
    /// </summary>
    public async Task ApplyCameraPolicyAsync(CancellationToken cancellationToken = default)
    {
        var settings = _settings.Current;
        var policyAllows = settings.BackgroundMonitoring
                           && !_trayPaused
                           && settings.MonitorCameraWhenUnlocked;
        var sessionAllows = !settings.SessionMonitoring
                            || _session.CurrentState.AllowsCameraMonitoring();
        var locked = settings.SessionMonitoring
                     && _session.CurrentState == SessionState.Locked;
        var captureActive = _camera.IsRunning || _camera.State == CameraState.Connecting;

        if (locked)
        {
            if (captureActive)
            {
                await OnUiAsync(() => _camera.PauseForSessionAsync(cancellationToken), cancellationToken)
                    .ConfigureAwait(false);
            }
            return;
        }

        if (!policyAllows)
        {
            if (captureActive)
            {
                await OnUiAsync(() => _camera.StopAsync(cancellationToken), cancellationToken)
                    .ConfigureAwait(false);
                _cameraStartPending = true;
            }
            // Not running: leave the pending flag alone so a pause issued
            // before the auto-start still starts on resume.
            return;
        }

        if (captureActive)
        {
            _cameraStartPending = false;
            return;
        }

        if (!sessionAllows)
            return;

        if (_camera.State == CameraState.Paused && _camera.KeepRunning)
        {
            await OnUiAsync(() => _camera.ResumeFromSessionAsync(cancellationToken), cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        if (!_cameraStartPending)
            return;

        _cameraStartPending = false;
        _lastCameraAttemptUtc = DateTime.UtcNow;
        await OnUiAsync(() => _camera.StartAsync(cancellationToken), cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Periodic reconcile: retry a failed start once
    /// <c>CameraRetryIntervalSeconds</c> has elapsed, and resume a lock pause
    /// if the unlock broadcast was missed. It never consults the pending flag,
    /// so a camera the operator stopped while monitoring is off is never
    /// restarted from the timer.
    /// </summary>
    public async Task ReconcileCameraAsync(CancellationToken cancellationToken = default)
    {
        var settings = _settings.Current;
        var shouldRun = settings.BackgroundMonitoring
                        && !_trayPaused
                        && settings.MonitorCameraWhenUnlocked
                        && (!settings.SessionMonitoring
                            || _session.CurrentState.AllowsCameraMonitoring());

        if (_camera.IsRunning || _camera.State == CameraState.Connecting)
            return;

        if (_camera.State == CameraState.Paused && _camera.KeepRunning && shouldRun)
        {
            await OnUiAsync(() => _camera.ResumeFromSessionAsync(cancellationToken), cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        // Retry only when someone (operator or monitor) still wants it running.
        if (!shouldRun || !_camera.KeepRunning)
            return;

        if (_camera.State == CameraState.Error && !CameraRetryDue(settings.CameraRetryIntervalSeconds))
            return;

        _lastCameraAttemptUtc = DateTime.UtcNow;
        await OnUiAsync(() => _camera.StartAsync(cancellationToken), cancellationToken)
            .ConfigureAwait(false);
    }

    private bool CameraRetryDue(int retryIntervalSeconds)
    {
        if (_lastCameraAttemptUtc is not DateTime last)
            return true;

        return DateTime.UtcNow - last >= TimeSpan.FromSeconds(Math.Max(0, retryIntervalSeconds));
    }

    // ---------------------------------------------------------------------
    // Notifications
    // ---------------------------------------------------------------------

    private void OnSecurityAlert(object? sender, SecurityAlertEventArgs e)
        => _ = HandleUnknownFaceAlertSafeAsync(e);

    private async Task HandleUnknownFaceAlertSafeAsync(SecurityAlertEventArgs e)
    {
        try
        {
            await HandleUnknownFaceAlertAsync(e, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Notification handling failed");
        }
    }

    /// <summary>
    /// Unknown-face alert pipeline: only while monitoring is active, never for
    /// a locked session (no UI may ever target the Secure Desktop), and at
    /// most one notification per <c>NotificationCooldownSeconds</c>. Records
    /// NotificationSent only when a desktop notification actually goes out.
    /// </summary>
    public async Task HandleUnknownFaceAlertAsync(
        SecurityAlertEventArgs alert,
        CancellationToken cancellationToken = default)
    {
        if (!IsMonitoringActive)
            return;

        if (_session.CurrentState == SessionState.Locked)
            return;

        if (!TryBeginNotification())
            return;

        if (_settings.Current.DesktopNotifications)
        {
            await RecordAsync(SecurityEventType.NotificationSent, SecurityEventResult.Info,
                "Desktop notification shown: Unknown person detected.",
                cancellationToken, confidence: alert.Result.Similarity).ConfigureAwait(false);
        }

        NotificationRequested?.Invoke(this, alert);
    }

    /// <summary>
    /// Notification cooldown gate. Returns true (and consumes the slot) when
    /// no notification was sent within <c>NotificationCooldownSeconds</c>.
    /// <paramref name="utcNow"/> exists so tests do not have to sleep.
    /// </summary>
    public bool TryBeginNotification(DateTime? utcNow = null)
    {
        var now = utcNow ?? DateTime.UtcNow;
        var cooldown = Math.Max(0, _settings.Current.NotificationCooldownSeconds);

        if (_lastNotificationUtc is DateTime last
            && (now - last).TotalSeconds < cooldown)
        {
            return false;
        }

        _lastNotificationUtc = now;
        return true;
    }

    // ---------------------------------------------------------------------
    // Detection gate
    // ---------------------------------------------------------------------

    /// <summary>
    /// The frame pipeline records unknown-face events / snapshots / alerts
    /// only while this gate is open: the operator's Unknown face detection
    /// switch, background monitoring, and not-paused must all hold. Session
    /// lock is not part of the gate because capture itself stops on lock.
    /// </summary>
    public void UpdateDetectionGate()
    {
        var settings = _settings.Current;
        _processor.UnknownFaceDetectionEnabled =
            settings.UnknownFaceDetection
            && settings.BackgroundMonitoring
            && !_trayPaused;
    }

    // ---------------------------------------------------------------------
    // Health
    // ---------------------------------------------------------------------

    /// <summary>
    /// Publish the five component statuses. Reads are pure state mapping —
    /// the only I/O is a throttled database reachability probe; the health
    /// monitor only raises HealthChanged when a status actually changes.
    /// </summary>
    public async Task RefreshHealthAsync(CancellationToken cancellationToken = default)
    {
        var settings = _settings.Current;

        if (!settings.BackgroundMonitoring)
            _health.ReportDegraded(HealthComponent.BackgroundService, "Background monitoring is off in Settings.");
        else if (_trayPaused)
            _health.ReportDegraded(HealthComponent.BackgroundService, "Background monitoring is paused by the operator.");
        else
            _health.ReportHealthy(HealthComponent.BackgroundService, "Background monitoring is running.");

        switch (_camera.State)
        {
            case CameraState.Live:
                _health.ReportHealthy(HealthComponent.Camera, "Camera is capturing.");
                break;

            case CameraState.Connecting:
                _health.ReportDegraded(HealthComponent.Camera, "Camera is starting.");
                break;

            case CameraState.Paused:
                _health.ReportDegraded(HealthComponent.Camera, "Camera is paused while Windows is locked.");
                break;

            case CameraState.Error:
                _health.ReportUnavailable(HealthComponent.Camera,
                    _camera.FailureReasons.FirstOrDefault() ?? "Camera failed to start.");
                break;

            default:
                var expected = settings.BackgroundMonitoring
                               && !_trayPaused
                               && settings.MonitorCameraWhenUnlocked;
                if (expected)
                    _health.ReportDegraded(HealthComponent.Camera, "Camera is off while camera monitoring is enabled.");
                else
                    _health.ReportHealthy(HealthComponent.Camera, "Camera monitoring is off.");
                break;
        }

        if (_camera.IsEngineReady)
            _health.ReportHealthy(HealthComponent.RecognitionEngine, "Recognition engine is ready.");
        else
            _health.ReportUnavailable(HealthComponent.RecognitionEngine, "Recognition engine is not ready.");

        if (settings.DesktopNotifications)
            _health.ReportHealthy(HealthComponent.Notifications, "Desktop notifications are enabled.");
        else
            _health.ReportDegraded(HealthComponent.Notifications, "Desktop notifications are off in Settings.");

        // Database: a cheap reachability probe, never on every tick.
        if (_lastDatabaseProbeUtc is not DateTime lastProbe
            || DateTime.UtcNow - lastProbe >= DatabaseProbeInterval)
        {
            _lastDatabaseProbeUtc = DateTime.UtcNow;
            try
            {
                await _events.CountAsync(cancellationToken).ConfigureAwait(false);
                _health.ReportHealthy(HealthComponent.Database, "Database reachable.");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _health.ReportUnavailable(HealthComponent.Database,
                    "Database is unreachable: " + ex.Message);
            }
        }
    }

    // ---------------------------------------------------------------------
    // Retention
    // ---------------------------------------------------------------------

    /// <summary>
    /// Run the retention sweep at most once per <see cref="RetentionInterval"/>
    /// (and once at startup). The sweep deletes only expired events and expired
    /// snapshots; its summary goes to the app log — never into the event table.
    /// A retention failure is logged and never affects monitoring.
    /// </summary>
    public async Task RunRetentionIfDueAsync(CancellationToken cancellationToken = default)
    {
        if (_retention is null)
            return;

        if (_lastRetentionUtc is DateTime last
            && DateTime.UtcNow - last < RetentionInterval)
        {
            return;
        }

        _lastRetentionUtc = DateTime.UtcNow;

        try
        {
            await _retention.RunAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Retention sweep failed");
        }
    }

    // ---------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------

    private async Task RecordAsync(
        SecurityEventType type,
        SecurityEventResult result,
        string description,
        CancellationToken cancellationToken,
        double? confidence = null,
        SessionState? sessionState = null)
    {
        try
        {
            await _events.RecordAsync(type, result, description, confidence, cancellationToken,
                sessionState: sessionState).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to record {EventType}", type);
        }
    }

    /// <summary>
    /// Run a camera lifecycle call on the WPF dispatcher when one exists (the
    /// coordinator's state is UI-affine); inline when there is none — unit
    /// tests run without an <see cref="Application"/>.
    /// </summary>
    private async Task OnUiAsync(Func<Task> action, CancellationToken cancellationToken)
    {
        var dispatcher = Application.Current?.Dispatcher;

        if (dispatcher is null || dispatcher.CheckAccess())
        {
            await action().ConfigureAwait(false);
            return;
        }

        // DispatcherOperation<Task> awaits to the inner camera task.
        var operation = await dispatcher.InvokeAsync(action);
        await operation.ConfigureAwait(false);
    }
}
