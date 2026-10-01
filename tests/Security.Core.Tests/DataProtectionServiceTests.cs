using Security.Core.Interfaces;
using Security.Infrastructure.Security;
using Xunit;

namespace Security.Core.Tests;

/// <summary>
/// Encryption round-trips for the two IDataProtectionService implementations.
/// These run against the real Windows DPAPI for the current test user.
/// </summary>
public class DataProtectionServiceTests
{
    private static IDataProtectionService CreateDpapi() => new DataProtectionService();

    [Fact]
    public void Protect_returns_base64_that_is_not_the_plaintext()
    {
        var service = CreateDpapi();
        const string secret = "super-secret-biometric-payload";

        var protectedPayload = service.Protect(secret);

        Assert.NotEqual(secret, protectedPayload);
        Assert.DoesNotContain(secret, protectedPayload, StringComparison.Ordinal);
        Assert.True(IsBase64(protectedPayload));
    }

    [Fact]
    public void Protect_unprotect_round_trips()
    {
        var service = CreateDpapi();
        const string secret = "an embedding we must never log";

        Assert.Equal(secret, service.Unprotect(service.Protect(secret)));
    }

    [Fact]
    public void Embedding_round_trips_preserving_length_order_and_values()
    {
        var service = CreateDpapi();
        var embedding = Enumerable.Range(0, 128)
            .Select(i => (float)Math.Sin(i) * 0.25f)
            .ToArray();

        var payload = service.ProtectEmbedding(embedding);
        var restored = service.UnprotectEmbedding(payload);

        Assert.Equal(embedding.Length, restored.Length);
        for (var i = 0; i < embedding.Length; i++)
            Assert.Equal(embedding[i], restored[i], 5);
    }

    [Fact]
    public void Embedding_payload_does_not_contain_readable_values()
    {
        var service = CreateDpapi();
        var embedding = new float[] { 0.123456f, -0.987654f, 0.555555f };

        var payload = service.ProtectEmbedding(embedding);

        Assert.DoesNotContain("0.123456", payload, StringComparison.Ordinal);
        Assert.DoesNotContain("123456", payload, StringComparison.Ordinal);
    }

    [Fact]
    public void Two_encryptions_of_the_same_value_both_decrypt_to_it()
    {
        // DPAPI output need not be non-deterministic; we only require that both
        // payloads decrypt to the same plaintext.
        var service = CreateDpapi();
        const string value = "same input";

        var a = service.Protect(value);
        var b = service.Protect(value);

        Assert.Equal(value, service.Unprotect(a));
        Assert.Equal(value, service.Unprotect(b));
    }

    [Fact]
    public void Unprotect_rejects_tampered_payload_without_echoing_it()
    {
        var service = CreateDpapi();
        var payload = service.Protect("original");

        // Flip a byte in the middle of the ciphertext.
        var bytes = Convert.FromBase64String(payload);
        bytes[bytes.Length / 2] ^= 0xFF;
        var tampered = Convert.ToBase64String(bytes);

        var ex = Assert.ThrowsAny<Exception>(() => service.Unprotect(tampered));
        Assert.DoesNotContain(payload, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Unprotect_rejects_non_base64_input()
    {
        var service = CreateDpapi();
        Assert.ThrowsAny<Exception>(() => service.Unprotect("this is not base64 !!!"));
    }

    [Fact]
    public void Null_arguments_are_rejected()
    {
        var service = CreateDpapi();
        Assert.Throws<ArgumentNullException>(() => service.Protect(null!));
        Assert.Throws<ArgumentNullException>(() => service.Unprotect(null!));
        Assert.Throws<ArgumentNullException>(() => service.ProtectEmbedding(null!));
    }

    [Fact]
    public void Aes_implementation_round_trips_too()
    {
        using var service = new AesDataProtectionService();
        var embedding = new float[] { 1f, 2f, 3f, -4f };

        Assert.Equal("hello", service.Unprotect(service.Protect("hello")));

        var restored = service.UnprotectEmbedding(service.ProtectEmbedding(embedding));
        Assert.Equal(embedding, restored);
    }

    [Fact]
    public void Aes_payload_differs_from_plaintext()
    {
        using var service = new AesDataProtectionService();
        var payload = service.Protect("measured-secret");
        Assert.DoesNotContain("measured-secret", payload, StringComparison.Ordinal);
    }

    private static bool IsBase64(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        try
        {
            Convert.FromBase64String(value);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
