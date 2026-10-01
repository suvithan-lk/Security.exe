using System.Diagnostics;
using Microsoft.Extensions.Logging;
using OpenCvSharp;
using Security.Core.Interfaces;
using Security.Core.Models;
using Security.Face.Models;

namespace Security.Face.Camera;

/// <summary>
/// Camera capture backed by OpenCV's VideoCapture.
///
/// Threading model:
///   - Start/Stop open and close the device on the calling (UI) thread but only
///     for the brief open/close calls; the capture loop runs on a dedicated
///     background task.
///   - Frames are raised via <see cref="FrameReceived"/> on that background task.
///     Subscribers must marshal to the UI thread themselves (and must copy or
///     synchronously consume the Mat, because it is disposed right after the
///     event returns).
///
/// Disposal guarantees: the device is released on Stop, on camera switch, on
/// dispose, on exception, and on process exit — never left locked.
/// </summary>
public sealed class CameraService : ICameraService
{
    private readonly CameraOptions _options;
    private readonly ILogger<CameraService>? _logger;
    private readonly object _gate = new();

    /// <summary>Upper bound on camera indices probed during enumeration.</summary>
    private const int MaxProbedCameras = 8;

    private VideoCapture? _capture;

    /// <summary>
    /// Most recent frame, owned by this class and replaced on every capture.
    ///
    /// <see cref="CaptureFrame"/> clones this instead of performing a second
    /// <c>VideoCapture.Read</c>. OpenCV's VideoCapture is NOT thread-safe, and
    /// reading from the UI thread while the capture loop is mid-<c>Read</c>
    /// corrupted the MSMF source reader in practice (repeated
    /// "can't grab frame" warnings followed by the device being declared
    /// disconnected). A cached frame removes the concurrent read entirely.
    /// </summary>
    private Mat? _latestFrame;

    private CancellationTokenSource? _loopCts;
    private Task? _loopTask;
    private CameraDevice? _selected;
    private bool _disposed;

    public CameraService(CameraOptions? options = null, ILogger<CameraService>? logger = null)
    {
        _options = options ?? new CameraOptions();
        _logger = logger;
    }

    public event EventHandler<CameraFrameEventArgs>? FrameReceived;

    public event EventHandler<CameraErrorEventArgs>? CameraError;

    public bool IsRunning
    {
        get
        {
            lock (_gate)
            {
                return _capture is not null && _capture.IsOpened() && _loopTask is { IsCompleted: false };
            }
        }
    }

    public CameraDevice? SelectedCamera
    {
        get
        {
            lock (_gate)
            {
                return _selected;
            }
        }
    }

    public Task<IReadOnlyList<CameraDevice>> GetAvailableCamerasAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Enumeration touches device APIs (WinRT + OpenCV), so run it off the
        // calling thread to keep the UI responsive.
        return Task.Run<IReadOnlyList<CameraDevice>>(async () =>
        {
            var result = new List<CameraDevice>();

            // --- Fast path: ask Windows, never open the device -----------------
            // Probe-by-opening cost 6.2 s on the reference machine (MSMF spends
            // seconds opening and grabbing a throwaway frame per index), which
            // made the camera look dead at startup. Windows already knows the
            // devices and their friendly names, and enumerates them in the same
            // order OpenCV uses on Windows.
            var names = await GetWindowsCameraNamesAsync(cancellationToken).ConfigureAwait(false);
            if (names.Count > 0)
            {
                for (var i = 0; i < names.Count && i < MaxProbedCameras; i++)
                    result.Add(new CameraDevice { Index = i, Name = names[i] });

                return (IReadOnlyList<CameraDevice>)result;
            }

            try
            {
                // --- Fallback: WinRT was unavailable (non-Windows, or the
                // privacy setting hides devices from it). Probe OpenCV indices.
                //
                // Never probe while capturing: opening index 0 would contend with
                // the live reader on the same non-thread-safe VideoCapture and
                // corrupt it.
                if (IsRunning)
                    return (IReadOnlyList<CameraDevice>)Array.Empty<CameraDevice>();

                for (var i = 0; i < MaxProbedCameras; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (!CanOpen(i))
                        break;

                    result.Add(new CameraDevice { Index = i, Name = $"Camera {i + 1}" });
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Camera enumeration failed");
                RaiseError("Could not list cameras.", ex, isPermission: false);
            }

            return (IReadOnlyList<CameraDevice>)result;
        }, cancellationToken);
    }

