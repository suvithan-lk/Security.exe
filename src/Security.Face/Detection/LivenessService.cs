using Microsoft.Extensions.Logging;
using OpenCvSharp;
using Security.Core.Interfaces;
using Security.Core.Models;

namespace Security.Face.Detection;

/// <summary>
/// PHASE 1 LIVENESS FOUNDATION — NOT PRODUCTION ANTI-SPOOFING.
///
/// This implementation performs NO presentation-attack detection whatsoever.
/// It exists so the pipeline, settings toggle, and result type are in place for
/// a real implementation in a later phase.
///
/// It deliberately reports IsLive = false with Confidence = 0 rather than a
/// fake "pass", so no caller can mistake it for an actual anti-spoofing check.
///
/// It does NOT protect against: photographs, videos, deepfakes, masks, replay,
/// or any other presentation attack.
/// </summary>
public sealed class LivenessService : ILivenessService
{
    private readonly ILogger<LivenessService>? _logger;
    private bool _warned;

    public LivenessService(ILogger<LivenessService>? logger = null)
    {
        _logger = logger;
    }

    /// <summary>Always false in Phase 1 — no real check exists.</summary>
    public bool IsAvailable => false;

    public string Method => LivenessMethods.BasicPlaceholder;

    public Task<LivenessResult> CheckAsync(Mat frame, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(frame);

        if (!_warned)
        {
            _warned = true;
            _logger?.LogWarning(
                "Liveness check invoked but this build has NO anti-spoofing capability. " +
                "Results are indeterminate by design and must not be treated as verification.");
        }

        // Phase 1: indeterminate. It does not even attempt to analyse the frame,
        // because a heuristic that pretends to detect liveness is more dangerous
        // than an honest "unknown".
        return Task.FromResult(LivenessResult.Placeholder(DateTime.UtcNow));
    }
}
