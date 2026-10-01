using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using OpenCvSharp;
using Security.App.Mvvm;
using Security.Core.Interfaces;
using Security.Core.Models;

namespace Security.App.Services;

/// <summary>
/// Shared owner of the camera, preview surface, analysis overlay, and
/// enrollment feed.
///
/// Every view (Dashboard, Camera, Face Profile) observes this single instance so
/// only one component ever opens the device. Views are read-only observers;
/// they invoke commands that delegate here.
///
/// Threading: frames arrive on the camera's own loop thread. Preview rendering
/// and property notifications are marshalled onto the UI thread. The camera
/// thread is never blocked.
/// </summary>
public sealed class CameraCoordinator : ObservableObject, IDisposable
{
    /// <summary>Preview is capped at this width to keep WPF pixel copies cheap.</summary>
    private const int MaxPreviewWidth = 960;

    /// <summary>Minimum gap between preview renders, in milliseconds (~15 fps).</summary>
    private const long PreviewIntervalMs = 66;

    private readonly ICameraService _camera;
    private readonly IFrameProcessor _processor;
    private readonly IEnrollmentService _enrollment;
    private readonly ISettingsService _settings;
    private readonly ISecurityEventService _events;
    private readonly IFaceRecognitionService _recognition;
    private readonly ILogger<CameraCoordinator>? _logger;

    private readonly Dispatcher _dispatcher;
    private readonly object _sync = new();

    private CameraDevice? _selectedCamera;
    private bool _isRunning;
    private string _cameraStatus = "Camera stopped";
    private string _statusText = "Waiting for camera…";
    private string _qualityMessage = string.Empty;
    private IReadOnlyList<OverlayBox> _boxes = Array.Empty<OverlayBox>();
    private FaceRecognitionResult? _lastRecognition;
    private string _engineStatus = "Checking…";
    private bool _engineReady;
    private bool _profileExists;

    private bool _enrollmentActive;
    private bool _completionRequested;
    private EnrollmentProgress _enrollmentProgress = new();
    private string _enrollmentMessage = string.Empty;
    private EnrollmentResult? _lastEnrollmentResult;

    private CameraState _state = CameraState.Offline;
    private IReadOnlyList<string> _failureReasons = Array.Empty<string>();
    private int _sourceWidth;
    private int _sourceHeight;
    private double _fps;
    private long _fpsWindowStartMs;
    private int _fpsWindowFrames;

    private WriteableBitmap? _preview;
    private int _previewPixelWidth;
    private int _previewPixelHeight;
    private int _frameWidth;
    private int _frameHeight;
    /// <summary>
    /// Tick of the last preview render, or null when none has been rendered.
    ///
    /// Null-checked instead of seeded with <c>long.MinValue</c>: the original
    /// code computed <c>now - _lastPreviewMs</c>, which under unchecked
    /// arithmetic overflowed to a negative value. The interval test then
    /// compared true on every frame, so <c>RenderPreview</c> was never reached
    /// and the camera preview stayed black forever.
    /// </summary>
    private long? _lastPreviewMs;
    private int _previewDispatchPending;
    private bool _disposed;

    public CameraCoordinator(
        ICameraService camera,
        IFrameProcessor processor,
        IEnrollmentService enrollment,
        ISettingsService settings,
        ISecurityEventService events,
        IFaceRecognitionService recognition,
        ILogger<CameraCoordinator>? logger = null)
    {
        _camera = camera;
        _processor = processor;
        _enrollment = enrollment;
        _settings = settings;
        _events = events;
        _recognition = recognition;
        _logger = logger;
        _dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;

        _camera.FrameReceived += OnFrameReceived;
        _camera.CameraError += OnCameraError;
        _processor.FrameAnalyzed += OnFrameAnalyzed;
        _processor.RecognitionCompleted += OnRecognitionCompleted;
        _processor.UnknownFaceDetected += OnUnknownFace;
        _enrollment.ProgressChanged += OnEnrollmentProgress;
    }

