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
}
