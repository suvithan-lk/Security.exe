namespace Security.Core.Models;

/// <summary>
/// Recognition tuning values.
///
/// IMPORTANT: every numeric default here is a STARTING DEFAULT, not a guaranteed
/// optimal value. The similarity threshold in particular must be calibrated
/// against the specific ONNX model and camera in use — see README "Calibrating
/// the recognition threshold".
/// </summary>
public sealed class RecognitionOptions
{
    public const string SectionName = "Recognition";

    /// <summary>
    /// Cosine similarity required for a KNOWN verdict. Default 0.60 is a
    /// placeholder starting point only.
    /// </summary>
    public double Threshold { get; set; } = 0.60;

    /// <summary>Minimum face bounding box width, in pixels, for recognition/enrollment.</summary>
    public int MinimumFaceSize { get; set; } = 120;

    /// <summary>Seconds to wait after a verdict before recognising again.</summary>
    public int RecognitionCooldownSeconds { get; set; } = 5;

    /// <summary>Target detection rate in frames-per-second (5–10 recommended).</summary>
    public int DetectionFps { get; set; } = 8;

    /// <summary>Variance-of-Laplacian below this is considered blurry.</summary>
    public double MinimumBlurScore { get; set; } = 45;

    /// <summary>Mean luminance below this is too dark.</summary>
    public double MinimumBrightness { get; set; } = 35;

    /// <summary>Mean luminance above this is too bright.</summary>
    public double MaximumBrightness { get; set; } = 220;

    /// <summary>Number of samples collected during enrollment.</summary>
    public int EnrollmentSampleCount { get; set; } = 20;

    /// <summary>Fraction of frame width the face must fill to be considered "close enough".</summary>
    public double MinimumFaceRatio { get; set; } = 0.12;

    /// <summary>Frames a face must be seen in a row before we trust it (stability).</summary>
    public int StableFaceFrames { get; set; } = 3;
}
