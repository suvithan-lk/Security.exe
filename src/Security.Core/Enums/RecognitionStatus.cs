namespace Security.Core.Enums;

/// <summary>
/// High level verdict produced by the face recognition service.
/// </summary>
public enum RecognitionStatus
{
    /// <summary>A face matched the enrolled profile within the configured threshold.</summary>
    Known,

    /// <summary>A face was analysed but did not match the enrolled profile.</summary>
    Unknown,

    /// <summary>
    /// No verdict could be produced (no face, multiple faces, poor quality,
    /// missing model, or an internal failure). Never treated as an identity claim.
    /// </summary>
    UnableToDetermine,
}
