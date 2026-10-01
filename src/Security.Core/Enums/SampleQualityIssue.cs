namespace Security.Core.Enums;

/// <summary>
/// Rejection reason when an enrollment sample fails quality validation.
/// </summary>
public enum SampleQualityIssue
{
    None,
    NoFaceDetected,
    MultipleFaces,
    FaceTooSmall,
    FaceTooFar,
    Blurry,
    TooDark,
    TooBright,
    FaceNotCentred,
    ProcessingError,
}