    #region Observable state

    public ObservableCollection<CameraDevice> Cameras { get; } = new();

    public CameraDevice? SelectedCamera
    {
        get => _selectedCamera;
        set => SetProperty(ref _selectedCamera, value);
    }

    public bool IsRunning
    {
        get => _isRunning;
        private set
        {
            if (SetProperty(ref _isRunning, value))
                RaiseCommandStatesChanged();
        }
    }

    /// <summary>
    /// Coarse capture state driving the status badge and the preview's
    /// empty/failure overlays. Distinct from <see cref="IsRunning"/>: a failed
    /// start leaves the device stopped AND the page in an error state that must
    /// offer a Retry, which a plain boolean cannot express.
    /// </summary>
    public CameraState State
    {
        get => _state;
        private set
        {
            if (SetProperty(ref _state, value))
            {
                OnPropertyChanged(nameof(IsLive));
                OnPropertyChanged(nameof(IsConnecting));
                OnPropertyChanged(nameof(IsOffline));
                OnPropertyChanged(nameof(IsError));
                OnPropertyChanged(nameof(StatusBadgeText));
            }
        }
    }

    public bool IsLive => State == CameraState.Live;

    public bool IsConnecting => State == CameraState.Connecting;

    public bool IsOffline => State == CameraState.Offline;

    public bool IsError => State == CameraState.Error;

    /// <summary>Short badge caption: LIVE / CONNECTING / OFFLINE / ERROR.</summary>
    public string StatusBadgeText => State switch
    {
        CameraState.Live => "LIVE",
        CameraState.Connecting => "CONNECTING",
        CameraState.Error => "ERROR",
        _ => "OFFLINE",
    };

    /// <summary>
    /// Specific, actionable causes behind a failure, shown verbatim in the
    /// CAMERA UNAVAILABLE panel. Empty whenever the camera is not in an error
    /// state, so an old failure can never linger after a successful start.
    /// </summary>
    public IReadOnlyList<string> FailureReasons
    {
        get => _failureReasons;
        private set
        {
            if (SetProperty(ref _failureReasons, value))
                OnPropertyChanged(nameof(HasFailureReasons));
        }
    }

    public bool HasFailureReasons => FailureReasons.Count > 0;

    /// <summary>Capture resolution in source pixels (0 while there is no frame).</summary>
    public int SourceWidth
    {
        get => _sourceWidth;
        private set
        {
            if (SetProperty(ref _sourceWidth, value))
                OnPropertyChanged(nameof(ResolutionText));
        }
    }

    public int SourceHeight
    {
        get => _sourceHeight;
        private set => SetProperty(ref _sourceHeight, value);
    }

    /// <summary>Resolution of the live preview, e.g. "1280 x 720".</summary>
    public string ResolutionText
        => SourceWidth > 0 && SourceHeight > 0 ? $"{SourceWidth} x {SourceHeight}" : "—";

    /// <summary>Measured delivery rate, smoothed over a one-second window.</summary>
    public double Fps
    {
        get => _fps;
        private set
        {
            if (SetProperty(ref _fps, value))
                OnPropertyChanged(nameof(FpsText));
        }
    }

    public string FpsText => Fps > 0 ? $"{Fps:0.0}" : "—";

