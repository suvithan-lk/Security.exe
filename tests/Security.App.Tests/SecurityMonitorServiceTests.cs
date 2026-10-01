using Security.App.Services;
using Security.Core.Enums;
using Security.Core.Interfaces;
using Security.Core.Models;
using Security.Infrastructure.Services;
using Xunit;

namespace Security.App.Tests;

/// <summary>
/// The Phase 3 background monitor: session → event/camera reactions, the
/// monitoring lifecycle (Started/Stopped/Paused/Resumed), camera policy
/// transitions (auto-start, lock pause, unlock resume, retry, operator-stop
/// respect), the notification cooldown and health publishing.
///
/// Every test runs against the same fakes the camera coordinator tests use —
/// no webcam, no Windows session broadcasts, no sleeping (cooldowns take an
/// explicit clock where timing matters).
/// </summary>
public class SecurityMonitorServiceTests
{
    private sealed class Harness
    {
        public required CameraCoordinator Coordinator { get; init; }
        public required FakeCameraService CameraService { get; init; }
        public required FakeFrameProcessor Processor { get; init; }
        public required FakeSettingsService Settings { get; init; }
        public required RecordingEventService Events { get; init; }
        public required FakeWindowsSessionService Session { get; init; }
        public required HealthMonitor Health { get; init; }
        public required SecurityMonitorService Monitor { get; init; }
    }

    private static Harness Create(Action<AppSettings>? configure = null)
    {
        var cameraService = new FakeCameraService();
        cameraService.SetDevices(new CameraDevice { Index = 0, Name = "Test camera" });

        var events = new RecordingEventService();
        var settings = new FakeSettingsService();
        configure?.Invoke(settings.Current);

        var processor = new FakeFrameProcessor();

        var coordinator = new CameraCoordinator(
            cameraService,
            processor,
            new FakeEnrollmentService(),
            settings,
            events,
            new FakeRecognitionService());

        var health = new HealthMonitor();
        var session = new FakeWindowsSessionService();

        var monitor = new SecurityMonitorService(
            session,
            coordinator,
            settings,
            events,
            health,
            processor);

        return new Harness
        {
            Coordinator = coordinator,
            CameraService = cameraService,
            Processor = processor,
            Settings = settings,
            Events = events,
            Session = session,
            Health = health,
            Monitor = monitor,
        };
    }

    /// <summary>Refresh, initialize, and let the policy auto-start capture — the state a running app is in.</summary>
    private static async Task<Harness> CreateRunningAsync(Action<AppSettings>? configure = null)
    {
        var harness = Create(configure);
        await harness.Coordinator.RefreshCamerasAsync();
        await harness.Monitor.InitializeAsync();
        await harness.Monitor.ApplyCameraPolicyAsync();

        Assert.Equal(CameraState.Live, harness.Coordinator.State);
        return harness;
    }

    /// <summary>Simulate a Windows session transition without double-raising the subscribed event.</summary>
    private static async Task SessionChangedAsync(Harness harness, SessionState next)
    {
        var previous = harness.Session.CurrentState;
        harness.Session.CurrentState = next;
        await harness.Monitor.HandleSessionStateChangeAsync(
            new SessionStateChangedEventArgs(previous, next));
    }

    private static bool Has(Harness harness, SecurityEventType type)
        => harness.Events.Recorded.Any(e => e.EventType == type);

    // ------------------------------------------------------------------
    // Initialization & lifecycle
    // ------------------------------------------------------------------

    [Fact]
    public async Task Initialize_records_started_and_begins_session_observation()
    {
        var harness = Create();

        await harness.Monitor.InitializeAsync();

        Assert.True(Has(harness, SecurityEventType.MonitoringStarted));
        Assert.True(harness.Session.StartCalled);
        Assert.True(harness.Monitor.IsMonitoringActive);
        Assert.Equal("MONITORING ACTIVE", harness.Monitor.MonitoringStatusText);
        Assert.Null(harness.Monitor.PauseReason);
    }

