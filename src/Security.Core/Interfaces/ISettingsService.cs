using Security.Core.Models;

namespace Security.Core.Interfaces;

/// <summary>
/// Application settings: appsettings.json supplies defaults, the local database
/// stores user overrides.
/// </summary>
public interface ISettingsService
{
    /// <summary>Current effective settings.</summary>
    AppSettings Current { get; }

    /// <summary>Current recognition options (threshold, cooldown, quality gates).</summary>
    RecognitionOptions Recognition { get; }

    Task LoadAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default);

    Task SaveRecognitionAsync(RecognitionOptions options, CancellationToken cancellationToken = default);

    /// <summary>Raised after settings change (any thread).</summary>
    event EventHandler? SettingsChanged;
}
