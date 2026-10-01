using OpenCvSharp;
using Security.Core.Models;

namespace Security.Core.Interfaces;

/// <summary>
/// Video capture device access. Implementations must never block the UI thread
/// and must release the underlying device on Dispose/Stop.
/// </summary>
public interface ICameraService : IDisposable
{
    /// <summary>Enumerate connected capture devices. Safe to call when not running.</summary>
    Task<IReadOnlyList<CameraDevice>> GetAvailableCamerasAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Start capturing on the device at <paramref name="cameraIndex"/>.
    /// An unknown index falls back to the default device rather than failing,
    /// so a stale selection can never leave the camera dead.
    /// </summary>
    Task StartAsync(int cameraIndex, CancellationToken cancellationToken = default);

    /// <summary>Start capturing from <paramref name="camera"/> (or the first device if null).</summary>
    Task StartAsync(CameraDevice? camera = null, CancellationToken cancellationToken = default);

    /// <summary>Release the device. Safe to call when already stopped.</summary>
    Task StopAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Stop, allow the driver a moment to release the device, then start the
    /// previously selected device again. This is the recovery path behind the
    /// camera page's Retry / Restart controls.
    /// </summary>
    Task RestartAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Grab the current frame. Returns null when the camera is stopped or the
    /// frame is invalid. Caller owns the returned Mat and must dispose it.
    /// </summary>
    Mat? CaptureFrame();

    bool IsRunning { get; }

    CameraDevice? SelectedCamera { get; }

    /// <summary>
    /// Raised on a background thread for each captured frame.
    ///
    /// OWNERSHIP: the Mat is valid ONLY for the duration of the handler call and
    /// is disposed immediately afterwards. If you need it later (e.g. to hand to
    /// async work), clone it inside the handler. Never store the reference.
    /// </summary>
    event EventHandler<CameraFrameEventArgs>? FrameReceived;

    /// <summary>Raised when the device errors or disconnects.</summary>
    event EventHandler<CameraErrorEventArgs>? CameraError;
}

public sealed class CameraFrameEventArgs : EventArgs
{
    public required Mat Frame { get; init; }

    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
}

public sealed class CameraErrorEventArgs : EventArgs
{
    public required string Message { get; init; }

    public Exception? Exception { get; init; }

    public bool IsPermissionError { get; init; }
}
