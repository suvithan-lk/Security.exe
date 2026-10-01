namespace Security.Core.Models;

/// <summary>
/// User-facing toggles. Defaults mirror appsettings.json.
/// </summary>
public sealed class AppSettings
{
    public const string SectionName = "Settings";

    // --- Recognition ---
    public bool RecognitionEnabled { get; set; } = true;

    public bool LivenessCheckEnabled { get; set; } = false;

    // --- Privacy ---
    /// <summary>OFF by default. Snapshots are never taken unless explicitly enabled.</summary>
    public bool StoreSnapshots { get; set; }

    /// <summary>ON by default.</summary>
    public bool StoreSecurityEvents { get; set; } = true;

    // --- Application ---
    public bool StartWithWindows { get; set; }

    public bool MinimizeToTray { get; set; }

    // --- Camera ---
    public string? SelectedCamera { get; set; }

    /// <summary>
    /// Requested capture width, applied by <c>CameraService</c> the next time a
    /// device is opened. Held on the settings object (a JSON blob in the
    /// key/value table) rather than a new column, so no migration is involved.
    /// </summary>
    public int CameraWidth { get; set; } = 1280;

    public int CameraHeight { get; set; } = 720;
}
