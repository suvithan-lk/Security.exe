namespace Security.Core.Enums;

/// <summary>
/// Windows session states the application can observe.
///
/// This is session *state monitoring* only — it reads what Windows already
/// publishes (SystemEvents / WTS). It never injects into winlogon, never
/// touches authentication, and never attempts to influence the lock screen.
/// </summary>
public enum SessionState
{
    /// <summary>Lock state could not be determined (never guessed as locked).</summary>
    Unknown,

    /// <summary>The workstation is locked (Secure Desktop / lock screen active).</summary>
    Locked,

    /// <summary>The workstation is unlocked and usable.</summary>
    Unlocked,

    /// <summary>A user logged on to the session.</summary>
    LoggedOn,

    /// <summary>A user logged off from the session.</summary>
    LoggedOff,

    /// <summary>The session connected (console attach or RDP connect).</summary>
    Connected,

    /// <summary>The session disconnected (console detach or RDP disconnect).</summary>
    Disconnected,
}

/// <summary>Helpers for <see cref="SessionState"/> used across the pipeline.</summary>
public static class SessionStateExtensions
{
    /// <summary>
    /// True only when Windows positively reports an unlocked session.
    /// <see cref="SessionState.Unknown"/> is deliberately NOT treated as
    /// unlocked: the camera must never resume on a guess.
    /// </summary>
    public static bool AllowsCameraMonitoring(this SessionState state)
        => state is SessionState.Unlocked or SessionState.LoggedOn or SessionState.Connected;

    /// <summary>Short caption for badges and the tray tooltip.</summary>
    public static string ToDisplayText(this SessionState state) => state switch
    {
        SessionState.Locked => "LOCKED",
        SessionState.Unlocked => "UNLOCKED",
        SessionState.LoggedOn => "LOGGED ON",
        SessionState.LoggedOff => "LOGGED OFF",
        SessionState.Connected => "CONNECTED",
        SessionState.Disconnected => "DISCONNECTED",
        _ => "UNKNOWN",
    };
}
