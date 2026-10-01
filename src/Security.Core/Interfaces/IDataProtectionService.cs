namespace Security.Core.Interfaces;

/// <summary>
/// Local protection for sensitive values (face embeddings).
/// Windows DPAPI scoped to the current user.
/// </summary>
public interface IDataProtectionService
{
    /// <summary>Encrypt plaintext and return a base64 string safe for database storage.</summary>
    string Protect(string plaintext);

    /// <summary>Reverse <see cref="Protect"/>. Throws on tampered/unreadable input.</summary>
    string Unprotect(string protectedPayload);

    /// <summary>Encrypt a float vector (length-prefixed binary, then base64).</summary>
    string ProtectEmbedding(float[] embedding);

    /// <summary>Decrypt a payload produced by <see cref="ProtectEmbedding"/>.</summary>
    float[] UnprotectEmbedding(string protectedPayload);
}
