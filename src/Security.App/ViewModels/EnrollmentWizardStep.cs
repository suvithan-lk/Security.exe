namespace Security.App.ViewModels;

/// <summary>
/// The five-step face enrollment wizard, plus its validation-failed branch.
///
/// Transitions are explicit rather than inferred from raw service state so a
/// stale notification can never drag a finished wizard back a step. Only
/// Capture → Processing → Complete/Failed advances automatically, because that
/// is the segment the user has no way to drive themselves.
/// </summary>
public enum EnrollmentWizardStep
{
    /// <summary>What enrollment does, and the privacy statement.</summary>
    Welcome,

    /// <summary>Live preview; the operator lines up a single, well-lit face.</summary>
    Position,

    /// <summary>n / 20 with progress bar, pose prompt, and cooldown pacing.</summary>
    Capture,

    /// <summary>Samples complete; averaging, encrypting and persisting.</summary>
    Processing,

    /// <summary>Profile name, created date, samples used, model version.</summary>
    Complete,

    /// <summary>Validation failed: reason, causes, Retry and Cancel.</summary>
    Failed,
}
