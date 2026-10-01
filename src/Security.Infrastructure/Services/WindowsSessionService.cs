using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using Security.Core.Enums;
using Security.Core.Interfaces;

namespace Security.Infrastructure.Services;

/// <summary>
/// Observes Windows session state using supported Windows APIs:
///
///  - transitions come from <see cref="SystemEvents.SessionSwitch"/>
///    (the documented broadcast mechanism — no hooks, no injection);
///  - the initial lock state comes from a WTS query
///    (<c>WTSQuerySessionInformation(WTSSessionInfoEx)</c>), because
///    SystemEvents only reports *changes* and says nothing about the state
///    the machine is already in when the app starts.
///
/// SECURITY BOUNDARY (Phase 3 spec):
///  - this class NEVER displays UI and never attempts to touch the
///    Secure Desktop, winlogon, credentials, or authentication;
///  - it only *reads* state Windows already publishes;
///  - lock state is never guessed: an unknown probe stays
///    <see cref="SessionState.Unknown"/> until a real broadcast arrives.
///
/// THREADING: SystemEvents raises SessionSwitch on a system broadcast
/// thread. This service translates that into its own event without
/// marshalling — subscribers are documented to marshal themselves.
/// </summary>
public sealed class WindowsSessionService : IWindowsSessionService
{
    private readonly ILogger<WindowsSessionService>? _logger;
    private readonly object _sync = new();

    private bool _started;
    private bool _disposed;
    private SessionState _currentState = SessionState.Unknown;
    private bool _initialized;

    public WindowsSessionService(ILogger<WindowsSessionService>? logger = null)
    {
        _logger = logger;
    }

    public event EventHandler<SessionStateChangedEventArgs>? SessionStateChanged;

    public SessionState CurrentState
    {
        get { lock (_sync) return _currentState; }
    }

    public bool IsInitialized
    {
        get { lock (_sync) return _initialized; }
    }

    public void Start()
    {
        lock (_sync)
        {
            if (_disposed || _started)
                return;
            _started = true;
        }

        // Probe FIRST so CurrentState is meaningful before any broadcast, then
        // subscribe: a lock that happens between the two is caught by the
        // subscription anyway, so no transition can be lost.
        RefreshLockState();

        try
        {
            SystemEvents.SessionSwitch += OnSessionSwitch;
        }
        catch (Exception ex)
        {
            // A failed subscription must not stop the application; the app
            // simply runs with an unknown session state until RefreshLockState
            // is called again.
            _logger?.LogWarning(ex, "Could not subscribe to Windows session switch broadcasts");
        }

        _logger?.LogInformation("Windows session monitoring started (state={State})", CurrentState.ToDisplayText());
    }

    public void Stop()
    {
        lock (_sync)
        {
            if (!_started)
                return;
            _started = false;
        }

        try
        {
            SystemEvents.SessionSwitch -= OnSessionSwitch;
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Unsubscribe from session switch broadcasts failed");
        }
    }

    /// <summary>
    /// Query the current lock state directly from WTS. This is the only way
    /// to learn the state at application start — SystemEvents emits nothing
    /// for a machine that was already locked before we launched.
    /// </summary>
    public SessionState RefreshLockState()
    {
        var state = QueryLockState();

        if (state is not SessionState.Unknown and not SessionState.Locked and not SessionState.Unlocked)
            state = SessionState.Unknown;

        ApplyState(state, raiseEvent: false);
        return state;
    }

    private void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        var mapped = e.Reason switch
        {
            SessionSwitchReason.SessionLock => SessionState.Locked,
            SessionSwitchReason.SessionUnlock => SessionState.Unlocked,
            SessionSwitchReason.SessionLogon => SessionState.LoggedOn,
            SessionSwitchReason.SessionLogoff => SessionState.LoggedOff,
            SessionSwitchReason.ConsoleConnect => SessionState.Connected,
            SessionSwitchReason.RemoteConnect => SessionState.Connected,
            SessionSwitchReason.ConsoleDisconnect => SessionState.Disconnected,
            SessionSwitchReason.RemoteDisconnect => SessionState.Disconnected,
            _ => SessionState.Unknown,
        };

        if (mapped == SessionState.Unknown)
            return; // SessionFlagsChange etc. carry no meaningful state for us.

