namespace Security.Core.Entities;

/// <summary>
/// The single local user profile for this application.
/// Phase 1 supports exactly one active profile.
/// </summary>
public class UserProfile
{
    public int Id { get; set; }

    public string DisplayName { get; set; } = "Default User";

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public bool IsActive { get; set; } = true;

    /// <summary>Navigation to the stored biometric representation.</summary>
    public FaceEmbedding? FaceEmbedding { get; set; }
}
