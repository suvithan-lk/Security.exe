using System.Diagnostics;
using Microsoft.Extensions.Logging;
using OpenCvSharp;
using Security.Core.Enums;
using Security.Core.Interfaces;
using Security.Core.Models;
using Security.Core.Services;
using Security.Face.Detection;

namespace Security.Face.Pipeline;

/// <summary>
/// Frame analysis pipeline.
///
/// Threading: the camera raises frames on its own loop thread. This class never
/// blocks that thread — it stores the newest frame in a single-slot buffer
/// (dropping older ones) and processes on a separate worker task.
///
/// Rate control:
///   detection    ~DetectionFps (default 8)
///   recognition  only when a single face has been stable for StableFaceFrames
///                consecutive detections AND the cooldown has elapsed
/// </summary>
public sealed class FrameProcessor : IFrameProcessor
{
    private readonly IFaceDetectionService _detection;
    private readonly IFaceRecognitionService _recognition;
    private readonly ISecurityEventService _events;
    private readonly ISettingsService _settings;
    private readonly ILogger<FrameProcessor>? _logger;

    /// <summary>
    /// Optional. When present, on-screen guidance is produced by the SAME
    /// service that gates enrollment, so the overlay can never claim a frame is
    /// fine while enrollment would reject it. Null keeps the pipeline usable in
    /// tests that do not care about quality.
    /// </summary>
    private readonly IFaceQualityService? _quality;

    private readonly object _slotLock = new();
    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly ISnapshotStore? _snapshots;

    private ICameraService? _camera;
    private Mat? _pending;
    private CancellationTokenSource? _cts;
    private Task? _worker;

    private int _stableFrames;

    /// <summary>
    /// Tick of the last verdict, or null if none has been produced yet.
    ///
    /// A nullable sentinel is used deliberately: the previous implementation
    /// initialised this to <c>long.MinValue</c> and computed
    /// <c>now - _lastRecognitionMs</c>. Because C# integer arithmetic is
    /// unchecked, that subtraction overflowed to a NEGATIVE number, so the
    /// "cooldown elapsed" comparison was permanently false and recognition
    /// never ran. Null checks avoid the arithmetic entirely.
    /// </summary>
    private long? _lastRecognitionMs;

    /// <summary>
    /// Tick of the last recorded unknown-face event, or null if none yet.
    /// Same null-sentinel pattern as <see cref="_lastRecognitionMs"/>: gating
    /// the first unknown on "never seen one" instead of a subtraction that
    /// could overflow.
    /// </summary>
    private long? _lastUnknownMs;

    private bool _disposed;

    public FrameProcessor(
        IFaceDetectionService detection,
        IFaceRecognitionService recognition,
        ISecurityEventService events,
        ISettingsService settings,
        ILogger<FrameProcessor>? logger = null,
        IFaceQualityService? quality = null,
        ISnapshotStore? snapshots = null)
    {
        _detection = detection;
        _recognition = recognition;
        _events = events;
        _settings = settings;
        _logger = logger;
        _quality = quality;
        _snapshots = snapshots;
    }

    public event EventHandler<FrameAnalysisEventArgs>? FrameAnalyzed;

    public event EventHandler<FaceRecognitionResult>? RecognitionCompleted;

    public event EventHandler<FaceRecognitionResult>? UnknownFaceDetected;

    public bool IsProcessing => _worker is { IsCompleted: false };

    public bool RecognitionSuppressed { get; set; }

    public bool UnknownFaceDetectionEnabled { get; set; } = true;

    public Func<Mat, CancellationToken, Task>? FrameSink { get; set; }

    public void Attach(ICameraService camera)
    {
        ArgumentNullException.ThrowIfNull(camera);
        ObjectDisposedException.ThrowIf(_disposed, this);

        Detach();

        _camera = camera;
        _stableFrames = 0;
        _lastRecognitionMs = null;
        _lastUnknownMs = null;

        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        camera.FrameReceived += OnFrameReceived;
        _worker = Task.Run(() => WorkerLoop(token), token);
    }

    public void Detach()
    {
        var camera = _camera;
        if (camera is not null)
            camera.FrameReceived -= OnFrameReceived;

        _camera = null;

        var cts = _cts;
        _cts = null;
        cts?.Cancel();

        var worker = _worker;
        _worker = null;

        try
        {
            worker?.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
            // Shutdown races are not actionable here.
        }
        catch (OperationCanceledException)
        {
        }

        cts?.Dispose();

        // Deliberately no _wake.Release() here. _wake has a maximum count of 1,
        // so a second Detach() (stop → start → stop) — or a Detach() racing a
        // frame that already signalled — would throw SemaphoreFullException and
        // take the whole camera start down with it. The worker loop waits with a
        // 50 ms timeout on a token that is cancelled above, so it wakes and exits
        // promptly without needing to be signalled.
        lock (_slotLock)
        {
            _pending?.Dispose();
            _pending = null;
        }

        _stableFrames = 0;
    }