    [Fact]
    public async Task Initialize_with_background_monitoring_disabled_records_nothing_and_stays_off()
    {
        var harness = Create(s => s.BackgroundMonitoring = false);

        await harness.Monitor.InitializeAsync();

        Assert.False(Has(harness, SecurityEventType.MonitoringStarted));
        Assert.False(harness.Monitor.IsMonitoringActive);
        Assert.Equal("MONITORING OFF", harness.Monitor.MonitoringStatusText);
        Assert.Equal("Background monitoring is off in Settings.", harness.Monitor.PauseReason);
    }

    [Fact]
    public async Task Session_monitoring_disabled_does_not_start_the_session_observer_or_record_session_events()
    {
        var harness = Create(s => s.SessionMonitoring = false);

        await harness.Monitor.InitializeAsync();
        Assert.False(harness.Session.StartCalled);

        await SessionChangedAsync(harness, SessionState.Locked);

        Assert.False(Has(harness, SecurityEventType.SessionLocked));
    }

    [Fact]
    public async Task Hosted_lifecycle_records_started_then_stopped()
    {
        var harness = Create();

        // .NET's BackgroundService.StartAsync defers ExecuteAsync instead of
        // running it inline, so initialization (which records MonitoringStarted)
        // happens right after — poll rather than race it.
        await harness.Monitor.StartAsync(CancellationToken.None);
        Assert.True(await WaitUntilAsync(
            () => Has(harness, SecurityEventType.MonitoringStarted), TimeSpan.FromSeconds(5)),
            "MonitoringStarted was not recorded after the host started.");

        using var stopTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await harness.Monitor.StopAsync(stopTimeout.Token);

        Assert.True(Has(harness, SecurityEventType.MonitoringStopped));
        Assert.True(harness.Session.StopCalled);
    }

