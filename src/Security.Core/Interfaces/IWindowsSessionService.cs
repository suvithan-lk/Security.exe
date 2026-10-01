using Security.Core.Enums;

namespace Security.Core.Interfaces;

/// <summary>
/// Observes Windows session state (lock/unlock/logon/logoff/connect/disconnect)
/// using supported Windows APIs only.
///
/// GUARANTEES:
///  - no process injection, no winlogon hooking, no credential access;
///  - never displays UI on the Secure Desktop / lock screen;
///  - <see cref="SessionStateChanged"/> may be raised on a system broadcast
///    thread — subscribers must marshal to their own context;
///  - <see cref="CurrentState"/> reflects the last known state; it starts as
///    <see cref="SessionState.Unknown"/> until <see cref="Start"/> probes the
///    real lock state, and Unknown is never silently upgraded to Unlocked.
/// </summary>
public interface IWindowsSessionService : IDisposable
{
    /// <summary>Last observed session state. <see cref="SessionState.Unknown"/> before the first probe.</summary>
    SessionState CurrentState { get; }

    /// <summary>True once the initial lock state has been probed at least once.</summary>
    bool IsInitialized { get; }

    /// <summary>
    /// Raised (possibly on a system broadcast thread) whenever the session
    /// state changes. Carries both the previous and the new state.
    /// </summary>
    event EventHandler<SessionStateChangedEventArgs>? SessionStateChanged;

    /// <summary>
    /// Begin listening and probe the current lock state immediately so
    /// <see cref="CurrentState"/> is meaningful before the first event.
    /// Safe to call more than once.
    /// </summary>
    void Start();

    /// <summary>Stop listening. Safe to call when never started.</summary>
    void Stop();

    /// <summary>
    /// Re-probe the lock state on demand (used by tests and after a missed
    /// broadcast). Returns the state that was found.
    /// </summary>
    SessionState RefreshLockState();
}

public sealed class SessionStateChangedEventArgs : EventArgs
{
    public SessionStateChangedEventArgs(SessionState previous, SessionState current)
    {
        Previous = previous;
        Current = current;
    }

    public SessionState Previous { get; }

    public SessionState Current { get; }

    public DateTime TimestampUtc { get; } = DateTime.UtcNow;
}
