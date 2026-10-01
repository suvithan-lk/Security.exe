using OpenCvSharp;
using Security.Core.Models;
using Security.Face.Detection;
using Xunit;

namespace Security.Face.Tests;

/// <summary>
/// Phase 1 liveness is a placeholder. These tests lock down the critical
/// property: it must never report a pass, because a fake "you are live" is
/// worse than an honest "unknown".
/// </summary>
public class LivenessServiceTests
{
    [Fact]
    public async Task Check_never_reports_a_live_face()
    {
        var service = new LivenessService();
        using var frame = new Mat(720, 1280, MatType.CV_8UC3, Scalar.All(128));

        var result = await service.CheckAsync(frame);

        Assert.False(result.IsLive);
        Assert.Equal(0, result.Confidence);
    }

    [Fact]
    public async Task Check_reports_the_placeholder_method_and_explicit_notes()
    {
        var service = new LivenessService();
        using var frame = new Mat(64, 64, MatType.CV_8UC3, Scalar.All(10));

        var result = await service.CheckAsync(frame);

        Assert.Equal(LivenessMethods.BasicPlaceholder, result.Method);
        Assert.False(string.IsNullOrWhiteSpace(result.Notes));
        Assert.Contains("NOT production anti-spoofing", result.Notes, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Service_declares_itself_unavailable()
    {
        var service = new LivenessService();
        Assert.False(service.IsAvailable);
        Assert.Equal(LivenessMethods.BasicPlaceholder, service.Method);
    }

    [Fact]
    public async Task Null_frame_is_rejected()
    {
        var service = new LivenessService();
        await Assert.ThrowsAsync<ArgumentNullException>(() => service.CheckAsync(null!));
    }

    [Fact]
    public async Task Result_timestamp_is_recent_and_utc()
    {
        var service = new LivenessService();
        using var frame = new Mat(32, 32, MatType.CV_8UC3);

        var before = DateTime.UtcNow.AddSeconds(-5);
        var result = await service.CheckAsync(frame);
        var after = DateTime.UtcNow.AddSeconds(5);

        Assert.InRange(result.Timestamp, before, after);
    }

    [Fact]
    public async Task Repeated_checks_stay_indeterminate()
    {
        var service = new LivenessService();
        using var frame = new Mat(32, 32, MatType.CV_8UC3, Scalar.All(200));

        for (var i = 0; i < 5; i++)
        {
            var result = await service.CheckAsync(frame);
            Assert.False(result.IsLive);
            Assert.Equal(0, result.Confidence);
        }
    }
}

// build probe 1790759077