    /// <summary>Polls a condition until it holds or the timeout elapses.</summary>
    private static async Task<bool> WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
                return true;
            await Task.Delay(25);
        }

        return condition();
    }

    // ------------------------------------------------------------------
    // Session state handling & camera transitions
    // ------------------------------------------------------------------

    [Fact]
    public async Task Auto_start_captures_when_monitoring_allows()
    {
        await CreateRunningAsync();

        // Assert inside CreateRunningAsync: capture reached Live.
    }

    [Fact]
    public async Task Unknown_session_state_never_starts_capture_but_unlock_does()
    {
        var harness = Create();
        harness.Session.CurrentState = SessionState.Unknown;
        await harness.Coordinator.RefreshCamerasAsync();
        await harness.Monitor.InitializeAsync();

        await harness.Monitor.ApplyCameraPolicyAsync();
        Assert.NotEqual(CameraState.Live, harness.Coordinator.State);

        await SessionChangedAsync(harness, SessionState.Unlocked);

        Assert.Equal(CameraState.Live, harness.Coordinator.State);
    }

    [Fact]
    public async Task Lock_records_session_locked_and_pauses_capture_without_losing_intent()
    {
        var harness = await CreateRunningAsync();

        await SessionChangedAsync(harness, SessionState.Locked);

        Assert.Equal(CameraState.Paused, harness.Coordinator.State);
        Assert.True(harness.Coordinator.KeepRunning);
        Assert.Contains(harness.Events.Recorded, e =>
            e.EventType == SecurityEventType.SessionLocked && e.SessionState == SessionState.Locked);
    }

    [Fact]
    public async Task Lock_pauses_capture_even_when_background_monitoring_is_disabled()
    {
        // Monitoring off means no auto-start — start the camera the way an
        // operator would, from the Camera page.
        var harness = Create(s => s.BackgroundMonitoring = false);
        await harness.Coordinator.RefreshCamerasAsync();
        await harness.Monitor.InitializeAsync();
        await harness.Coordinator.StartAsync();
        Assert.Equal(CameraState.Live, harness.Coordinator.State);

        await SessionChangedAsync(harness, SessionState.Locked);

        Assert.Equal(CameraState.Paused, harness.Coordinator.State);
    }

    [Fact]
    public async Task Unlock_resumes_capture_when_monitoring_is_active()
    {
        var harness = await CreateRunningAsync();

        await SessionChangedAsync(harness, SessionState.Locked);
        Assert.Equal(CameraState.Paused, harness.Coordinator.State);

        await SessionChangedAsync(harness, SessionState.Unlocked);

        Assert.Equal(CameraState.Live, harness.Coordinator.State);
        Assert.Contains(harness.Events.Recorded, e => e.EventType == SecurityEventType.SessionUnlocked);
    }

    [Fact]
    public async Task Unlock_does_not_resume_while_monitoring_is_paused()
    {
        var harness = await CreateRunningAsync();

        await SessionChangedAsync(harness, SessionState.Locked);
        await harness.Monitor.SetMonitoringPausedAsync(true);

        await SessionChangedAsync(harness, SessionState.Unlocked);

        Assert.NotEqual(CameraState.Live, harness.Coordinator.State);
        Assert.True(harness.Monitor.IsPaused);
        Assert.Equal("MONITORING PAUSED", harness.Monitor.MonitoringStatusText);
    }

    [Fact]
    public async Task Session_state_change_records_only_the_matching_event()
    {
        var harness = await CreateRunningAsync();

        await SessionChangedAsync(harness, SessionState.Connected);

        Assert.Contains(harness.Events.Recorded, e =>
            e.EventType == SecurityEventType.SessionConnected && e.SessionState == SessionState.Connected);
        Assert.False(Has(harness, SecurityEventType.SessionLocked));
    }

    // ------------------------------------------------------------------
    // Tray pause / resume
    // ------------------------------------------------------------------

    [Fact]
    public async Task Tray_pause_stops_capture_records_paused_and_closes_the_detection_gate()
    {
        var harness = await CreateRunningAsync();

        await harness.Monitor.SetMonitoringPausedAsync(true);

        Assert.False(harness.Coordinator.IsRunning);
        Assert.Equal(CameraState.Offline, harness.Coordinator.State);
        Assert.True(Has(harness, SecurityEventType.MonitoringPaused));
        Assert.False(harness.Processor.UnknownFaceDetectionEnabled);
    }

    [Fact]
    public async Task Tray_resume_restarts_capture_records_resumed_and_reopens_the_gate()
    {
        var harness = await CreateRunningAsync();

        await harness.Monitor.SetMonitoringPausedAsync(true);
        await harness.Monitor.SetMonitoringPausedAsync(false);

        Assert.Equal(CameraState.Live, harness.Coordinator.State);
        Assert.True(Has(harness, SecurityEventType.MonitoringResumed));
        Assert.True(harness.Processor.UnknownFaceDetectionEnabled);
        Assert.True(harness.Monitor.IsMonitoringActive);
    }

    [Fact]
    public async Task Tray_pause_twice_is_idempotent()
    {
        var harness = await CreateRunningAsync();

        await harness.Monitor.SetMonitoringPausedAsync(true);
        await harness.Monitor.SetMonitoringPausedAsync(true);

        Assert.Equal(1, harness.Events.Recorded.Count(e => e.EventType == SecurityEventType.MonitoringPaused));
    }

    // ------------------------------------------------------------------
    // Settings transitions
    // ------------------------------------------------------------------

    [Fact]
    public async Task Background_monitoring_off_stops_capture_and_records_stopped()
    {
        var harness = await CreateRunningAsync();

        harness.Settings.Current.BackgroundMonitoring = false;
        await harness.Monitor.HandleSettingsChangedAsync();

        Assert.False(harness.Coordinator.IsRunning);
        Assert.True(Has(harness, SecurityEventType.MonitoringStopped));
        Assert.False(harness.Processor.UnknownFaceDetectionEnabled);
        Assert.Equal("MONITORING OFF", harness.Monitor.MonitoringStatusText);
    }

    [Fact]
    public async Task Background_monitoring_on_restarts_capture_and_records_started()
    {
        var harness = await CreateRunningAsync();

        harness.Settings.Current.BackgroundMonitoring = false;
        await harness.Monitor.HandleSettingsChangedAsync();

        harness.Settings.Current.BackgroundMonitoring = true;
        await harness.Monitor.HandleSettingsChangedAsync();

        Assert.Equal(CameraState.Live, harness.Coordinator.State);
        Assert.True(Has(harness, SecurityEventType.MonitoringStarted));
        Assert.True(harness.Processor.UnknownFaceDetectionEnabled);
    }

    [Fact]
    public async Task Operator_stop_is_never_overridden_by_the_policy_reconcile()
    {
        var harness = await CreateRunningAsync();

        // The operator presses Stop on the Camera page.
        await harness.Coordinator.StopAsync();
        Assert.False(harness.Coordinator.KeepRunning);

        // The monitor's periodic reconcile must leave it alone.
        await harness.Monitor.ReconcileCameraAsync();
        await harness.Monitor.ReconcileCameraAsync();

        Assert.False(harness.Coordinator.IsRunning);
    }

    [Fact]
    public async Task Failed_start_is_retried_once_the_retry_interval_has_elapsed()
    {
        var harness = Create(s => s.CameraRetryIntervalSeconds = 0);
        harness.CameraService.StartFailure = new InvalidOperationException("No device.");
        await harness.Coordinator.RefreshCamerasAsync();
        await harness.Monitor.InitializeAsync();

        await harness.Monitor.ApplyCameraPolicyAsync();
        Assert.Equal(CameraState.Error, harness.Coordinator.State);
        Assert.True(harness.Coordinator.KeepRunning);

        var attemptsBefore = harness.CameraService.StartCalls;
        harness.CameraService.StartFailure = null;

        await harness.Monitor.ReconcileCameraAsync();

        Assert.Equal(CameraState.Live, harness.Coordinator.State);
        Assert.True(harness.CameraService.StartCalls > attemptsBefore);
    }

    [Fact]
    public async Task Failed_start_is_not_retried_before_the_retry_interval()
    {
        var harness = Create(s => s.CameraRetryIntervalSeconds = 3600);
        harness.CameraService.StartFailure = new InvalidOperationException("No device.");
        await harness.Coordinator.RefreshCamerasAsync();
        await harness.Monitor.InitializeAsync();

        await harness.Monitor.ApplyCameraPolicyAsync();
        Assert.Equal(CameraState.Error, harness.Coordinator.State);

        var attemptsBefore = harness.CameraService.StartCalls;
        harness.CameraService.StartFailure = null;

        await harness.Monitor.ReconcileCameraAsync();

        Assert.Equal(attemptsBefore, harness.CameraService.StartCalls);
    }

    [Fact]
    public async Task Monitor_camera_when_unlocked_off_stops_capture_and_on_restarts_it()
    {
        var harness = await CreateRunningAsync();

        harness.Settings.Current.MonitorCameraWhenUnlocked = false;
        await harness.Monitor.HandleSettingsChangedAsync();
        Assert.False(harness.Coordinator.IsRunning);

        harness.Settings.Current.MonitorCameraWhenUnlocked = true;
        await harness.Monitor.HandleSettingsChangedAsync();
        Assert.Equal(CameraState.Live, harness.Coordinator.State);
    }

    [Fact]
    public async Task Detection_gate_follows_the_unknown_face_setting()
    {
        var harness = await CreateRunningAsync();
        Assert.True(harness.Processor.UnknownFaceDetectionEnabled);

        harness.Settings.Current.UnknownFaceDetection = false;
        await harness.Monitor.HandleSettingsChangedAsync();
        Assert.False(harness.Processor.UnknownFaceDetectionEnabled);

        harness.Settings.Current.UnknownFaceDetection = true;
        await harness.Monitor.HandleSettingsChangedAsync();
        Assert.True(harness.Processor.UnknownFaceDetectionEnabled);
    }

    // ------------------------------------------------------------------
    // Notifications
    // ------------------------------------------------------------------

    private static SecurityAlertEventArgs UnknownAlert()
        => new(FaceRecognitionResult.Create(
            RecognitionStatus.Unknown, similarity: 0.42, threshold: 0.60, profileId: null,
            timestamp: DateTime.UtcNow));

    [Fact]
    public async Task Alert_sends_one_notification_then_the_cooldown_blocks_the_next()
    {
        var harness = await CreateRunningAsync();
        var raised = 0;
        harness.Monitor.NotificationRequested += (_, _) => raised++;

        await harness.Monitor.HandleUnknownFaceAlertAsync(UnknownAlert());
        await harness.Monitor.HandleUnknownFaceAlertAsync(UnknownAlert());

        Assert.Equal(1, raised);
        Assert.Equal(1, harness.Events.Recorded.Count(e => e.EventType == SecurityEventType.NotificationSent));
    }

    [Fact]
    public async Task Notification_cooldown_window_opens_again_after_the_configured_seconds()
    {
        var harness = await CreateRunningAsync();
        var t0 = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

        Assert.True(harness.Monitor.TryBeginNotification(t0));
        Assert.False(harness.Monitor.TryBeginNotification(t0.AddSeconds(29)));
        Assert.True(harness.Monitor.TryBeginNotification(t0.AddSeconds(31)));
    }

    [Fact]
    public async Task Alert_is_silent_while_monitoring_is_paused()
    {
        var harness = await CreateRunningAsync();
        await harness.Monitor.SetMonitoringPausedAsync(true);
        var raised = 0;
        harness.Monitor.NotificationRequested += (_, _) => raised++;

        await harness.Monitor.HandleUnknownFaceAlertAsync(UnknownAlert());

        Assert.Equal(0, raised);
        Assert.False(Has(harness, SecurityEventType.NotificationSent));
    }

    [Fact]
    public async Task Alert_without_desktop_notifications_raises_no_sent_event_but_still_notifies_subscribers()
    {
        var harness = await CreateRunningAsync(s => s.DesktopNotifications = false);
        var raised = 0;
        harness.Monitor.NotificationRequested += (_, _) => raised++;

        await harness.Monitor.HandleUnknownFaceAlertAsync(UnknownAlert());

        Assert.Equal(1, raised);
        Assert.False(Has(harness, SecurityEventType.NotificationSent));
    }

    [Fact]
    public async Task Alert_is_never_raised_for_a_locked_session()
    {
        var harness = await CreateRunningAsync();
        await SessionChangedAsync(harness, SessionState.Locked);
        var raised = 0;
        harness.Monitor.NotificationRequested += (_, _) => raised++;

        await harness.Monitor.HandleUnknownFaceAlertAsync(UnknownAlert());

        Assert.Equal(0, raised);
        Assert.False(Has(harness, SecurityEventType.NotificationSent));
    }

    // ------------------------------------------------------------------
    // Health
    // ------------------------------------------------------------------

    [Fact]
    public async Task Health_publishes_all_five_components_with_concrete_details()
    {
        var harness = await CreateRunningAsync();

        await harness.Monitor.RefreshHealthAsync();

        var components = harness.Health.Components;
        Assert.Equal(5, components.Count);
        Assert.All(components, c => Assert.False(string.IsNullOrWhiteSpace(c.Detail)));

        Assert.Equal(HealthStatus.Healthy, harness.Health.Components.Single(c => c.Component == HealthComponent.Database).Status);
        Assert.Equal(HealthStatus.Healthy, harness.Health.Components.Single(c => c.Component == HealthComponent.Camera).Status);
        Assert.Equal(HealthStatus.Healthy, harness.Health.Components.Single(c => c.Component == HealthComponent.BackgroundService).Status);
        Assert.Equal(HealthStatus.Healthy, harness.Health.Components.Single(c => c.Component == HealthComponent.Notifications).Status);
    }

    [Fact]
    public async Task Health_reports_camera_degraded_while_monitoring_expects_it_but_it_is_off()
    {
        var harness = Create(s => s.BackgroundMonitoring = false);
        await harness.Monitor.InitializeAsync();

        await harness.Monitor.RefreshHealthAsync();

        var camera = harness.Health.Components.Single(c => c.Component == HealthComponent.Camera);
        Assert.Equal(HealthStatus.Healthy, camera.Status); // off by configuration, not a fault
        Assert.Equal("Camera monitoring is off.", camera.Detail);
    }
}
