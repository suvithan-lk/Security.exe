using OpenCvSharp;
using Security.Core.Models;

namespace Security.Core.Interfaces;

/// <summary>
/// Detects a face, embeds it, and compares it against the enrolled profile.
/// </summary>
public interface IFaceRecognitionService
{
    /// <summary>True when detection + embedding models are loaded.</summary>
    bool IsReady { get; }

    /// <summary>True when an active profile with a stored embedding exists.</summary>
    bool HasEnrolledProfile { get; }

    double Threshold { get; }

    /// <summary>
    /// Analyse a full frame and return a verdict.
    /// Never throws for expected conditions — returns UnableToDetermine instead.
    /// </summary>
    Task<FaceRecognitionResult> RecognizeAsync(Mat frame, CancellationToken cancellationToken = default);
}
