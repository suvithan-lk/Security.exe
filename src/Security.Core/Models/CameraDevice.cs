namespace Security.Core.Models;

/// <summary>
/// Describes an available video capture device.
/// </summary>
public sealed class CameraDevice
{
    public int Index { get; init; }

    public string Name { get; init; } = string.Empty;

    public override string ToString() => string.IsNullOrWhiteSpace(Name) ? $"Camera {Index}" : Name;
}