        ApplyState(mapped, raiseEvent: true);
    }

    /// <summary>
    /// Publish a new state.
    ///
    /// Lock/unlock states become <see cref="CurrentState"/> — that is what
    /// camera gating reads. Logon/logoff/connect/disconnect are transient:
    /// they raise the event (so they can be logged as security events) but do
    /// not overwrite the lock state, which would otherwise make the camera
    /// policy flip on a RDP disconnect.
    /// </summary>
    private void ApplyState(SessionState state, bool raiseEvent)
    {
        SessionStateChangedEventArgs? args = null;

        lock (_sync)
        {
            if (_disposed)
                return;

            _initialized = true;

            var previous = _currentState;

            if (raiseEvent)
            {
                if (previous == state)
                    return;

                if (state is SessionState.Locked or SessionState.Unlocked)
                    _currentState = state;

                args = new SessionStateChangedEventArgs(previous, state);
            }
            else
            {
                // Silent probe: seed the state without notifying — nothing has
                // "changed" from the application's point of view yet.
                if (previous == state)
                    return;

                _currentState = state;
            }
        }

        if (args is not null)
        {
            try
            {
                SessionStateChanged?.Invoke(this, args);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "A SessionStateChanged subscriber threw");
            }
        }
    }

    /// <summary>Read the raw lock flag. Returns Unknown whenever Windows won't say.</summary>
    private SessionState QueryLockState()
    {
        try
        {
            var sessionId = WTSGetActiveConsoleSessionId();
            if (sessionId == 0xFFFFFFFF)
                return SessionState.Unknown;

            if (!WTSQuerySessionInformationW(
                    IntPtr.Zero, sessionId, WtsInfoClass.WTSSessionInfoEx,
                    out var buffer, out var bytesReturned) ||
                buffer == IntPtr.Zero || bytesReturned <= 0)
            {
                return SessionState.Unknown;
            }

            try
            {
                var info = Marshal.PtrToStructure<WtsInfoEx>(buffer);

                // Level 1 is the only level defined today; anything else means
                // we cannot interpret the buffer, so we do not try.
                if (info.Level != 1)
                    return SessionState.Unknown;

                return info.Data.Level1.SessionFlags switch
                {
                    // On Windows 8+ / Server 2012+ (and everything this app
                    // targets): 0 = locked, 1 = unlocked, -1 = unknown.
                    0 => SessionState.Locked,
                    1 => SessionState.Unlocked,
                    _ => SessionState.Unknown,
                };
            }
            finally
            {
                WTSFreeMemory(buffer);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "WTS lock-state probe failed; state stays unknown");
            return SessionState.Unknown;
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
                return;
            _disposed = true;
        }

        Stop();
        SessionStateChanged = null;
    }

    #region Native interop

    private enum WtsInfoClass
    {
        WTSSessionInfoEx = 25,
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct WtsInfoEx
    {
        [FieldOffset(0)]
        public uint Level;

        // The native union is 8-byte aligned (WTSINFOEX_LEVEL1 ends with
        // LARGE_INTEGER members), so C/C++ insert 4 bytes of padding after
        // Level and the level data starts at offset 8 — NOT 4. Reading at 4
        // silently maps SessionState (WTSActive=0) into SessionFlags and
        // reports every unlocked session as Locked, which would block the
        // camera auto-start forever. Verified against the live buffer:
        // Level@0=1, SessionId@8, SessionState@12, SessionFlags@16,
        // WinStationName@20 ("Console").
        [FieldOffset(8)]
        public WtsInfoExLevel Data;
    }

    /// <summary>C union — a single member at offset 0.</summary>
    [StructLayout(LayoutKind.Explicit)]
    private struct WtsInfoExLevel
    {
        [FieldOffset(0)]
        public WtsInfoExLevel1 Level1;
    }

    /// <summary>
    /// Leading members of WTSINFOEX_LEVEL1_W. Only the first three fields are
    /// declared: they are all the lock probe needs, and marshalling a prefix is
    /// safe because the CLR reads exactly the declared size from the buffer.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct WtsInfoExLevel1
    {
        public uint SessionId;
        public uint SessionState;
        public int SessionFlags;
    }

    [DllImport("kernel32.dll")]
    private static extern uint WTSGetActiveConsoleSessionId();

    [DllImport("wtsapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSQuerySessionInformationW(
        IntPtr hServer,
        uint sessionId,
        WtsInfoClass wtsInfoClass,
        out IntPtr ppBuffer,
        out uint pBytesReturned);

    [DllImport("wtsapi32.dll")]
    private static extern void WTSFreeMemory(IntPtr pMemory);

    #endregion
}
