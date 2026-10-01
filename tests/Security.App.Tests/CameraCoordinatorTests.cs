using System.IO;
using Security.App.Services;
using Security.Core.Enums;
using Security.Core.Models;
using Xunit;

namespace Security.App.Tests;

/// <summary>
/// The camera page's state machine.
///
/// These tests exist because the states the operator sees (OFFLINE /
/// CONNECTING / LIVE / ERROR) are a separate contract from
/// <c>ICameraService.IsRunning</c>, and Phase 1 collapsed the distinction: a
/// refused device open reported success, so the page never reached the
/// CAMERA UNAVAILABLE panel it was supposed to show. Every case here runs
/// against fakes, so no webcam is required.
/// </summary>
public class CameraCoordinatorTests
{
    private static (CameraCoordinator Coordinator, FakeCameraService Camera, RecordingEventService Events) Create()
    {
        var camera = new FakeCameraService();
        var events = new RecordingEventService();

        var coordinator = new CameraCoordinator(
            camera,
            new FakeFrameProcessor(),
            new FakeEnrollmentService(),
            new FakeSettingsService(),
            events,
            new FakeRecognitionService());

        return (coordinator, camera, events);
    }

    private static CameraDevice ACamera(int index = 0)
        => new() { Index = index, Name = "Test camera" };

    // --- No camera ----------------------------------------------------------

