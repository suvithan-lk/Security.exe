namespace Security.Core.Entities;

/// <summary>
/// Encrypted face embedding belonging to a <see cref="UserProfile"/>.
/// The raw float vector is never stored in plaintext.
/// </summary>
public class FaceEmbedding
{
    public int Id { get; set; }

    public int UserProfileId { get; set; }

    public UserProfile? UserProfile { get; set; }

    /// <summary>
    /// DPAPI-protected payload. On disk this is base64 of the encrypted bytes.
    /// NEVER log, transmit, or expose this value.
    /// </summary>
    public string EmbeddingData { get; set; } = string.Empty;

    /// <summary>Identifier of the ONNX model that produced the embedding.</summary>
    public string ModelVersion { get; set; } = string.Empty;

    /// <summary>Number of samples averaged into this representation.</summary>
    public int SampleCount { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
