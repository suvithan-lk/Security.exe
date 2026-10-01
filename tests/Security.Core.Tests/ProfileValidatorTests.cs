using Security.Core.Models;
using Security.Core.Services;
using Xunit;

namespace Security.Core.Tests;

public class ProfileValidatorTests
{
    [Theory]
    [InlineData("Default User", true)]
    [InlineData("  Ada  ", true)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData(null, false)]
    public void Display_name_validation(string? name, bool expected)
    {
        Assert.Equal(expected, ProfileValidator.IsValidDisplayName(name));
    }

    [Fact]
    public void Display_name_longer_than_limit_is_rejected()
    {
        var tooLong = new string('a', ProfileValidator.MaxDisplayNameLength + 1);
        Assert.False(ProfileValidator.IsValidDisplayName(tooLong));
    }

    [Fact]
    public void Display_name_at_limit_is_accepted()
    {
        var exact = new string('a', ProfileValidator.MaxDisplayNameLength);
        Assert.True(ProfileValidator.IsValidDisplayName(exact));
    }

    [Theory]
    [InlineData(null, "Default User")]
    [InlineData("", "Default User")]
    [InlineData("   ", "Default User")]
    public void Sanitize_falls_back_when_blank(string? name, string expected)
    {
        Assert.Equal(expected, ProfileValidator.SanitizeDisplayName(name));
    }

    [Fact]
    public void Sanitize_trims_and_truncates()
    {
        var longName = new string('x', ProfileValidator.MaxDisplayNameLength + 20);
        var sanitized = ProfileValidator.SanitizeDisplayName(longName);

        Assert.Equal(ProfileValidator.MaxDisplayNameLength, sanitized.Length);
    }

    [Fact]
    public void Embedding_validation_accepts_finite_vector()
    {
        Assert.True(ProfileValidator.IsValidEmbedding(new float[] { 0.1f, -0.2f, 0.3f }));
    }

    [Fact]
    public void Embedding_validation_rejects_null_empty_and_non_finite()
    {
        Assert.False(ProfileValidator.IsValidEmbedding(null));
        Assert.False(ProfileValidator.IsValidEmbedding(Array.Empty<float>()));
        Assert.False(ProfileValidator.IsValidEmbedding(new float[] { 1f, float.NaN }));
        Assert.False(ProfileValidator.IsValidEmbedding(new float[] { 1f, float.PositiveInfinity }));
    }

    [Theory]
    [InlineData(120, 800, true)]   // meets px minimum and ratio (15%)
    [InlineData(119, 800, false)]  // below px minimum
    [InlineData(300, 800, true)]
    public void Face_size_gate(double faceWidth, double frameWidth, bool expected)
    {
        var options = new RecognitionOptions { MinimumFaceSize = 120, MinimumFaceRatio = 0.12 };
        Assert.Equal(expected, ProfileValidator.IsFaceLargeEnough(faceWidth, frameWidth, options));
    }

    [Fact]
    public void Face_size_gate_rejects_degenerate_frame()
    {
        var options = new RecognitionOptions();
        Assert.False(ProfileValidator.IsFaceLargeEnough(500, 0, options));
        Assert.False(ProfileValidator.IsFaceLargeEnough(500, -1, options));
    }

    [Fact]
    public void Face_size_gate_rejects_ratio_below_minimum()
    {
        // 130px is above the 120px absolute minimum but only 9% of the frame.
        var options = new RecognitionOptions { MinimumFaceSize = 120, MinimumFaceRatio = 0.12 };
        Assert.False(ProfileValidator.IsFaceLargeEnough(130, 1280, options));
    }

    [Fact]
    public void Face_size_gate_rejects_null_options()
    {
        Assert.False(ProfileValidator.IsFaceLargeEnough(500, 1280, null!));
    }

    [Fact]
    public void CanFinalize_requires_enough_valid_samples()
    {
        var required = 4;
        var good = Enumerable.Range(0, required).Select(_ => new float[] { 1f, 2f }).ToList();

        Assert.True(ProfileValidator.CanFinalize(good, required));
        Assert.False(ProfileValidator.CanFinalize(good.Take(3).ToList(), required));
        Assert.False(ProfileValidator.CanFinalize(null, required));
    }

    [Fact]
    public void CanFinalize_rejects_batch_containing_invalid_embedding()
    {
        var samples = new List<float[]>
        {
            new float[] { 1f, 2f },
            new float[] { 1f, float.NaN },
            new float[] { 1f, 2f },
            new float[] { 1f, 2f },
        };

        Assert.False(ProfileValidator.CanFinalize(samples, 4));
    }
}
