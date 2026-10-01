using OpenCvSharp;
using Security.Core.Models;

namespace Security.Core.Interfaces;

/// <summary>
/// Orchestrates the per-frame analysis pipeline.
///
/// Pipeline: camera frame -> throttle -> detect -> (stability + quality gate)
///           -> recognize (cooldown-limited) -> security event.
///
/// Recognition is deliberately NOT run on every frame.
/// </summary>
public interface IFrameProcessor : IDisposable
{
    /// <summary>Begin consuming frames from a camera service.</summary>
    void Attach(ICameraService camera);

    /// <summary>Stop consuming frames (does not stop the camera).</summary>
    void Detach();

    bool IsProcessing { get; }

    /// <summary>
    /// Raised on a background thread whenever a frame has been analysed.
    /// Intended for live UI overlay updates.
    /// </summary>
    event EventHandler<FrameAnalysisEventArgs>? FrameAnalyzed;

    /// <summary>Raised when a recognition verdict is produced (after cooldown).</summary>
    event EventHandler<FaceRecognitionResult>? RecognitionCompleted;

    /// <summary>Raised when an unknown face triggers the security alert.</summary>
    event EventHandler<FaceRecognitionResult>? UnknownFaceDetected;

    /// <summary>
    /// While true, detection still runs (for the live overlay) but recognition is
    /// skipped. Set during enrollment so the two flows never contend.
    /// </summary>
    bool RecognitionSuppressed { get; set; }

    /// <summary>
    /// Optional sink invoked on the worker thread with each analysed frame,
    /// after detection. The Mat is valid only for the duration of the call.
    ///
    /// This is how enrollment receives throttled frames without building a
    /// second capture pipeline. The sink is awaited, so a slow sink lowers the
    /// effective detection rate — acceptable while enrolling.
    /// </summary>
    Func<Mat, CancellationToken, Task>? FrameSink { get; set; }
}

public sealed class FrameAnalysisEventArgs : EventArgs
{
    /// <summary>Frame this analysis describes.</summary>
    public required DateTime Timestamp { get; init; }

    /// <summary>Pixel size of the analysed frame, so the UI can map boxes.</summary>
    public required int FrameWidth { get; init; }

    public required int FrameHeight { get; init; }

    public IReadOnlyList<DetectedFace> Faces { get; init; } = Array.Empty<DetectedFace>();

    public int FaceCount => Faces.Count;

    /// <summary>Human-readable detection status for the UI.</summary>
    public string StatusText { get; init; } = string.Empty;

    /// <summary>Populated only when a verdict was produced this frame.</summary>
    public FaceRecognitionResult? Recognition { get; init; }

    /// <summary>Quality message shown during enrollment/normal operation.</summary>
    public string QualityMessage { get; init; } = string.Empty;
}