    private void OnFrameReceived(object? sender, CameraFrameEventArgs e)
    {
        // Runs on the camera thread — must be non-blocking. Take ownership of a
        // clone (the sender disposes its own Mat when the handler returns).
        var clone = e.Frame.Clone();

        lock (_slotLock)
        {
            _pending?.Dispose();
            _pending = clone;
        }

        try
        {
            _wake.Release();
        }
        catch (SemaphoreFullException)
        {
            // Already signalled; nothing to do.
        }
    }

    private async Task WorkerLoop(CancellationToken token)
    {
        long? lastDetectionMs = null;

        while (!token.IsCancellationRequested)
        {
            try
            {
                await _wake.WaitAsync(50, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            if (token.IsCancellationRequested)
                break;

            Mat? frame;
            lock (_slotLock)
            {
                frame = _pending;
                _pending = null;
            }

            if (frame is null)
                continue;

            try
            {
                var now = _clock.ElapsedMilliseconds;

                // Frame throttle: honour the configured detection rate.
                // `lastDetectionMs is null` means "first frame ever" — it must
                // fall through, never be compared against a sentinel value.
                var minIntervalMs = 1000.0 / Math.Max(1, _settings.Recognition.DetectionFps);
                if (lastDetectionMs is long previousMs && now - previousMs < minIntervalMs)
                    continue;

                lastDetectionMs = now;

                using (frame)
                {
                    await AnalyzeAsync(frame, now, token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // A bad frame must never kill the pipeline.
                _logger?.LogWarning(ex, "Frame analysis failed");
            }
            finally
            {
                frame?.Dispose();
            }
        }
    }

    private async Task AnalyzeAsync(Mat frame, long nowMs, CancellationToken token)
    {
        var detection = _detection.Detect(frame);
        var faces = detection.Faces;

        FaceRecognitionResult? result = null;

        // Live guidance from the same service that gates enrollment. It runs
        // first because it also decides how many of these detections are really
        // people: YuNet's occasional ~15px false positive must not tell the
        // operator there is a crowd, and must not suppress recognition while a
        // perfectly good face is in frame.
        FaceQualityResult? quality = null;
        if (_quality is not null && detection.FaceCount > 0)
            quality = _quality.Evaluate(frame, detection);

        var faceCount = quality?.FaceCount ?? detection.FaceCount;
        var single = faceCount == 1;

        string qualityMessage = quality switch
        {
            // The frame would be refused as a sample: say exactly why on screen.
            { IsAcceptable: false } => quality.Reason,
            // No quality service to consult: fall back to the detector's text.
            null => detection.StatusText,
            _ => StatusTextFor(faceCount),
        };

        if (!single)
        {
            _stableFrames = 0;

            // Repeated multi-face frames are worth an event (but rate-limited by
            // the recognition cooldown so we do not spam the log).
            // Rate-limited by the shared cooldown so a crowd does not spam the
            // event log. The first multi-face frame is always worth recording.
            if (faceCount > 1 && CooldownElapsed(nowMs))
            {
                _lastRecognitionMs = nowMs;
                await _events.RecordAsync(SecurityEventType.FaceDetected, SecurityEventResult.Info,
                    "Multiple faces detected; recognition suppressed.", null, token).ConfigureAwait(false);
            }
        }
        else
        {
            _stableFrames++;

            var options = _settings.Recognition;
            var stableEnough = _stableFrames >= Math.Max(1, options.StableFaceFrames);
            var cooldownElapsed = CooldownElapsed(nowMs);
            var face = detection.PrimaryFace!;
            var largeEnough = ProfileValidator.IsFaceLargeEnough(face.Width, frame.Width, options);

            if (stableEnough && cooldownElapsed && largeEnough &&
                _settings.Current.RecognitionEnabled && !RecognitionSuppressed)
            {
                _lastRecognitionMs = nowMs;

                if (_recognition.IsReady && _recognition.HasEnrolledProfile)
                {
                    result = await _recognition.RecognizeAsync(frame, token).ConfigureAwait(false);
                    qualityMessage = Describe(result);

                    await PublishAsync(frame, result, nowMs, token).ConfigureAwait(false);
                }
                else
                {
                    qualityMessage = _recognition.IsReady
                        ? "No face profile enrolled."
                        : "Recognition engine is not ready.";
                }
            }
        }

        var args = new FrameAnalysisEventArgs
        {
            Timestamp = DateTime.UtcNow,
            FrameWidth = frame.Width,
            FrameHeight = frame.Height,
            Faces = faces,
            StatusText = detection.StatusText,
            Recognition = result,
            QualityMessage = qualityMessage,
        };

        try
        {
            FrameAnalyzed?.Invoke(this, args);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "A FrameAnalyzed subscriber threw");
        }

        // Give the sink (e.g. enrollment) its turn with the same throttled frame.
        var sink = FrameSink;
        if (sink is not null)
        {
            try
            {
                await sink(frame, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Frame sink failed");
            }
        }
    }

    private async Task PublishAsync(Mat frame, FaceRecognitionResult result, long nowMs, CancellationToken token)
    {
        try
        {
            RecognitionCompleted?.Invoke(this, result);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "A RecognitionCompleted subscriber threw");
        }

        switch (result.Status)
        {
            case RecognitionStatus.Known:
                await _events.RecordAsync(SecurityEventType.KnownFaceDetected, SecurityEventResult.Known,
                    "Face matched the enrolled profile.", result.Similarity, token).ConfigureAwait(false);
                break;

            case RecognitionStatus.Unknown:
                // Unknown detection is gated two ways: the operator/monitor
                // switch (no event, no snapshot, no alert while off) and a
                // dedicated cooldown so a face lingering in frame cannot write
                // a row per recognition tick.
                if (!UnknownFaceDetectionEnabled || !UnknownCooldownElapsed(nowMs))
                    break;

                _lastUnknownMs = nowMs;

                // Snapshot first: the event row then stores the path in a
                // single write. A failed snapshot never blocks the event.
                string? snapshotPath = null;
                if (_settings.Current.StoreSnapshots)
                    snapshotPath = _snapshots?.Save(frame, token);

                await _events.RecordAsync(SecurityEventType.UnknownFaceDetected, SecurityEventResult.Unknown,
                    "Face did not match the enrolled profile.", result.Similarity, token,
                    snapshotPath: snapshotPath).ConfigureAwait(false);

                try
                {
                    UnknownFaceDetected?.Invoke(this, result);
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "An UnknownFaceDetected subscriber threw");
                }

                break;

            default:
                await _events.RecordAsync(SecurityEventType.RecognitionFailed, SecurityEventResult.Failure,
                    string.IsNullOrWhiteSpace(result.Reason) ? "No verdict." : result.Reason, null, token)
                    .ConfigureAwait(false);
                break;
        }
    }

    private long CooldownMs()
        => Math.Max(0, _settings.Recognition.RecognitionCooldownSeconds) * 1000L;

    private long UnknownCooldownMs()
        => Math.Max(0, _settings.Recognition.UnknownFaceCooldownSeconds) * 1000L;

    /// <summary>
    /// True when no unknown event has been recorded yet, or when the
    /// dedicated unknown-face cooldown has elapsed since the last one.
    /// Null-check instead of subtraction — same overflow reasoning as
    /// <see cref="CooldownElapsed"/>.
    /// </summary>
    private bool UnknownCooldownElapsed(long nowMs)
        => _lastUnknownMs is not long last || nowMs - last >= UnknownCooldownMs();

    /// <summary>
    /// True when no verdict has been produced yet, or when the configured
    /// cooldown has elapsed since the last one.
    ///
    /// Written as an explicit null check rather than a sentinel subtraction so
    /// it can never overflow.
    /// </summary>
    private bool CooldownElapsed(long nowMs)
        => _lastRecognitionMs is not long last || nowMs - last >= CooldownMs();

    private static string Describe(FaceRecognitionResult result) => result.Status switch
    {
        RecognitionStatus.Known => $"KNOWN (similarity {result.Similarity:F2})",
        RecognitionStatus.Unknown => $"UNKNOWN (similarity {result.Similarity:F2})",
        _ => result.Reason,
    };

    /// <summary>
    /// Overlay text for a face count that has already had sub-threshold
    /// detections removed, so what the overlay says matches what the quality
    /// gate and the recognition suppression both decided.
    /// </summary>
    private static string StatusTextFor(int faceCount) => faceCount switch
    {
        0 => "No face detected",
        1 => "Face detected",
        _ => "Multiple faces detected. Please ensure only one person is visible.",
    };

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        Detach();
        _wake.Dispose();
        GC.SuppressFinalize(this);
    }
}