    public string CameraStatus
    {
        get => _cameraStatus;
        private set => SetProperty(ref _cameraStatus, value);
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public string QualityMessage
    {
        get => _qualityMessage;
        private set => SetProperty(ref _qualityMessage, value);
    }

    /// <summary>Face rectangles, already expressed in preview pixel coordinates.</summary>
    public IReadOnlyList<OverlayBox> Boxes
    {
        get => _boxes;
        private set => SetProperty(ref _boxes, value);
    }

    public FaceRecognitionResult? LastRecognition
    {
        get => _lastRecognition;
        private set => SetProperty(ref _lastRecognition, value);
    }

    public string EngineStatus
    {
        get => _engineStatus;
        private set => SetProperty(ref _engineStatus, value);
    }

    public bool EngineReady
    {
        get => _engineReady;
        private set => SetProperty(ref _engineReady, value);
    }

    public bool ProfileExists
    {
        get => _profileExists;
        set
        {
            if (SetProperty(ref _profileExists, value))
                RaiseCommandStatesChanged();
        }
    }

    public bool IsEnrolling
    {
        get => _enrollmentActive;
        private set
        {
            if (SetProperty(ref _enrollmentActive, value))
                RaiseCommandStatesChanged();
        }
    }

    public EnrollmentProgress EnrollmentProgress
    {
        get => _enrollmentProgress;
        private set => SetProperty(ref _enrollmentProgress, value);
    }

    public string EnrollmentMessage
    {
        get => _enrollmentMessage;
        private set => SetProperty(ref _enrollmentMessage, value);
    }

    /// <summary>
    /// Outcome of the most recent enrollment run, or null when none has
    /// completed since the last Begin. The wizard reads this to decide between
    /// its Complete and Validation failed views, and to show the samples used
    /// and the model version that produced the stored template.
    /// </summary>
    public EnrollmentResult? LastEnrollmentResult
    {
        get => _lastEnrollmentResult;
        private set => SetProperty(ref _lastEnrollmentResult, value);
    }

    public WriteableBitmap? Preview
    {
        get => _preview;
        private set => SetProperty(ref _preview, value);
    }

    public int PreviewPixelWidth
    {
        get => _previewPixelWidth;
        private set => SetProperty(ref _previewPixelWidth, value);
    }

    public int PreviewPixelHeight
    {
        get => _previewPixelHeight;
        private set => SetProperty(ref _previewPixelHeight, value);
    }

    /// <summary>False while the model probe shows the engine cannot run.</summary>
    public bool IsEngineReady => EngineReady;

    public event EventHandler? CommandStatesChanged;

    public event EventHandler<SecurityAlertEventArgs>? SecurityAlert;

    private void RaiseCommandStatesChanged()
        => CommandStatesChanged?.Invoke(this, EventArgs.Empty);

    #endregion

    #region Camera lifecycle

    /// <summary>Populate the camera list and refresh engine/profile status.</summary>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await RefreshCamerasAsync(cancellationToken).ConfigureAwait(true);
        await RefreshEngineStatusAsync(cancellationToken).ConfigureAwait(true);
    }

    public async Task RefreshCamerasAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var cameras = await _camera.GetAvailableCamerasAsync(cancellationToken).ConfigureAwait(true);

            Cameras.Clear();
            foreach (var camera in cameras)
                Cameras.Add(camera);

            // Match by index — each enumeration yields fresh instances, so
            // reference equality would drop the user's selection on refresh.
            var previousIndex = SelectedCamera?.Index;
            SelectedCamera = previousIndex is int idx
                ? Cameras.FirstOrDefault(c => c.Index == idx)
                : null;

            if (SelectedCamera is null)
                SelectedCamera = Cameras.FirstOrDefault();

            if (Cameras.Count == 0)
            {
                State = CameraState.Offline;
                CameraStatus = "No camera detected";
                StatusText = "No camera detected. Connect a camera and refresh.";
                FailureReasons =
                [
                    "No capture device was found on this system.",
                    "Connect a camera, then choose Refresh.",
                ];
            }
            else if (!IsRunning && State != CameraState.Error)
            {
                State = CameraState.Offline;
                CameraStatus = "Camera ready";
                FailureReasons = Array.Empty<string>();
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Camera enumeration failed in coordinator");
            State = CameraState.Error;
            CameraStatus = "Could not list cameras";
            FailureReasons =
            [
                "Windows refused to list capture devices.",
                "Open Settings > Privacy & security > Camera and allow desktop apps.",
            ];
        }
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (IsRunning)
            return;

