using System.Diagnostics;
using OpenCvSharp;
using Security.Core.Interfaces;
using Security.Core.Models;
using Security.Face.Pipeline;
using Xunit;

namespace Security.Face.Tests;

/// <summary>
/// Regression tests for the frame ANALYSIS path.
///
/// A real runtime failure was observed: the pipeline attached, reported
/// <see cref="IFrameProcessor.IsProcessing"/> as true, and consumed frames — but
/// <see cref="IFrameProcessor.FrameAnalyzed"/> never fired, so detection showed
/// no bounding box, recognition never produced a verdict, and enrollment
/// received no frames and stalled at 0 samples forever.
///
/// Root cause: the throttles seeded their timestamps with
/// <c>long.MinValue</c> and then evaluated <c>now - lastTimestamp</c>. C#
/// integer arithmetic is unchecked, so that subtraction overflowed to a
/// NEGATIVE number, making the "interval has not elapsed yet" branch taken on
/// every single frame — permanently.
///
/// These tests feed real frames through the real worker loop and assert that
/// analysis actually happens.
/// </summary>
public class FrameProcessorAnalysisTests
{
    [Fact]
    public async Task First_frame_is_analysed_immediately()
    {
        using var detector = new StubDetection();
        using var processor = new FrameProcessor(
            detector, new StubRecognition(), new StubEvents(), new StubSettings());
        using var camera = new EmittingCamera();

        var tcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var count = 0;
        processor.FrameAnalyzed += (_, _) =>
        {
            Interlocked.Increment(ref count);
            tcs.TrySetResult(count);
        };

        processor.Attach(camera);
        camera.Emit();

        var completed = await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(3)));
        Assert.True(ReferenceEquals(completed, tcs.Task),
            "FrameAnalyzed never fired for the first frame — the analysis throttle is stuck.");

        processor.Detach();
    }

    [Fact]
    public async Task Sustained_frames_produce_sustained_analysis()
    {
        using var detector = new StubDetection();
        using var processor = new FrameProcessor(
            detector, new StubRecognition(), new StubEvents(), new StubSettings());
        using var camera = new EmittingCamera();

        var count = 0;
        processor.FrameAnalyzed += (_, _) => Interlocked.Increment(ref count);

        processor.Attach(camera);

        // ~20 fps for 1.5 s. Detection is throttled to 8 fps, so we expect a
        // handful of analyses — but emphatically not zero.
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < TimeSpan.FromMilliseconds(1500))
        {
            camera.Emit();
            await Task.Delay(50);
        }

        Assert.True(count > 0,
            $"FrameAnalyzed fired {count} times over 1.5 s of frames — analysis is not running.");
        Assert.True(count <= 40,
            $"FrameAnalyzed fired {count} times; the detection throttle is not limiting the rate.");

        processor.Detach();
    }

    [Fact]
    public async Task Analysis_survives_a_detach_and_reattach()
    {
        using var detector = new StubDetection();
        using var processor = new FrameProcessor(
            detector, new StubRecognition(), new StubEvents(), new StubSettings());
        using var camera = new EmittingCamera();

        var count = 0;
        processor.FrameAnalyzed += (_, _) => Interlocked.Increment(ref count);

        processor.Attach(camera);
        processor.Detach();
        processor.Attach(camera);

        camera.Emit();

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(3);
        while (count == 0 && DateTime.UtcNow < deadline)
            await Task.Delay(50);

        Assert.True(count > 0, "No analysis after a detach/reattach cycle.");
        processor.Detach();
    }

    #region Doubles

    /// <summary>Camera that can push a frame on demand.</summary>
    private sealed class EmittingCamera : ICameraService
    {
        public event EventHandler<CameraFrameEventArgs>? FrameReceived;

        public event EventHandler<CameraErrorEventArgs>? CameraError
        {
            add { }
            remove { }
        }

        public bool IsRunning { get; private set; }

        public CameraDevice? SelectedCamera => null;

        public void Emit()
        {
            using var mat = new Mat(72, 128, MatType.CV_8UC3, Scalar.All(64));
            FrameReceived?.Invoke(this, new CameraFrameEventArgs { Frame = mat });
        }

        public Task<IReadOnlyList<CameraDevice>> GetAvailableCamerasAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<CameraDevice>>(Array.Empty<CameraDevice>());

        public Task StartAsync(CameraDevice? camera = null, CancellationToken ct = default)
        {
            IsRunning = true;
            return Task.CompletedTask;
        }

        public Task StartAsync(int cameraIndex, CancellationToken ct = default)
            => StartAsync(null, ct);

        public Task RestartAsync(CancellationToken ct = default)
        {
            IsRunning = true;
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken ct = default)
        {
            IsRunning = false;
            return Task.CompletedTask;
        }

        public Mat? CaptureFrame() => null;

        public void Dispose() => IsRunning = false;
    }

    private sealed class StubDetection : IFaceDetectionService
    {
        public bool IsReady => true;

        public FaceDetectionResult Detect(Mat frame)
            => new() { Faces = Array.Empty<DetectedFace>() };

        public FaceDetectionResult DetectPrimary(Mat frame) => Detect(frame);

        public void Dispose()
        {
        }
    }

    private sealed class StubRecognition : IFaceRecognitionService
    {
        public bool IsReady => true;

        public bool HasEnrolledProfile => false;

        public double Threshold => 0.60;

        public Task<FaceRecognitionResult> RecognizeAsync(Mat frame, CancellationToken ct = default)
            => Task.FromResult(FaceRecognitionResult.UnableToDetermine("Not used."));
    }

    private sealed class StubEvents : ISecurityEventService
    {
        public event EventHandler<Core.Entities.SecurityEvent>? EventRecorded
        {
            add { }
            remove { }
        }

        public Task<IReadOnlyList<Core.Entities.SecurityEvent>> GetRecentAsync(int count = 100, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<Core.Entities.SecurityEvent>>(Array.Empty<Core.Entities.SecurityEvent>());

        public Task<IReadOnlyList<Core.Entities.SecurityEvent>> QueryAsync(
            Core.Enums.SecurityEventType? type = null,
            DateTime? fromUtc = null,
            DateTime? toUtc = null,
            int limit = 500,
            CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<Core.Entities.SecurityEvent>>(Array.Empty<Core.Entities.SecurityEvent>());

        public Task<int> CountAsync(CancellationToken ct = default) => Task.FromResult(0);

        public Task ClearAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task<Core.Entities.SecurityEvent> RecordAsync(
            Core.Enums.SecurityEventType eventType,
            Core.Enums.SecurityEventResult result,
            string description,
            double? confidence = null,
            CancellationToken ct = default)
            => Task.FromResult(new Core.Entities.SecurityEvent());
    }

    private sealed class StubSettings : ISettingsService
    {
        public AppSettings Current { get; } = new();

        public RecognitionOptions Recognition { get; } = new();

        public event EventHandler? SettingsChanged
        {
            add { }
            remove { }
        }

        public Task LoadAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task SaveAsync(AppSettings settings, CancellationToken ct = default) => Task.CompletedTask;

        public Task SaveRecognitionAsync(RecognitionOptions options, CancellationToken ct = default) => Task.CompletedTask;
    }

    #endregion
}
