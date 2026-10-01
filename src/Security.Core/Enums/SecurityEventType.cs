namespace Security.Core.Enums;

/// <summary>
/// Categories of security events recorded by the application.
/// </summary>
public enum SecurityEventType
{
    ApplicationStarted,
    ApplicationStopped,
    CameraStarted,
    CameraStopped,
    CameraError,
    EnrollmentStarted,
    EnrollmentCompleted,
    EnrollmentFailed,
    FaceDetected,
    KnownFaceDetected,
    UnknownFaceDetected,
    RecognitionFailed,
    SettingChanged,
    EventsCleared,

    // --- Phase 3: Windows session monitoring ---
    SessionLocked,
    SessionUnlocked,
    SessionLogon,
    SessionLogoff,
    SessionConnected,
    SessionDisconnected,

    // --- Phase 3: background monitoring lifecycle ---
    MonitoringStarted,
    MonitoringStopped,
    MonitoringPaused,
    MonitoringResumed,

    // --- Phase 3: notifications and snapshots ---
    NotificationSent,
    SnapshotCaptured,
    SnapshotDeleted,
}
