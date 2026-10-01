using System;

namespace Security.App.Services;

/// <summary>
/// Indirection between screens and the shell's navigation rail.
///
/// View models depend on this instead of <c>MainViewModel</c>, which would
/// otherwise create a circular DI graph (shell -> screens -> shell).
/// </summary>
public interface INavigationService
{
    /// <summary>Request navigation to a screen key defined by the shell.</summary>
    void NavigateTo(string key);

    /// <summary>Raised after navigation (any thread). Payload is the screen key.</summary>
    event EventHandler<string>? Navigated;
}

public sealed class NavigationService : INavigationService
{
    public event EventHandler<string>? Navigated;

    public void NavigateTo(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
            return;

        Navigated?.Invoke(this, key);
    }
}
