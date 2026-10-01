using OpenCvSharp;
using Security.Core.Interfaces;
using Security.Core.Models;
using Security.Face.Pipeline;
using Xunit;

namespace Security.Face.Tests;

/// <summary>
/// Regression tests for the frame pipeline's attach/detach lifecycle.
///
/// A real runtime failure was observed: starting the camera, stopping it, and
/// starting it again threw SemaphoreFullException out of Detach(), which
/// surfaced as "Camera start failed" and left the device open. Detach() has to
/// be safe to call repeatedly, including when nothing is attached.
/// </summary>
public class FrameProcessorTests
{
    private static FrameProcessor CreateProcessor() => new(
        new UnusedDetection(),
        new UnusedRecognition(),
        new UnusedEvents(),
        new UnusedSettings());

    [Fact]
    public void Detach_can_be_called_repeatedly_without_throwing()
    {
        using var processor = CreateProcessor();

        // _wake has a maximum count of 1, so a second unconditional Release()
        // used to overflow the semaphore.
        processor.Detach();
        processor.Detach();
        processor.Detach();
    }

    [Fact]
    public void Attach_detach_attach_cycle_does_not_throw()
    {
        using var processor = CreateProcessor();
        using var camera = new FakeCamera();

        // Exactly the sequence that failed in the running app:
        // start → stop → start.
        processor.Attach(camera);
        Assert.True(processor.IsProcessing);

        processor.Detach();
        Assert.False(processor.IsProcessing);

        processor.Attach(camera);
        Assert.True(processor.IsProcessing);

        processor.Detach();
        Assert.False(processor.IsProcessing);
    }

    [Fact]
    public void Attach_unsubscribes_so_repeated_attach_does_not_stack_handlers()
    {
        using var processor = CreateProcessor();
        using var camera = new FakeCamera();

        for (var i = 0; i < 5; i++)
        {
            processor.Attach(camera);
            processor.Detach();
        }

        // One Detach() left exactly one (now unsubscribed) handler behind.
        Assert.Equal(0, camera.FrameReceivedHandlerCount);
    }

    [Fact]
    public void Dispose_is_safe_when_attached_and_when_idle()
    {
        var attached = CreateProcessor();
        using (var camera = new FakeCamera())
        {
            attached.Attach(camera);
            attached.Dispose();
            attached.Dispose(); // idempotent
        }

        var idle = CreateProcessor();
        idle.Dispose();
    }

    [Fact]
    public void Attach_after_dispose_throws()
    {
        var processor = CreateProcessor();
        using var camera = new FakeCamera();

        processor.Attach(camera);
        processor.Dispose();

        Assert.Throws<ObjectDisposedException>(() => processor.Attach(camera));
    }

    [Fact]
    public void Attach_rejects_null_camera()
    {
        using var processor = CreateProcessor();
        Assert.Throws<ArgumentNullException>(() => processor.Attach(null!));
    }

    #region Doubles

    /// <summary>
    /// Camera stand-in that records subscription counts so handler leaks are
    /// visible. Never produces frames — these tests exercise lifecycle only.
    /// </summary>
    private sealed class FakeCamera : ICameraService
    {
        public event EventHandler<CameraFrameEventArgs>? FrameReceived;

        public event EventHandler<CameraErrorEventArgs>? CameraError
        {
            add { }
            remove { }
        }

        public int FrameReceivedHandlerCount => FrameReceived?.GetInvocationList().Length ?? 0;

        public bool IsRunning { get; private set; }

        public CameraDevice? SelectedCamera => null;

        public Task<IReadOnlyList<CameraDevice>> GetAvailableCamerasAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<CameraDevice>>(Array.Empty<CameraDevice>());

        public Task StartAsync(CameraDevice? camera = null, CancellationToken cancellationToken = default)
        {
            IsRunning = true;
            return Task.CompletedTask;
        }

        public Task StartAsync(int cameraIndex, CancellationToken cancellationToken = default)
            => StartAsync(null, cancellationToken);

        public Task RestartAsync(CancellationToken cancellationToken = default)
        {
            IsRunning = true;
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            IsRunning = false;
            return Task.CompletedTask;
        }

        public Mat? CaptureFrame() => null;

        public void Dispose() => IsRunning = false;
    }

    private sealed class UnusedDetection : IFaceDetectionService
    {
        public bool IsReady => false;

        public FaceDetectionResult Detect(Mat frame)
            => throw new InvalidOperationException("Detection is not expected in these tests.");

        public FaceDetectionResult DetectPrimary(Mat frame)
            => throw new InvalidOperationException("Detection is not expected in these tests.");

        public void Dispose()
        {
        }
    }

    private sealed class UnusedRecognition : IFaceRecognitionService
    {
        public bool IsReady => false;

        public bool HasEnrolledProfile => false;

        public double Threshold => 0.60;

        public Task<FaceRecognitionResult> RecognizeAsync(Mat frame, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Recognition is not expected in these tests.");
    }

    private sealed class UnusedEvents : ISecurityEventService
    {
        public event EventHandler<Core.Entities.SecurityEvent>? EventRecorded
        {
            add { }
            remove { }
        }

        public Task<IReadOnlyList<Core.Entities.SecurityEvent>> GetRecentAsync(int count = 100, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<Core.Entities.SecurityEvent>>(Array.Empty<Core.Entities.SecurityEvent>());

        public Task<IReadOnlyList<Core.Entities.SecurityEvent>> QueryAsync(
            Core.Enums.SecurityEventType? type = null,
            DateTime? fromUtc = null,
            DateTime? toUtc = null,
            int limit = 500,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<Core.Entities.SecurityEvent>>(Array.Empty<Core.Entities.SecurityEvent>());

        public Task<int> CountAsync(CancellationToken cancellationToken = default) => Task.FromResult(0);

        public Task ClearAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<Core.Entities.SecurityEvent> RecordAsync(
            Core.Enums.SecurityEventType eventType,
            Core.Enums.SecurityEventResult result,
            string description,
            double? confidence = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new Core.Entities.SecurityEvent());
    }

    private sealed class UnusedSettings : ISettingsService
    {
        public AppSettings Current { get; } = new();

        public RecognitionOptions Recognition { get; } = new();

        public event EventHandler? SettingsChanged
        {
            add { }
            remove { }
        }

        public Task LoadAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task SaveRecognitionAsync(RecognitionOptions options, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    #endregion
}
