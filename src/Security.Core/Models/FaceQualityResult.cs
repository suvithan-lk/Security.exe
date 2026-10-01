namespace Security.Core.Models;

/// <summary>
/// Verdict of a single frame quality evaluation, used to drive live guidance
/// during enrollment and recognition.
///
/// <see cref="Reason"/> is always a short, operator-facing instruction. It never
/// contains pixel data, embeddings, or anything derived from the image itself.
/// </summary>
public sealed class FaceQualityResult
{
    /// <summary>True when this frame is good enough to be used as a sample.</summary>
    public bool IsAcceptable { get; init; }

    /// <summary>
    /// Composite quality score in the range 0..1. 1 is ideal, 0 is unusable.
    /// Provided so the UI can show a bar; thresholds are decided by
    /// <see cref="IsAcceptable"/>.
    /// </summary>
    public double Score { get; init; }

    /// <summary>Operator-facing guidance. Empty when <see cref="IsAcceptable"/>.</summary>
    public string Reason { get; init; } = string.Empty;

    // --- Measurements behind the verdict, exposed for diagnostics/tests. ---
    public double BlurScore { get; init; }
    public double Brightness { get; init; }
    public double FaceWidthRatio { get; init; }
    public int FaceCount { get; init; }

    /// <summary>Centre offset of the face from frame centre, as a 0..1 fraction.</summary>
    public double CenteringOffset { get; init; }

    public static FaceQualityResult Accept(double score, double blur, double brightness, double ratio, double offset) => new()
    {
        IsAcceptable = true,
        Score = Math.Clamp(score, 0, 1),
        Reason = string.Empty,
        BlurScore = blur,
        Brightness = brightness,
        FaceWidthRatio = ratio,
        FaceCount = 1,
        CenteringOffset = offset,
    };

    public static FaceQualityResult Reject(string reason, double score, double blur = 0, double brightness = 0,
        double ratio = 0, int faceCount = 0, double offset = 0) => new()
    {
        IsAcceptable = false,
        Score = Math.Clamp(score, 0, 1),
        Reason = reason,
        BlurScore = blur,
        Brightness = brightness,
        FaceWidthRatio = ratio,
        FaceCount = faceCount,
        CenteringOffset = offset,
    };
}