    [Fact]
    public async Task Refresh_with_no_devices_reports_offline_and_says_what_to_do()
    {
        var (coordinator, camera, _) = Create();
        camera.SetDevices();

        await coordinator.RefreshCamerasAsync();

        Assert.Empty(coordinator.Cameras);
        Assert.Equal(CameraState.Offline, coordinator.State);
        Assert.Equal("OFFLINE", coordinator.StatusBadgeText);
        Assert.True(coordinator.HasFailureReasons);
        Assert.Contains(coordinator.FailureReasons, r => r.Contains("Connect a camera", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Refresh_that_throws_ends_in_the_error_state_with_permission_guidance()
    {
        var (coordinator, camera, _) = Create();
        camera.EnumerationFails = true;

        await coordinator.RefreshCamerasAsync();

        Assert.Equal(CameraState.Error, coordinator.State);
        Assert.Equal("ERROR", coordinator.StatusBadgeText);
        Assert.True(coordinator.HasFailureReasons);
        Assert.Contains(coordinator.FailureReasons, r => r.Contains("Privacy", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Refresh_with_devices_reports_a_selectable_camera()
    {
        var (coordinator, camera, _) = Create();
        camera.SetDevices(ACamera(0), ACamera(1));

        await coordinator.RefreshCamerasAsync();

        Assert.Equal(2, coordinator.Cameras.Count);
        Assert.Same(coordinator.Cameras[0], coordinator.SelectedCamera);
        Assert.Equal(CameraState.Offline, coordinator.State);
        Assert.False(coordinator.HasFailureReasons);
    }

    // --- Start --------------------------------------------------------------

    [Fact]
    public async Task Successful_start_reaches_live_and_records_the_event()
    {
        var (coordinator, camera, events) = Create();
        camera.SetDevices(ACamera());
        await coordinator.RefreshCamerasAsync();

        await coordinator.StartAsync();

        Assert.Equal(CameraState.Live, coordinator.State);
        Assert.Equal("LIVE", coordinator.StatusBadgeText);
        Assert.True(coordinator.IsRunning);
        Assert.False(coordinator.HasFailureReasons);
        Assert.Contains(events.Recorded, e => e.EventType == SecurityEventType.CameraStarted);
    }

    [Fact]
    public async Task Refused_device_open_ends_in_error_and_not_stuck_connecting()
    {
        var (coordinator, camera, _) = Create();
        camera.SetDevices(ACamera());
        await coordinator.RefreshCamerasAsync();
        camera.StartFailure = new IOException("The camera is in use by another application.");

        await coordinator.StartAsync();

        // Regression: this used to leave State == Connecting, so the badge
        // spun forever and CAMERA UNAVAILABLE (with Retry) never appeared.
        Assert.Equal(CameraState.Error, coordinator.State);
        Assert.Equal("ERROR", coordinator.StatusBadgeText);
        Assert.False(coordinator.IsRunning);
        Assert.False(camera.IsRunning);
        Assert.True(coordinator.HasFailureReasons);
    }

    [Fact]
    public async Task Refused_device_open_lists_concrete_causes_an_operator_can_act_on()
    {
        var (coordinator, camera, _) = Create();
        camera.SetDevices(ACamera());
        await coordinator.RefreshCamerasAsync();
        camera.StartFailure = new IOException("The camera is in use by another application.");

        await coordinator.StartAsync();

        Assert.True(coordinator.FailureReasons.Count >= 3);
        Assert.Contains(coordinator.FailureReasons, r => r.Contains("another application", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(coordinator.FailureReasons, r => r.Contains("unplugged", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(coordinator.FailureReasons, r => r.Contains("driver", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Permission_denied_open_reports_how_to_unblock_windows_camera_access()
    {
        var (coordinator, camera, _) = Create();
        camera.SetDevices(ACamera());
        await coordinator.RefreshCamerasAsync();
        camera.StartFailure = new UnauthorizedAccessException("Access to the path is denied.");

        await coordinator.StartAsync();

        Assert.Equal(CameraState.Error, coordinator.State);
        Assert.Equal("Camera permission denied", coordinator.CameraStatus);
        Assert.Contains(coordinator.FailureReasons, r => r.Contains("Privacy & security", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Successful_start_clears_the_previous_failure_reasons()
    {
        var (coordinator, camera, _) = Create();
        camera.SetDevices(ACamera());
        await coordinator.RefreshCamerasAsync();

        camera.StartFailure = new IOException("busy");
        await coordinator.StartAsync();
        Assert.True(coordinator.HasFailureReasons);

        camera.StartFailure = null;
        await coordinator.StartAsync();
        Assert.Equal(CameraState.Live, coordinator.State);
        Assert.False(coordinator.HasFailureReasons);
    }

    [Fact]
    public async Task Start_while_running_is_a_no_op()
    {
        var (coordinator, camera, _) = Create();
        camera.SetDevices(ACamera());
        await coordinator.RefreshCamerasAsync();

        await coordinator.StartAsync();
        await coordinator.StartAsync();

        Assert.Equal(1, camera.StartCalls);
    }

    // --- Stop ---------------------------------------------------------------

    [Fact]
    public async Task Stop_returns_offline_and_clears_rate_resolution_and_verdict()
    {
        var (coordinator, camera, events) = Create();
        camera.SetDevices(ACamera());
        await coordinator.RefreshCamerasAsync();
        await coordinator.StartAsync();

        await coordinator.StopAsync();

        Assert.Equal(CameraState.Offline, coordinator.State);
        Assert.Equal("OFFLINE", coordinator.StatusBadgeText);
        Assert.False(coordinator.IsRunning);
        Assert.False(camera.IsRunning);
        Assert.Equal(0, coordinator.Fps);
        Assert.Equal(0, coordinator.SourceWidth);
        Assert.Equal(0, coordinator.SourceHeight);
        Assert.Null(coordinator.LastRecognition);
        Assert.False(coordinator.HasFailureReasons);
        Assert.Contains(events.Recorded, e => e.EventType == SecurityEventType.CameraStopped);
    }

    [Fact]
    public async Task Stop_when_already_stopped_does_not_record_a_second_camera_stopped_event()
    {
        var (coordinator, camera, events) = Create();
        camera.SetDevices(ACamera());
        await coordinator.RefreshCamerasAsync();

        await coordinator.StopAsync();

        Assert.DoesNotContain(events.Recorded, e => e.EventType == SecurityEventType.CameraStopped);
        Assert.Equal(CameraState.Offline, coordinator.State);
    }

    // --- Restart ------------------------------------------------------------

    [Fact]
    public async Task Restart_recovers_a_camera_that_failed_to_open()
    {
        var (coordinator, camera, _) = Create();
        camera.SetDevices(ACamera());
        await coordinator.RefreshCamerasAsync();

        camera.StartsToFail = 1;
        await coordinator.StartAsync();
        Assert.Equal(CameraState.Error, coordinator.State);

        camera.StartsToFail = 0;
        await coordinator.RestartAsync();

        Assert.Equal(CameraState.Live, coordinator.State);
        Assert.True(coordinator.IsRunning);
        Assert.False(coordinator.HasFailureReasons);
    }

    [Fact]
    public async Task Restart_of_a_running_camera_reopens_it_once()
    {
        var (coordinator, camera, _) = Create();
        camera.SetDevices(ACamera());
        await coordinator.RefreshCamerasAsync();
        await coordinator.StartAsync();

        await coordinator.RestartAsync();

        Assert.Equal(CameraState.Live, coordinator.State);
        Assert.True(coordinator.IsRunning);
        Assert.Equal(2, camera.StartCalls);
        Assert.Equal(1, camera.StopCalls);
    }

    // --- Badge --------------------------------------------------------------

    [Fact]
    public async Task Status_badge_tracks_every_transition_the_page_can_show()
    {
        var (coordinator, camera, _) = Create();

        Assert.Equal("OFFLINE", coordinator.StatusBadgeText);
        Assert.True(coordinator.IsOffline);

        camera.SetDevices(ACamera());
        await coordinator.RefreshCamerasAsync();
        Assert.Equal("OFFLINE", coordinator.StatusBadgeText);

        camera.StartFailure = new IOException("busy");
        await coordinator.StartAsync();
        Assert.Equal("ERROR", coordinator.StatusBadgeText);
        Assert.True(coordinator.IsError);

        camera.StartFailure = null;
        await coordinator.StartAsync();
        Assert.Equal("LIVE", coordinator.StatusBadgeText);
        Assert.True(coordinator.IsLive);

        await coordinator.StopAsync();
        Assert.Equal("OFFLINE", coordinator.StatusBadgeText);
    }

    [Fact]
    public void Starting_state_before_anything_happened_is_offline()
    {
        var (coordinator, _, _) = Create();

        Assert.Equal(CameraState.Offline, coordinator.State);
        Assert.Equal("OFFLINE", coordinator.StatusBadgeText);
        Assert.False(coordinator.IsRunning);
        Assert.False(coordinator.HasFailureReasons);
    }
}
