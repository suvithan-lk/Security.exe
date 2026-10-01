using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Security.Core.Interfaces;

namespace Security.Infrastructure.Security;

/// <summary>
/// DPAPI-based local protection for sensitive values (face embeddings).
///
/// Security model:
///  - Uses Windows Data Protection API scoped to <see cref="DataProtectionScope.CurrentUser"/>.
///  - Ciphertext is only decryptable by the same Windows user on the same machine.
///  - Moving security.db to another account/machine makes embeddings undecryptable
///    (the profile must be re-enrolled) — this is intentional.
///  - Plaintext is never logged and never included in exception messages.
/// </summary>
public sealed class DataProtectionService : IDataProtectionService
{
    private const string EmbeddingTag = "SEC.EXE.EMB.v1";

    private readonly ILogger<DataProtectionService>? _logger;

    public DataProtectionService(ILogger<DataProtectionService>? logger = null)
    {
        _logger = logger;
    }

    public string Protect(string plaintext)
    {
        ArgumentNullException.ThrowIfNull(plaintext);

        var bytes = Encoding.UTF8.GetBytes(plaintext);
        var encrypted = ProtectedData.Protect(bytes, GetEntropy(), DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(encrypted);
    }

    public string Unprotect(string protectedPayload)
    {
        ArgumentNullException.ThrowIfNull(protectedPayload);

        byte[] encrypted;
        try
        {
            encrypted = Convert.FromBase64String(protectedPayload);
        }
        catch (FormatException ex)
        {
            // Do not echo the payload — it may be a partial ciphertext.
            throw new CryptographicException("Protected payload is not valid base64.", ex);
        }

        byte[] plain;
        try
        {
            plain = ProtectedData.Unprotect(encrypted, GetEntropy(), DataProtectionScope.CurrentUser);
        }
        catch (CryptographicException ex)
        {
            // Common when the database is opened by a different Windows user
            // or restored from another machine.
            throw new CryptographicException(
                "Protected data could not be decrypted. It was likely protected by a " +
                "different Windows user or on a different machine. Re-enrollment is required.",
                ex);
        }

        return Encoding.UTF8.GetString(plain);
    }

    public string ProtectEmbedding(float[] embedding)
    {
        ArgumentNullException.ThrowIfNull(embedding);
        if (embedding.Length == 0)
            throw new ArgumentException("Embedding must not be empty.", nameof(embedding));

        // Layout: [tag]\n[dim:int32][float32 * dim]
        using var ms = new MemoryStream();
        using (var writer = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(EmbeddingTag);
            writer.Write(embedding.Length);
            foreach (var value in embedding)
                writer.Write(float.IsFinite(value) ? value : 0f);
        }

        var encrypted = ProtectedData.Protect(ms.ToArray(), GetEntropy(), DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(encrypted);
    }

    public float[] UnprotectEmbedding(string protectedPayload)
    {
        var payload = UnprotectProtectedBytes(protectedPayload);

        try
        {
            using var ms = new MemoryStream(payload);
            using var reader = new BinaryReader(ms, Encoding.UTF8);

            var tag = reader.ReadString();
            if (!string.Equals(tag, EmbeddingTag, StringComparison.Ordinal))
                throw new CryptographicException("Protected payload has an unexpected format.");

            var dim = reader.ReadInt32();
            if (dim <= 0 || dim > 100_000)
                throw new CryptographicException("Protected payload declares an invalid embedding size.");

            var values = new float[dim];
            for (var i = 0; i < dim; i++)
                values[i] = reader.ReadSingle();

            return values;
        }
        catch (EndOfStreamException ex)
        {
            throw new CryptographicException("Protected payload is truncated.", ex);
        }
        catch (IOException ex)
        {
            throw new CryptographicException("Protected payload could not be read.", ex);
        }
    }

    private byte[] UnprotectProtectedBytes(string protectedPayload)
    {
        ArgumentNullException.ThrowIfNull(protectedPayload);

        byte[] encrypted;
        try
        {
            encrypted = Convert.FromBase64String(protectedPayload);
        }
        catch (FormatException ex)
        {
            throw new CryptographicException("Protected payload is not valid base64.", ex);
        }

        try
        {
            return ProtectedData.Unprotect(encrypted, GetEntropy(), DataProtectionScope.CurrentUser);
        }
        catch (CryptographicException ex)
        {
            _logger?.LogWarning(ex, "Failed to decrypt a protected payload");
            throw new CryptographicException(
                "Protected data could not be decrypted. Re-enrollment is required.",
                ex);
        }
    }

    /// <summary>
    /// Optional additional secret mixed into DPAPI. Adds a small amount of
    /// hardening against raw blob replay, but the real boundary remains DPAPI
    /// + the Windows user profile.
    /// </summary>
    private static byte[] GetEntropy()
        => Encoding.UTF8.GetBytes("SECURITY.EXE:Phase1:FaceEmbedding:v1");
}
