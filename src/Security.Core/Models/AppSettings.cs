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

    /// <summary>
    /// Phase 3 default is ON: closing the window hides it to the tray so
    /// background monitoring continues. Setting it off restores Phase 2's
    /// exit-on-close behaviour.
    /// </summary>
    public bool MinimizeToTray { get; set; } = true;

    // --- Monitoring (Phase 3) ---

    /// <summary>Master switch for the background monitor. Default ON.</summary>
    public bool BackgroundMonitoring { get; set; } = true;

    /// <summary>Observe Windows session state (lock/unlock/logon/logoff). Default ON.</summary>
    public bool SessionMonitoring { get; set; } = true;

    /// <summary>
    /// Run the camera while the session is unlocked (pause on lock, resume on
    /// unlock). Default ON. Never runs on the Secure Desktop.
    /// </summary>
    public bool MonitorCameraWhenUnlocked { get; set; } = true;

    /// <summary>Create UnknownFaceDetected events while unlocked. Default ON.</summary>
    public bool UnknownFaceDetection { get; set; } = true;

    /// <summary>Show desktop (tray balloon) notifications. Default ON.</summary>
    public bool DesktopNotifications { get; set; } = true;

    /// <summary>
    /// Minimum seconds between desktop notifications for the same condition.
    /// Default 30 (spec §26).
    /// </summary>
    public int NotificationCooldownSeconds { get; set; } = 30;

    /// <summary>
    /// Days to keep event snapshots on disk before automatic cleanup.
    /// Allowed: 1, 3, 7, 14, 30 (spec §14). Default 7.
    /// </summary>
    public int SnapshotRetentionDays { get; set; } = 7;

    /// <summary>
    /// Days to keep security event records before automatic cleanup.
    /// Default 30 (spec §33). Recent events are never deleted.
    /// </summary>
    public int EventRetentionDays { get; set; } = 30;

    /// <summary>
    /// Seconds to wait before retrying a failed camera start in the
    /// background (spec §30/§32). Default 30.
    /// </summary>
    public int CameraRetryIntervalSeconds { get; set; } = 30;

    /// <summary>
    /// One-time migration marker. 0 = a database written before Phase 3.
    /// Used only to apply Phase 3 defaults (e.g. MinimizeToTray) exactly once;
    /// it never deletes or rewrites existing user data.
    /// </summary>
    public int SettingsVersion { get; set; }

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