    /// <summary>
    /// Friendly device names from Windows, used to label the OpenCV indices.
    /// Returns an empty list if WinRT enumeration is unavailable.
    /// </summary>
    private async Task<IReadOnlyList<string>> GetWindowsCameraNamesAsync(CancellationToken cancellationToken)
    {
        try
        {
            var devices = await Windows.Devices.Enumeration.DeviceInformation
                .FindAllAsync(Windows.Devices.Enumeration.DeviceClass.VideoCapture)
                .AsTask(cancellationToken);

            return devices.Select(d => d.Name).Where(n => !string.IsNullOrWhiteSpace(n)).ToList();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Windows camera name enumeration unavailable");
            return Array.Empty<string>();
        }
    }

    /// <summary>
    /// Try to open a camera index briefly. Returns true when OpenCV can use it.
    /// </summary>
    private bool CanOpen(int index)
    {
        try
        {
            using var probe = new VideoCapture(index);
            var ok = probe.IsOpened();

            // Read one frame to confirm the device actually produces data, since
            // some drivers report "opened" for absent devices.
            if (ok)
            {
                using var frame = new Mat();
                ok = probe.Read(frame) && !frame.Empty();
            }

            return ok;
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Probe of camera index {Index} failed", index);
            return false;
        }
    }

    public async Task StartAsync(CameraDevice? camera = null, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // Stop any existing session first — switching cameras must release the device.
        await StopAsync(cancellationToken).ConfigureAwait(false);

        var target = camera ?? await PickDefaultCameraAsync(cancellationToken).ConfigureAwait(false);

        await Task.Run(() => OpenDevice(target), cancellationToken).ConfigureAwait(false);

        StartLoop();
    }

    public async Task StartAsync(int cameraIndex, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (cameraIndex < 0)
        {
            await StartAsync((CameraDevice?)null, cancellationToken).ConfigureAwait(false);
            return;
        }

        // Resolve the friendly name so logs and the UI show a real device, and
        // so an index that no longer exists falls back to the default device
        // instead of stranding the camera in a permanently failed state.
        var cameras = await GetAvailableCamerasAsync(cancellationToken).ConfigureAwait(false);
        var match = cameras.FirstOrDefault(c => c.Index == cameraIndex);

        await StartAsync(match, cancellationToken).ConfigureAwait(false);
    }

    public async Task RestartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // Remember the device first: StopAsync clears SelectedCamera.
        var last = SelectedCamera;

        await StopAsync(cancellationToken).ConfigureAwait(false);

        // Windows/Media Foundation needs a beat to hand the device back. Without
        // this pause an immediate reopen intermittently fails with
        // "camera in use", which is exactly what Retry must not do.
        await Task.Delay(250, cancellationToken).ConfigureAwait(false);

