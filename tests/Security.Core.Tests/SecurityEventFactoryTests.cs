using Security.Core.Enums;
using Security.Core.Entities;
using Security.Core.Services;
using Xunit;

namespace Security.Core.Tests;

public class SecurityEventFactoryTests
{
    [Fact]
    public void Create_maps_fields_and_defaults_timestamp_to_utc_now()
    {
        var before = DateTime.UtcNow.AddSeconds(-1);

        var evt = SecurityEventFactory.Create(
            SecurityEventType.KnownFaceDetected,
            SecurityEventResult.Known,
            "Face matched the enrolled profile.",
            confidence: 0.82);

        var after = DateTime.UtcNow.AddSeconds(1);

        Assert.Equal(SecurityEventType.KnownFaceDetected, evt.EventType);
        Assert.Equal(SecurityEventResult.Known, evt.Result);
        Assert.Equal("Face matched the enrolled profile.", evt.Description);
        Assert.Equal(0.82, evt.Confidence!.Value, 6);
        Assert.InRange(evt.Timestamp, before, after);
        Assert.Equal(0, evt.Id); // assigned by the store
    }

    [Theory]
    [InlineData(0.5, 0.5)]
    [InlineData(0.0, 0.0)]
    [InlineData(1.0, 1.0)]
    [InlineData(-3, 0.0)]       // clamped
    [InlineData(9, 1.0)]        // clamped
    [InlineData(double.NaN, 0.0)] // out-of-range => null
    public void Confidence_is_clamped_or_nulled(double input, double expected)
    {
        var value = SecurityEventFactory.NormalizeConfidence(input);

        if (double.IsNaN(input))
            Assert.Null(value);
        else
            Assert.Equal(expected, value!.Value, 6);
    }

    [Fact]
    public void Null_confidence_stays_null()
    {
        Assert.Null(SecurityEventFactory.NormalizeConfidence(null));
    }

    [Theory]
    [InlineData(SecurityEventType.CameraStarted, SecurityEventResult.Info, "Camera started")]
    [InlineData(SecurityEventType.EnrollmentFailed, SecurityEventResult.Failure, "Too few samples")]
    public void Description_is_trimmed_and_never_empty(
        SecurityEventType type, SecurityEventResult result, string description)
    {
        var evt = SecurityEventFactory.Create(type, result, $"   {description}   ");
        Assert.Equal(description, evt.Description);
    }

    [Fact]
    public void Blank_description_becomes_empty_string_not_null()
    {
        var evt = SecurityEventFactory.Create(
            SecurityEventType.CameraStopped, SecurityEventResult.Info, "   ");
        Assert.Equal(string.Empty, evt.Description);
    }

    [Theory]
    [InlineData(RecognitionStatus.Known, SecurityEventResult.Known)]
    [InlineData(RecognitionStatus.Unknown, SecurityEventResult.Unknown)]
    [InlineData(RecognitionStatus.UnableToDetermine, SecurityEventResult.Info)]
    public void Recognition_status_maps_to_event_result(RecognitionStatus status, SecurityEventResult expected)
    {
        Assert.Equal(expected, SecurityEventFactory.ResultFor(status));
    }

    [Fact]
    public void Created_events_contain_no_biometric_payload_fields()
    {
        // Guard against someone adding an embedding/image field to the entity:
        // the persisted event must only carry metadata.
        var allowed = new HashSet<string>
        {
            nameof(SecurityEvent.Id),
            nameof(SecurityEvent.EventType),
            nameof(SecurityEvent.Result),
            nameof(SecurityEvent.Confidence),
            nameof(SecurityEvent.Timestamp),
            nameof(SecurityEvent.Description),

            // Phase 3 additions — both metadata, never biometric payload:
            // the session state at record time and a *relative* path to a
            // locally stored snapshot file.
            nameof(SecurityEvent.SessionState),
            nameof(SecurityEvent.SnapshotPath),
        };

        var actual = typeof(SecurityEvent)
            .GetProperties()
            .Select(p => p.Name)
            .ToHashSet();

        Assert.True(allowed.SetEquals(actual),
            "SecurityEvent gained or lost fields. Persisted events must carry metadata only: "
            + $"expected [{string.Join(", ", allowed.Order())}], got [{string.Join(", ", actual.Order())}]");
    }
}
