namespace Security.Core.Models;

/// <summary>
/// Progress snapshot pushed to the UI while enrollment runs.
/// </summary>
public sealed class EnrollmentProgress
{
    public int Captured { get; init; }

    public int Target { get; init; }

    public double Percent => Target <= 0 ? 0 : Math.Round(100.0 * Captured / Target, 1);

    public bool IsComplete => Target > 0 && Captured >= Target;

    /// <summary>Guidance currently shown under the progress bar.</summary>
    public string Instruction { get; init; } = string.Empty;

    /// <summary>Most recent rejection reason, if the last frame was refused.</summary>
    public string LastIssue { get; init; } = string.Empty;

    public IReadOnlyList<string> CapturedPoseHints { get; init; } = Array.Empty<string>();
}

/// <summary>
/// Outcome of a full enrollment run.
/// </summary>
public sealed class EnrollmentResult
{
    public bool Succeeded { get; init; }

    public int ProfileId { get; init; }

    public int SampleCount { get; init; }

    public string ModelVersion { get; init; } = string.Empty;

    public string Message { get; init; } = string.Empty;

    public DateTime Timestamp { get; init; } = DateTime.UtcNow;

    public static EnrollmentResult Fail(string message) => new()
    {
        Succeeded = false,
        Message = message,
        Timestamp = DateTime.UtcNow,
    };
}
