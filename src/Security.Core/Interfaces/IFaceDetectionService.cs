using OpenCvSharp;
using Security.Core.Models;

namespace Security.Core.Interfaces;

/// <summary>
/// Locates human faces inside a frame.
/// </summary>
public interface IFaceDetectionService : IDisposable
{
    /// <summary>True when the underlying detection model is loaded.</summary>
    bool IsReady { get; }

    FaceDetectionResult Detect(Mat frame);

    /// <summary>
    /// Run detection and keep only the single best face. Multi-face frames come
    /// back with zero faces so downstream code cannot accidentally act on a crowd.
    /// </summary>
    FaceDetectionResult DetectPrimary(Mat frame);
}
