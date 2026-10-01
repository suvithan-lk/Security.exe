namespace Security.Core.Models;

/// <summary>
/// A quality/ambience measurement of a frame, used by the quality gate.
/// </summary>
public sealed class FrameQualityMetrics
{
    /// <summary>Variance of Laplacian. Low values mean blurry.</summary>
    public double BlurScore { get; init; }

    /// <summary>Mean luminance 0..255.</summary>
    public double Brightness { get; init; }

    public double FaceSizePx { get; init; }

    public double FaceSizeRatio { get; init; }
}
