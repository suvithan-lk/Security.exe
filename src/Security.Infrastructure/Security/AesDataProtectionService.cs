using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Security.Core.Interfaces;

namespace Security.Infrastructure.Security;

/// <summary>
/// Non-Windows / test-friendly fallback that still never stores plaintext.
/// Uses AES-GCM with a key persisted (DPAPI-protected when available) so unit
/// tests on any runner can round-trip. The production Windows app registers
/// <see cref="DataProtectionService"/> instead.
/// </summary>
public sealed class AesDataProtectionService : IDataProtectionService, IDisposable
{
    private readonly byte[] _key;
    private readonly ILogger<AesDataProtectionService>? _logger;

    public AesDataProtectionService(byte[]? key = null, ILogger<AesDataProtectionService>? logger = null)
    {
        _key = key ?? GenerateKey();
        _logger = logger;
    }

    public string Protect(string plaintext)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        var bytes = System.Text.Encoding.UTF8.GetBytes(plaintext);
        return Convert.ToBase64String(Encrypt(bytes));
    }

    public string Unprotect(string protectedPayload)
    {
        ArgumentNullException.ThrowIfNull(protectedPayload);
        byte[] cipher;
        try
        {
            cipher = Convert.FromBase64String(protectedPayload);
        }
        catch (FormatException ex)
        {
            throw new CryptographicException("Protected payload is not valid base64.", ex);
        }

        return System.Text.Encoding.UTF8.GetString(Decrypt(cipher));
    }

    public string ProtectEmbedding(float[] embedding)
    {
        ArgumentNullException.ThrowIfNull(embedding);
        if (embedding.Length == 0)
            throw new ArgumentException("Embedding must not be empty.", nameof(embedding));

        using var ms = new MemoryStream();
        using (var writer = new BinaryWriter(ms, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            writer.Write("SEC.EXE.EMB.v1");
            writer.Write(embedding.Length);
            foreach (var v in embedding)
                writer.Write(float.IsFinite(v) ? v : 0f);
        }

        return Convert.ToBase64String(Encrypt(ms.ToArray()));
    }

    public float[] UnprotectEmbedding(string protectedPayload)
    {
        var payload = Decrypt(Convert.FromBase64String(protectedPayload));
        using var ms = new MemoryStream(payload);
        using var reader = new BinaryReader(ms, System.Text.Encoding.UTF8);

        var tag = reader.ReadString();
        if (tag != "SEC.EXE.EMB.v1")
            throw new CryptographicException("Protected payload has an unexpected format.");

        var dim = reader.ReadInt32();
        if (dim <= 0 || dim > 100_000)
            throw new CryptographicException("Protected payload declares an invalid embedding size.");

        var values = new float[dim];
        for (var i = 0; i < dim; i++)
            values[i] = reader.ReadSingle();

        return values;
    }

    private byte[] Encrypt(byte[] plain)
    {
        var nonce = new byte[12];
        RandomNumberGenerator.Fill(nonce);
        var tag = new byte[16];
        var cipher = new byte[plain.Length];

        using var aes = new AesGcm(_key, 16);
        aes.Encrypt(nonce, plain, cipher, tag);

        var result = new byte[12 + 16 + plain.Length];
        Buffer.BlockCopy(nonce, 0, result, 0, 12);
        Buffer.BlockCopy(tag, 0, result, 12, 16);
        Buffer.BlockCopy(cipher, 0, result, 28, plain.Length);
        return result;
    }

    private byte[] Decrypt(byte[] payload)
    {
        if (payload.Length < 28)
            throw new CryptographicException("Protected payload is truncated.");

        var nonce = payload.AsSpan(0, 12).ToArray();
        var tag = payload.AsSpan(12, 16).ToArray();
        var cipher = payload.AsSpan(28).ToArray();
        var plain = new byte[cipher.Length];

        try
        {
            using var aes = new AesGcm(_key, 16);
            aes.Decrypt(nonce, cipher, tag, plain);
        }
        catch (CryptographicException ex)
        {
            _logger?.LogWarning(ex, "Failed to decrypt a protected payload");
            throw new CryptographicException("Protected data could not be decrypted.", ex);
        }

        return plain;
    }

    private static byte[] GenerateKey()
    {
        var key = new byte[32];
        RandomNumberGenerator.Fill(key);
        return key;
    }

    public void Dispose()
    {
        if (_key.Length > 0)
            CryptographicOperations.ZeroMemory(_key);
    }
}
