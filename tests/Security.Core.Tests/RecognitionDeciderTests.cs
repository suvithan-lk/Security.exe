using Security.Core.Enums;
using Security.Core.Services;
using Xunit;

namespace Security.Core.Tests;

/// <summary>
/// Threshold / verdict logic. These tests encode the contract the UI relies on:
/// never claim an identity without enough similarity, and never crash.
/// </summary>
public class RecognitionDeciderTests
{
    [Theory]
    [InlineData(0.95, 0.60)]
    [InlineData(0.60, 0.60)]   // exactly at threshold => KNOWN
    [InlineData(0.61, 0.60)]
    public void Similarity_at_or_above_threshold_is_Known(double similarity, double threshold)
    {
        Assert.Equal(RecognitionStatus.Known, RecognitionDecider.Decide(similarity, threshold));
    }

    [Theory]
    [InlineData(0.59, 0.60)]
    [InlineData(0.0, 0.60)]
    [InlineData(0.99, 1.00 - 1e-9)] // just below a threshold of ~1.0
    public void Similarity_below_threshold_is_Unknown(double similarity, double threshold)
    {
        Assert.Equal(RecognitionStatus.Unknown, RecognitionDecider.Decide(similarity, threshold));
    }

    [Theory]
    [InlineData(double.NaN, 0.60)]
    [InlineData(double.PositiveInfinity, 0.60)]
    [InlineData(-5, 0.60)]
    [InlineData(42, 0.60)]
    public void Out_of_range_similarity_is_clamped_not_crashing(double similarity, double threshold)
    {
        var status = RecognitionDecider.Decide(similarity, threshold);
        Assert.True(status is RecognitionStatus.Known or RecognitionStatus.Unknown);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-0.5)]
    [InlineData(1.5)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void Invalid_threshold_falls_back_to_default(double threshold)
    {
        Assert.Equal(RecognitionDecider.DefaultThreshold, RecognitionDecider.NormalizeThreshold(threshold));
    }

    [Fact]
    public void Default_threshold_is_documented_sixty_percent()
    {
        Assert.Equal(0.60, RecognitionDecider.DefaultThreshold, 6);
    }

    [Fact]
    public void CosineSimilarity_of_identical_vectors_is_one()
    {
        var v = new float[] { 0.1f, -0.4f, 0.9f, 0.3f };
        Assert.Equal(1.0, RecognitionDecider.CosineSimilarity(v, v), 6);
    }

    [Fact]
    public void CosineSimilarity_of_opposite_vectors_is_zero_not_negative()
    {
        // Clamped to 0..1: a negative correlation is "no similarity", not
        // something the UI would have to interpret.
        var a = new float[] { 1f, 0f };
        var b = new float[] { -1f, 0f };
        var similarity = RecognitionDecider.CosineSimilarity(a, b);
        Assert.True(similarity >= 0);
        Assert.True(similarity <= 1);
    }

    [Fact]
    public void CosineSimilarity_of_mismatched_lengths_returns_zero()
    {
        Assert.Equal(0, RecognitionDecider.CosineSimilarity(new float[] { 1f }, new float[] { 1f, 2f }));
    }

    [Fact]
    public void CosineSimilarity_with_empty_or_null_returns_zero()
    {
        Assert.Equal(0, RecognitionDecider.CosineSimilarity(Array.Empty<float>(), Array.Empty<float>()));
        Assert.Equal(0, RecognitionDecider.CosineSimilarity(null!, new float[] { 1f }));
    }

    [Fact]
    public void CosineSimilarity_with_non_finite_values_returns_zero()
    {
        var a = new float[] { float.NaN, 1f };
        var b = new float[] { 1f, 1f };
        Assert.Equal(0, RecognitionDecider.CosineSimilarity(a, b));
    }

    [Fact]
    public void L2Normalize_produces_unit_norm()
    {
        var normalized = RecognitionDecider.L2Normalize(new float[] { 3f, 4f });
        var norm = Math.Sqrt(normalized.Sum(v => (double)v * v));
        Assert.Equal(1.0, norm, 5);
    }

    [Fact]
    public void L2Normalize_leaves_zero_vector_unchanged()
    {
        var normalized = RecognitionDecider.L2Normalize(new float[] { 0f, 0f, 0f });
        Assert.All(normalized, v => Assert.Equal(0f, v));
    }

    [Fact]
    public void AverageAndNormalize_returns_empty_for_no_samples()
    {
        Assert.Empty(RecognitionDecider.AverageAndNormalize(new List<float[]>()));
    }

    [Fact]
    public void AverageAndNormalize_averages_then_normalizes()
    {
        var samples = new List<float[]>
        {
            new float[] { 1f, 0f },
            new float[] { 0f, 1f },
            new float[] { 1f, 0f },
        };

        var result = RecognitionDecider.AverageAndNormalize(samples);

        Assert.Equal(2, result.Length);
        // mean = (2/3, 1/3) -> direction preserved, norm == 1
        var norm = Math.Sqrt(result.Sum(v => (double)v * v));
        Assert.Equal(1.0, norm, 5);
        Assert.True(result[0] > result[1]);
    }

    [Fact]
    public void AverageAndNormalize_skips_mismatched_dimensions()
    {
        var samples = new List<float[]> { new float[] { 1f, 0f }, new float[] { 1f, 0f, 0f } };
        var result = RecognitionDecider.AverageAndNormalize(samples);

        Assert.Equal(2, result.Length);
        var norm = Math.Sqrt(result.Sum(v => (double)v * v));
        Assert.Equal(1.0, norm, 5);
    }
}