        // Tracks whether the device is already open, so a later failure never
        // leaves the camera held while the UI believes it is stopped.
        var deviceOpen = false;

        try
        {
            CameraStatus = "Starting camera…";
            State = CameraState.Connecting;
            FailureReasons = Array.Empty<string>();

            var target = SelectedCamera ?? Cameras.FirstOrDefault();
            await _camera.StartAsync(target, cancellationToken).ConfigureAwait(true);
            deviceOpen = true;

            _processor.Attach(_camera);

            IsRunning = true;
            State = CameraState.Live;
            CameraStatus = "Camera running";
            StatusText = "Camera started";

            await _events.RecordAsync(
                Core.Enums.SecurityEventType.CameraStarted,
                Core.Enums.SecurityEventResult.Info,
                $"Camera started: {target?.ToString() ?? "default device"}.").ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            await ReleaseDeviceAsync(deviceOpen).ConfigureAwait(true);
            throw;
        }
        catch (UnauthorizedAccessException ex)
        {
            await ReleaseDeviceAsync(deviceOpen).ConfigureAwait(true);
            CameraStatus = "Camera permission denied";
            StatusText = "Camera access was denied. Allow camera access in Windows Settings > Privacy.";
            FailureReasons = DescribePermissionFailure();
            _logger?.LogWarning(ex, "Camera permission denied");
        }
        catch (Exception ex)
        {
            await ReleaseDeviceAsync(deviceOpen).ConfigureAwait(true);
            CameraStatus = "Camera failed to start";
            StatusText = DescribeCameraFailure(ex);
            FailureReasons = DescribeFailureReasons(ex);
            _logger?.LogError(ex, "Camera start failed");
        }
    }

    /// <summary>
    /// Stop, pause long enough for the driver to release the device, then start
    /// the previously selected device again. This backs the camera page's
    /// Retry/Restart controls.
    /// </summary>
    public async Task RestartAsync(CancellationToken cancellationToken = default)
    {
        // Remember it first: StopAsync clears the selection.
        var previous = SelectedCamera ?? Cameras.FirstOrDefault();

        await StopAsync(cancellationToken).ConfigureAwait(true);

        SelectedCamera = previous;

        // Windows/Media Foundation needs a beat to hand the device back; an
        // immediate reopen intermittently fails with "camera in use", which is
        // exactly what Retry must not do.
        await Task.Delay(250, cancellationToken).ConfigureAwait(true);

        await StartAsync(cancellationToken).ConfigureAwait(true);
    }

    /// <summary>
    /// Best-effort teardown used when a partially-completed start fails, so the
    /// device and the pipeline never disagree about who owns what.
    /// </summary>
    private async Task ReleaseDeviceAsync(bool deviceOpen)
    {
        // The error transition happens BEFORE the early return: when the device
        // open itself is what failed, deviceOpen is still false and this would
        // otherwise return without ever leaving Connecting — leaving the badge
        // stuck on CONNECTING and the CAMERA UNAVAILABLE panel unreachable.
        IsRunning = false;
        State = CameraState.Error;
        CameraStatus = "Camera failed to start";

        if (!deviceOpen)
            return;

        try
        {
            _processor.Detach();

            if (_camera.IsRunning)
                await _camera.StopAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Could not release the camera after a failed start");
        }
    }

    /// <summary>
    /// Concrete, non-alarmist causes to show under CAMERA UNAVAILABLE. Ordered
    /// most → least likely so the operator checks the right thing first.
    /// </summary>
    private IReadOnlyList<string> DescribeFailureReasons(Exception ex)
    {
        var reasons = new List<string>
        {
            DescribeCameraFailure(ex),
        };

        if (IsLikelyPermission(ex))
            reasons.AddRange(DescribePermissionFailure());

        reasons.Add("Another application (a meeting or chat app) may be using the camera.");
        reasons.Add("The selected device may be unplugged — try Refresh, or a different camera.");
        reasons.Add("The camera driver may be missing or out of date.");

        return reasons;
    }

    private static IReadOnlyList<string> DescribePermissionFailure() =>
    [
        "Windows may be blocking this app from using the camera.",
        "Open Settings > Privacy & security > Camera and allow desktop apps.",
    ];

    private static bool IsLikelyPermission(Exception ex)
        => ex is UnauthorizedAccessException
           || ex.Message.Contains("permission", StringComparison.OrdinalIgnoreCase)
           || ex.Message.Contains("denied", StringComparison.OrdinalIgnoreCase)
           || ex.Message.Contains("access", StringComparison.OrdinalIgnoreCase);

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            _processor.Detach();

            if (_camera.IsRunning)
                await _camera.StopAsync(cancellationToken).ConfigureAwait(true);

            var wasRunning = IsRunning;

            IsRunning = false;
            State = CameraState.Offline;
            CameraStatus = "Camera stopped";
            StatusText = "Camera stopped";

            // The status block must not keep advertising a rate and resolution
            // from a session that has ended.
            Fps = 0;
            SourceWidth = 0;
            SourceHeight = 0;
            FailureReasons = Array.Empty<string>();

            ClearOverlay();
            LastRecognition = null;

            if (wasRunning)
            {
                await _events.RecordAsync(
                    Core.Enums.SecurityEventType.CameraStopped,
                    Core.Enums.SecurityEventResult.Info,
                    "Camera stopped.").ConfigureAwait(true);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Camera stop failed");
            IsRunning = false;
            State = CameraState.Offline;
            CameraStatus = "Camera stopped";
        }
    }

    /// <summary>Switch devices, restarting capture if it was running.</summary>
    public async Task SwitchAsync(CameraDevice camera, CancellationToken cancellationToken = default)
    {
        SelectedCamera = camera;

        if (IsRunning)
        {
            await StopAsync(cancellationToken).ConfigureAwait(true);
            await StartAsync(cancellationToken).ConfigureAwait(true);
        }
    }

    private void OnCameraError(object? sender, CameraErrorEventArgs e)
    {
        OnUi(() =>
        {
            IsRunning = false;
            State = CameraState.Error;
            CameraStatus = e.IsPermissionError ? "Camera permission denied" : "Camera error";
            StatusText = e.IsPermissionError
                ? "Camera access was denied. Allow camera access in Windows Settings > Privacy."
                : e.Message;

            var reasons = new List<string> { e.Message };
            if (e.IsPermissionError)
                reasons.AddRange(DescribePermissionFailure());
            else if (e.Exception is not null)
                reasons.AddRange(DescribeFailureReasons(e.Exception));
            FailureReasons = reasons;

            ClearOverlay();
            Fps = 0;
        });

        _logger?.LogWarning(e.Exception, "Camera error reported to UI: {Message}", e.Message);
    }

    private static string DescribeCameraFailure(Exception ex) => ex switch
    {
        System.IO.FileNotFoundException => "Camera driver or device is unavailable.",
        UnauthorizedAccessException => "Camera access was denied by Windows.",
        _ when ex.Message.Contains("in use", StringComparison.OrdinalIgnoreCase)
            => "The camera is being used by another application.",
        _ => "Could not start the camera. It may be in use by another application.",
    };

    #endregion

    #region Preview

    private void OnFrameReceived(object? sender, CameraFrameEventArgs e)
    {
        if (_disposed)
            return;

        // ---- Delivery rate ------------------------------------------------
        // Counted on every arriving frame, before the preview throttle below,
        // so the reported FPS is the camera's real output rate rather than the
        // (deliberately lower) UI render rate.
        var frameNow = Environment.TickCount64;
        var frames = Interlocked.Increment(ref _fpsWindowFrames);
        if (frameNow - _fpsWindowStartMs >= 1000)
        {
            var windowMs = frameNow - _fpsWindowStartMs;
            _fpsWindowStartMs = frameNow;
            Interlocked.Exchange(ref _fpsWindowFrames, 0);

            if (windowMs > 0)
            {
                var measured = frames * 1000.0 / windowMs;
                OnUi(() => Fps = measured);
            }
        }

        // Runs on the camera thread — keep it short and never block the device.
        if (_lastPreviewMs is long lastPreview && frameNow - lastPreview < PreviewIntervalMs)
            return;

        // Drop the frame if a render is still queued; the UI must never fall
        // behind the camera.
        if (Interlocked.CompareExchange(ref _previewDispatchPending, 1, 0) != 0)
            return;

        _lastPreviewMs = frameNow;

        try
        {
            var frame = e.Frame;
            if (frame.Empty())
            {
                Interlocked.Exchange(ref _previewDispatchPending, 0);
                return;
            }

            var sourceWidth = frame.Width;
            var sourceHeight = frame.Height;

            var scale = Math.Min(1.0, MaxPreviewWidth / (double)sourceWidth);
            var targetW = Math.Max(1, (int)Math.Round(sourceWidth * scale));
            var targetH = Math.Max(1, (int)Math.Round(sourceHeight * scale));

            using var preview = new Mat();
            Cv2.Resize(frame, preview, new OpenCvSharp.Size(targetW, targetH), 0, 0, InterpolationFlags.Area);
            using var bgra = new Mat();
            Cv2.CvtColor(preview, bgra, ColorConversionCodes.BGR2BGRA);

            var buffer = new byte[bgra.Width * bgra.Height * 4];
            Marshal.Copy(bgra.Data, buffer, 0, buffer.Length);

            _frameWidth = sourceWidth;
            _frameHeight = sourceHeight;

            _ = _dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(() =>
            {
                Interlocked.Exchange(ref _previewDispatchPending, 0);
                RenderPreview(buffer, targetW, targetH, sourceWidth, sourceHeight);
            }));
        }
        catch (Exception ex)
        {
            Interlocked.Exchange(ref _previewDispatchPending, 0);
            _logger?.LogDebug(ex, "Preview render failed");
        }
    }

    private void RenderPreview(byte[] buffer, int width, int height, int sourceWidth, int sourceHeight)
    {
        if (_disposed)
            return;

        if (_preview is null || _previewPixelWidth != width || _previewPixelHeight != height)
        {
            _preview = new WriteableBitmap(width, height, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null);
            PreviewPixelWidth = width;
            PreviewPixelHeight = height;
            OnPropertyChanged(nameof(Preview));
        }

        _preview.Lock();
        try
        {
            _preview.WritePixels(new Int32Rect(0, 0, width, height), buffer, width * 4, 0);
        }
        finally
        {
            _preview.Unlock();
        }

        _frameWidth = sourceWidth;
        _frameHeight = sourceHeight;

        // Published here (UI thread) rather than on the camera thread so the
        // status block's Resolution readout is a bound property, not a poll.
        SourceWidth = sourceWidth;
        SourceHeight = sourceHeight;
    }

    #endregion

    #region Analysis overlay

    private void OnFrameAnalyzed(object? sender, FrameAnalysisEventArgs e)
    {
        OnUi(() =>
        {
            StatusText = e.StatusText;
            if (!string.IsNullOrWhiteSpace(e.QualityMessage))
                QualityMessage = e.QualityMessage;

            Boxes = BuildBoxes(e.Faces);
            LastRecognition = e.Recognition ?? LastRecognition;
        });
    }

    private void OnRecognitionCompleted(object? sender, FaceRecognitionResult result)
    {
        OnUi(() => LastRecognition = result);
    }

    private void OnUnknownFace(object? sender, FaceRecognitionResult result)
    {
        OnUi(() =>
        {
            try
            {
                SecurityAlert?.Invoke(this, new SecurityAlertEventArgs(result));
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "A SecurityAlert subscriber threw");
            }
        });
    }

    /// <summary>Map frame-space detections into preview pixel space for the overlay.</summary>
    private IReadOnlyList<OverlayBox> BuildBoxes(IReadOnlyList<DetectedFace> faces)
    {
        if (faces.Count == 0)
            return Array.Empty<OverlayBox>();

        var frameW = _frameWidth;
        var frameH = _frameHeight;

        // Before the first frame lands we have no mapping yet; render 1:1 so the
        // boxes are still roughly right rather than invisible.
        if (frameW <= 0 || frameH <= 0)
        {
            frameW = (int)(faces.Max(f => f.X + f.Width) + 1);
            frameH = (int)(faces.Max(f => f.Y + f.Height) + 1);
        }

        var scaleX = _previewPixelWidth > 0 ? _previewPixelWidth / (double)frameW : 1.0;
        var scaleY = _previewPixelHeight > 0 ? _previewPixelHeight / (double)frameH : 1.0;

        var boxes = new List<OverlayBox>(faces.Count);
        for (var i = 0; i < faces.Count; i++)
        {
            var f = faces[i];
            boxes.Add(new OverlayBox
            {
                X = f.X * scaleX,
                Y = f.Y * scaleY,
                Width = f.Width * scaleX,
                Height = f.Height * scaleY,
                Score = f.Score,
                IsPrimary = i == 0,
            });
        }

        return boxes;
    }

    private void ClearOverlay()
    {
        Boxes = Array.Empty<OverlayBox>();
        QualityMessage = string.Empty;
    }

    #endregion

    #region Engine status

    public async Task RefreshEngineStatusAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            // IsReady forces the ONNX session to load on first access, and
            // HasEnrolledProfile can block on a bounded database read — neither
            // belongs on the dispatcher. Probe off-thread, then publish the
            // results back on the caller's context.
            var (ready, profileExists) = await Task.Run(() =>
            {
                var isReady = _recognition.IsReady;
                var hasProfile = _recognition.HasEnrolledProfile;
                return (isReady, hasProfile);
            }, cancellationToken).ConfigureAwait(true);

            EngineReady = ready;
            EngineStatus = ready
                ? "Recognition engine ready"
                : "Recognition engine not ready — models missing or failed to load.";

            ProfileExists = profileExists;

            if (!ready)
                StatusText = "Recognition engine is not ready. Models could not be loaded.";
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            EngineReady = false;
            EngineStatus = "Recognition engine not ready";
            _logger?.LogWarning(ex, "Engine status probe failed");
        }
    }

    #endregion

    #region Enrollment

    public void BeginEnrollment(int targetSamples)
    {
        if (IsEnrolling)
            return;

        _completionRequested = false;
        EnrollmentMessage = string.Empty;
        LastEnrollmentResult = null;
        LastRecognition = null;
        EnrollmentProgress = new EnrollmentProgress { Captured = 0, Target = targetSamples };

        _processor.RecognitionSuppressed = true;
        _processor.FrameSink = EnrollmentSinkAsync;
        _enrollment.Begin(targetSamples);
        IsEnrolling = true;

        // Deliberately NOT recording EnrollmentStarted here: EnrollmentService
        // already owns the lifecycle (Started / Completed / Failed / Cancelled)
        // and recording it in both places logged the event twice.
    }

    public void CancelEnrollment()
    {
        if (!IsEnrolling)
            return;

        _enrollment.Cancel();
        IsEnrolling = false;
        _processor.FrameSink = null;
        _processor.RecognitionSuppressed = false;
        LastEnrollmentResult = null;
        EnrollmentMessage = "Enrollment cancelled.";
        EnrollmentProgress = new EnrollmentProgress();
    }

    private async Task EnrollmentSinkAsync(Mat frame, CancellationToken cancellationToken)
    {
        if (!IsEnrolling || _disposed)
            return;

        var progress = await _enrollment.SubmitFrameAsync(frame, cancellationToken).ConfigureAwait(false);

        if (progress.IsComplete && !_completionRequested)
        {
            _completionRequested = true;
            await CompleteEnrollmentAsync().ConfigureAwait(false);
            return;
        }

        OnUi(() =>
        {
            EnrollmentProgress = progress;
            if (!string.IsNullOrWhiteSpace(progress.LastIssue))
                EnrollmentMessage = progress.LastIssue;
            else if (!string.IsNullOrWhiteSpace(progress.Instruction))
                EnrollmentMessage = progress.Instruction;
        });
    }

    private async Task<EnrollmentResult> CompleteEnrollmentAsync()
    {
        try
        {
            var result = await _enrollment.CompleteAsync().ConfigureAwait(true);

            OnUi(() =>
            {
                // Set first: the wizard keys its Complete/Failed transition off
                // IsEnrolling flipping to false, so the result must already be
                // observable when that notification lands.
                LastEnrollmentResult = result;
                IsEnrolling = false;
                EnrollmentMessage = result.Message;
                EnrollmentProgress = result.Succeeded
                    ? new EnrollmentProgress { Captured = result.SampleCount, Target = result.SampleCount }
                    : EnrollmentProgress;
            });

            _processor.FrameSink = null;
            _processor.RecognitionSuppressed = false;

            if (result.Succeeded)
                await RefreshEngineStatusAsync().ConfigureAwait(true);

            return result;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Enrollment completion failed");

            var failed = EnrollmentResult.Fail("The face profile could not be saved. Check that the database is available and that nothing is locking the data folder.");

            OnUi(() =>
            {
                LastEnrollmentResult = failed;
                IsEnrolling = false;
                EnrollmentMessage = failed.Message;
            });

            _processor.FrameSink = null;
            _processor.RecognitionSuppressed = false;

            return failed;
        }
    }

    private void OnEnrollmentProgress(object? sender, EnrollmentProgress progress)
    {
        // Enrolled via the frame sink, which already reports progress; this
        // covers events raised outside the frame path (e.g. Begin/Cancel).
        if (!IsEnrolling)
            return;

        OnUi(() => EnrollmentProgress = progress);
    }

    #endregion

    #region Helpers

    private void OnUi(Action action)
    {
        if (_disposed)
            return;

        if (_dispatcher.CheckAccess())
        {
            SafeRun(action);
            return;
        }

        _ = _dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() => SafeRun(action)));
    }

    private static void SafeRun(Action action)
    {
        try
        {
            action();
        }
        catch (Exception)
        {
            // UI marshalling races during shutdown are not actionable.
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        _camera.FrameReceived -= OnFrameReceived;
        _camera.CameraError -= OnCameraError;
        _processor.FrameAnalyzed -= OnFrameAnalyzed;
        _processor.RecognitionCompleted -= OnRecognitionCompleted;
        _processor.UnknownFaceDetected -= OnUnknownFace;
        _enrollment.ProgressChanged -= OnEnrollmentProgress;

        try
        {
            _processor.Dispose();
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Frame processor dispose failed");
        }

        _preview = null;
        GC.SuppressFinalize(this);
    }

    #endregion
}

/// <summary>Face rectangle expressed in preview pixel coordinates.</summary>
public sealed class OverlayBox
{
    public double X { get; init; }

    public double Y { get; init; }

    public double Width { get; init; }

    public double Height { get; init; }

    public double Score { get; init; }

    public bool IsPrimary { get; init; }
}

/// <summary>Raised in-app when an unknown face is detected.</summary>
public sealed class SecurityAlertEventArgs : EventArgs
{
    public SecurityAlertEventArgs(FaceRecognitionResult result) => Result = result;

    public FaceRecognitionResult Result { get; }

    public DateTime Timestamp => Result.Timestamp;
}
