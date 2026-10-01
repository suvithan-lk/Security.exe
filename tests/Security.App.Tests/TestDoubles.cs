using System.IO;
using OpenCvSharp;
using Security.Core.Entities;
using Security.Core.Enums;
using Security.Core.Interfaces;
using Security.Core.Models;

namespace Security.App.Tests;

/// <summary>
/// In-memory stand-ins for everything the camera coordinator talks to.
///
/// Each fake is deliberately configurable so a specific failure (no device,
/// enumeration refused, device open refused) can be provoked on demand — that
/// is the whole point of these tests: the coordinator's state machine is
/// exercised without a webcam and without a model.
/// </summary>
internal sealed class FakeCameraService : ICameraService
{
    private IReadOnlyList<CameraDevice> _devices = Array.Empty<CameraDevice>();

    /// <summary>Devices returned by the next enumeration.</summary>
    public void SetDevices(params CameraDevice[] devices) => _devices = devices;

    /// <summary>Throws on the next <see cref="GetAvailableCamerasAsync"/> call.</summary>
    public bool EnumerationFails { get; set; }

    /// <summary>How many upcoming starts should fail before one succeeds.</summary>
    public int StartsToFail { get; set; }

    /// <summary>Every start fails with this error while it is set.</summary>
    public Exception? StartFailure { get; set; }

    public int StartCalls { get; private set; }

    public int StopCalls { get; private set; }

    public bool IsRunning { get; private set; }

    public CameraDevice? SelectedCamera { get; private set; }

    public event EventHandler<CameraFrameEventArgs>? FrameReceived
    {
        add { }
        remove { }
    }

    public event EventHandler<CameraErrorEventArgs>? CameraError
    {
        add { }
        remove { }
    }

    public Task<IReadOnlyList<CameraDevice>> GetAvailableCamerasAsync(CancellationToken cancellationToken = default)
    {
        if (EnumerationFails)
            throw new UnauthorizedAccessException("Windows refused to list capture devices.");

        return Task.FromResult(_devices);
    }

    public Task StartAsync(int cameraIndex, CancellationToken cancellationToken = default)
        => StartAsync(new CameraDevice { Index = cameraIndex, Name = $"Camera {cameraIndex}" }, cancellationToken);

    public Task StartAsync(CameraDevice? camera = null, CancellationToken cancellationToken = default)
    {
        StartCalls++;

        if (StartsToFail > 0)
        {
            StartsToFail--;
            throw new IOException("The camera is in use by another application.");
        }

        if (StartFailure is not null)
            throw StartFailure;

        IsRunning = true;
        SelectedCamera = camera ?? new CameraDevice { Index = 0, Name = "Default camera" };
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        StopCalls++;
        IsRunning = false;
        return Task.CompletedTask;
    }

    public Task RestartAsync(CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public Mat? CaptureFrame() => null;

    public void Dispose()
    {
        IsRunning = false;
    }
}

internal sealed class FakeFrameProcessor : IFrameProcessor
{
    public bool IsProcessing { get; private set; }

    public bool RecognitionSuppressed { get; set; }

    public Func<Mat, CancellationToken, Task>? FrameSink { get; set; }

    public int AttachCount { get; private set; }

    public int DetachCount { get; private set; }

    public event EventHandler<FrameAnalysisEventArgs>? FrameAnalyzed
    {
        add { }
        remove { }
    }

    public event EventHandler<FaceRecognitionResult>? RecognitionCompleted
    {
        add { }
        remove { }
    }

    public event EventHandler<FaceRecognitionResult>? UnknownFaceDetected
    {
        add { }
        remove { }
    }

    public void Attach(ICameraService camera)
    {
        AttachCount++;
        IsProcessing = true;
    }

    public void Detach()
    {
        DetachCount++;
        IsProcessing = false;
    }

    public void Dispose() => Detach();
}

internal sealed class FakeEnrollmentService : IEnrollmentService
{
    public bool ProfileExists { get; set; }

    public bool IsEnrolling { get; private set; }

    public event EventHandler<EnrollmentProgress>? ProgressChanged
    {
        add { }
        remove { }
    }

    public void Begin(int targetSamples) => IsEnrolling = true;

    public Task<EnrollmentResult> CompleteAsync(CancellationToken cancellationToken = default)
    {
        IsEnrolling = false;
        return Task.FromResult(new EnrollmentResult { Succeeded = true });
    }

    public Task<EnrollmentProgress> SubmitFrameAsync(Mat frame, CancellationToken cancellationToken = default)
        => Task.FromResult(new EnrollmentProgress());

    public void Cancel() => IsEnrolling = false;
}

internal sealed class FakeRecognitionService : IFaceRecognitionService
{
    public bool IsReady { get; set; } = true;

    public bool HasEnrolledProfile { get; set; }

    public double Threshold { get; set; } = 0.60;

    public Task<FaceRecognitionResult> RecognizeAsync(Mat frame, CancellationToken cancellationToken = default)
        => Task.FromResult(FaceRecognitionResult.UnableToDetermine("Recognition is faked in these tests."));
}

internal sealed class FakeSettingsService : ISettingsService
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

/// <summary>Captures every event instead of writing to the database.</summary>
internal sealed class RecordingEventService : ISecurityEventService
{
    private readonly List<SecurityEvent> _recorded = new();

    public IReadOnlyList<SecurityEvent> Recorded
    {
        get
        {
            lock (_recorded)
                return _recorded.ToList();
        }
    }

    public event EventHandler<SecurityEvent>? EventRecorded
    {
        add { }
        remove { }
    }

    public Task ClearAsync(CancellationToken cancellationToken = default)
    {
        lock (_recorded)
            _recorded.Clear();
        return Task.CompletedTask;
    }

    public Task<int> CountAsync(CancellationToken cancellationToken = default)
    {
        lock (_recorded)
            return Task.FromResult(_recorded.Count);
    }

    public Task<IReadOnlyList<SecurityEvent>> GetRecentAsync(int count = 100, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<SecurityEvent>>(Recorded.Take(count).ToList());

    public Task<IReadOnlyList<SecurityEvent>> QueryAsync(
        SecurityEventType? type = null,
        DateTime? fromUtc = null,
        DateTime? toUtc = null,
        int limit = 500,
        CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<SecurityEvent>>(Recorded.Take(limit).ToList());

    public Task<SecurityEvent> RecordAsync(
        SecurityEventType eventType,
        SecurityEventResult result,
        string description,
        double? confidence = null,
        CancellationToken cancellationToken = default)
    {
        var securityEvent = new SecurityEvent
        {
            EventType = eventType,
            Result = result,
            Description = description,
            Confidence = confidence,
            Timestamp = DateTime.UtcNow,
        };

        lock (_recorded)
            _recorded.Add(securityEvent);

        return Task.FromResult(securityEvent);
    }
}
