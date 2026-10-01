using Security.Core.Enums;

namespace Security.Core.Models;

/// <summary>
/// Result of quality-validating one enrollment frame.
/// </summary>
public sealed class SampleQualityResult
{
    public bool IsAcceptable { get; init; }

    public SampleQualityIssue Issue { get; init; } = SampleQualityIssue.None;

    /// <summary>Optional guidance shown to the user during enrollment.</summary>
    public string Message { get; init; } = string.Empty;

    /// <summary>Blur score (variance of Laplacian). Higher is sharper.</summary>
    public double BlurScore { get; init; }

    /// <summary>Mean luminance 0..255.</summary>
    public double Brightness { get; init; }

    /// <summary>Detected face size in pixels (width of the bounding box).</summary>
    public double FaceSize { get; init; }

    public static SampleQualityResult Accept(double blur, double brightness, double faceSize) => new()
    {
        IsAcceptable = true,
        Issue = SampleQualityIssue.None,
        BlurScore = blur,
        Brightness = brightness,
        FaceSize = faceSize,
        Message = "Sample captured",
    };

    public static SampleQualityResult Reject(SampleQualityIssue issue, string message, double blur = 0, double brightness = 0, double faceSize = 0) => new()
    {
        IsAcceptable = false,
        Issue = issue,
        Message = message,
        BlurScore = blur,
        Brightness = brightness,
        FaceSize = faceSize,
    };
}
