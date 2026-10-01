using Security.Core.Enums;

namespace Security.Core.Models;

/// <summary>
/// Result of the (Phase 1 placeholder) liveness check.
/// </summary>
public sealed class LivenessResult
{
    public bool IsLive { get; init; }

    /// <summary>0..1. Phase 1 never returns a meaningful value.</summary>
    public double Confidence { get; init; }

    /// <summary>Which method produced this result.</summary>
    public string Method { get; init; } = LivenessMethods.BasicPlaceholder;

    public DateTime Timestamp { get; init; } = DateTime.UtcNow;

    /// <summary>Always populated with an honest description of what this result means.</summary>
    public string Notes { get; init; } = string.Empty;

    public static LivenessResult Placeholder(DateTime timestamp) => new()
    {
        // Phase 1 deliberately returns "indeterminate" rather than a fake pass.
        IsLive = false,
        Confidence = 0,
        Method = LivenessMethods.BasicPlaceholder,
        Timestamp = timestamp,
        Notes = "Basic liveness foundation — NOT production anti-spoofing. " +
                "This build does not attempt to defeat photographs, videos, deepfakes, masks, " +
                "or other presentation attacks.",
    };
}

public static class LivenessMethods
{
    public const string BasicPlaceholder = "basic-placeholder";
}
