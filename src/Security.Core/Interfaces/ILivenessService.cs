using OpenCvSharp;
using Security.Core.Models;

namespace Security.Core.Interfaces;

/// <summary>
/// Liveness / presentation-attack detection.
///
/// PHASE 1 STATUS: basic foundation only — NOT production anti-spoofing.
/// The Phase 1 implementation does not attempt to defeat photographs, videos,
/// deepfakes, masks, or any other presentation attack, and must not be
/// represented as if it does.
/// </summary>
public interface ILivenessService
{
    /// <summary>True when a real checking algorithm is available (false in Phase 1).</summary>
    bool IsAvailable { get; }

    /// <summary>Short description of the method in use.</summary>
    string Method { get; }

    /// <summary>
    /// Assess a frame. Phase 1 returns an explicitly indeterminate placeholder.
    /// </summary>
    Task<LivenessResult> CheckAsync(Mat frame, CancellationToken cancellationToken = default);
}
