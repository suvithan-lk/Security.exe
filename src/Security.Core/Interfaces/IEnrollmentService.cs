using Security.Core.Models;

namespace Security.Core.Interfaces;

/// <summary>
/// Orchestrates the multi-sample face enrollment flow.
/// </summary>
public interface IEnrollmentService
{
    /// <summary>True when an active profile with a stored embedding already exists.</summary>
    bool ProfileExists { get; }

    /// <summary>Raised whenever capture progress changes (on a background thread).</summary>
    event EventHandler<EnrollmentProgress>? ProgressChanged;

    /// <summary>True while an enrollment run is in progress.</summary>
    bool IsEnrolling { get; }

    /// <summary>
    /// Evaluate one camera frame for the in-flight enrollment run.
    /// Returns progress; does not itself own the frame.
    /// </summary>
    Task<EnrollmentProgress> SubmitFrameAsync(OpenCvSharp.Mat frame, CancellationToken cancellationToken = default);

    /// <summary>
    /// Finalize the run: average the collected embeddings, encrypt, persist.
    /// </summary>
    Task<EnrollmentResult> CompleteAsync(CancellationToken cancellationToken = default);

    /// <summary>Abort the run discarding all collected samples.</summary>
    void Cancel();

    /// <summary>Start a new run (invalidates any previous one).</summary>
    void Begin(int targetSamples);
}
