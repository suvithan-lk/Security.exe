namespace Security.Core.Models;

/// <summary>
/// Camera capture tuning values.
/// </summary>
public sealed class CameraOptions
{
    public const string SectionName = "Camera";

    /// <summary>Requested capture width.</summary>
    public int Width { get; set; } = 1280;

    /// <summary>Requested capture height.</summary>
    public int Height { get; set; } = 720;

    /// <summary>Preferred capture backend index. 0 = let OpenCV choose.</summary>
    public int Backend { get; set; }
}