        await StartAsync(last, cancellationToken).ConfigureAwait(false);
    }

    private async Task<CameraDevice?> PickDefaultCameraAsync(CancellationToken cancellationToken)
    {
        var cameras = await GetAvailableCamerasAsync(cancellationToken).ConfigureAwait(false);
        return cameras.Count > 0 ? cameras[0] : null;
    }

    private void OpenDevice(CameraDevice? target)
    {
        lock (_gate)
        {
            if (_disposed)
                return;

            try
            {
                var capture = target is null
                    ? new VideoCapture(0)
                    : new VideoCapture(target.Index);

                if (!capture.IsOpened())
                {
                    capture.Dispose();
                    var message = target is null
                        ? "No camera available."
                        : $"Camera unavailable: {target.Name}";

                    _logger?.LogWarning("Failed to open {Target}", target?.ToString() ?? "<default>");
                    RaiseError(message, null, IsLikelyPermissionError(message));

                    // Throwing matters: returning normally made StartAsync report
                    // success with no device open, so the UI flipped to "Camera
                    // running" and logged CameraStarted while nothing was captured.
                    throw new InvalidOperationException(message);
                }

                // Best-effort request; drivers ignore unsupported values.
                capture.Set(VideoCaptureProperties.FrameWidth, _options.Width);
                capture.Set(VideoCaptureProperties.FrameHeight, _options.Height);

                _capture = capture;
                _selected = target;

                _logger?.LogInformation("Camera started: {Name}", target?.ToString() ?? "<default>");
            }
            catch (InvalidOperationException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Exception while opening camera {Target}", target?.ToString() ?? "<default>");
                RaiseError("Camera unavailable.", ex, IsLikelyPermissionError(ex.Message));
                throw new InvalidOperationException("Camera unavailable.", ex);
            }
        }
    }

    private void StartLoop()
    {
        lock (_gate)
        {
            if (_capture is null || _disposed)
                return;

            _loopCts = new CancellationTokenSource();
            var token = _loopCts.Token;
            var capture = _capture;

            _loopTask = Task.Run(() => CaptureLoop(capture, token), token);
        }
    }

    private void CaptureLoop(VideoCapture capture, CancellationToken token)
    {
        using var frame = new Mat();
        var consecutiveFailures = 0;

        while (!token.IsCancellationRequested)
        {
            try
            {
                if (!capture.IsOpened())
                {
                    RaiseError("Camera disconnected.", null, false);
                    break;
                }

                if (!capture.Read(frame) || frame.Empty())
                {
                    consecutiveFailures++;

                    // A few transient empty reads are normal while the driver
                    // settles; a sustained run means the device went away.
                    if (consecutiveFailures >= 30)
                    {
                        _logger?.LogWarning("Camera stopped returning frames ({Count} consecutive failures)", consecutiveFailures);
                        RaiseError("Camera disconnected or stopped responding.", null, false);
                        break;
                    }

                    Thread.Sleep(15);
                    continue;
                }

                consecutiveFailures = 0;

                // Clone once: the loop reuses `frame` for the next iteration, and
                // the event handler must not receive a Mat that mutates under it.
                // The same clone becomes the cached "latest frame", so
                // CaptureFrame() never has to touch the device again.
                var copy = frame.Clone();
                lock (_gate)
                {
                    _latestFrame?.Dispose();
                    _latestFrame = copy;
                }

                FrameReceived?.Invoke(this, new CameraFrameEventArgs { Frame = copy, Timestamp = DateTime.UtcNow });
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Camera capture loop error");
                RaiseError("Camera error.", ex, false);
                break;
            }
        }
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        Task? loop;

        lock (_gate)
        {
            _loopCts?.Cancel();
            loop = _loopTask;
            _loopCts = null;
            _loopTask = null;
        }

        // Wait outside the lock so the loop can finish without deadlocking.
        var wait = Task.Run(() =>
        {
            try
            {
                loop?.Wait(TimeSpan.FromSeconds(2));
            }
            catch (AggregateException)
            {
                // Cancellation/loop faults are already reported via CameraError.
            }
        }, cancellationToken);

        // CancellationToken.None is deliberate: this is the teardown continuation.
        // Passing the caller's token meant a cancelled StopAsync never disposed
        // _capture, leaking the device handle so the next StartAsync failed with
        // "camera in use".
        return wait.ContinueWith(_ =>
        {
            lock (_gate)
            {
                if (_capture is not null)
                {
                    _logger?.LogInformation("Camera stopped: {Name}", _selected?.ToString() ?? "<default>");
                    _capture.Dispose();
                    _capture = null;
                }

                _latestFrame?.Dispose();
                _latestFrame = null;

                _selected = null;
                _loopCts?.Dispose();
            }
        }, CancellationToken.None);
    }

    public Mat? CaptureFrame()
    {
        lock (_gate)
        {
            // Deliberately does NOT call VideoCapture.Read(): the capture loop
            // owns the device. This returns a private copy of the last frame the
            // loop produced, so it can never race or corrupt the reader.
            if (_disposed || _latestFrame is null || _latestFrame.Empty())
                return null;

            try
            {
                return _latestFrame.Clone();
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "CaptureFrame failed");
                return null;
            }
        }
    }

    private static bool IsLikelyPermissionError(string message)
    {
        var m = message ?? string.Empty;
        return m.Contains("denied", StringComparison.OrdinalIgnoreCase) ||
               m.Contains("privacy", StringComparison.OrdinalIgnoreCase) ||
               m.Contains("permission", StringComparison.OrdinalIgnoreCase) ||
               m.Contains("in use", StringComparison.OrdinalIgnoreCase) ||
               m.Contains("access", StringComparison.OrdinalIgnoreCase);
    }

    private void RaiseError(string message, Exception? ex, bool isPermission)
    {
        try
        {
            CameraError?.Invoke(this, new CameraErrorEventArgs
            {
                Message = message,
                Exception = ex,
                IsPermissionError = isPermission,
            });
        }
        catch (Exception handlerEx)
        {
            _logger?.LogWarning(handlerEx, "A CameraError subscriber threw");
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        try
        {
            StopAsync().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Error while stopping camera during dispose");
        }

        lock (_gate)
        {
            _capture?.Dispose();
            _capture = null;
            _latestFrame?.Dispose();
            _latestFrame = null;
            _loopCts?.Dispose();
        }

        GC.SuppressFinalize(this);
    }
}
