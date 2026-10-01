using System;
using System.Threading;

namespace Security.App.Services;

/// <summary>
/// Guarantees only one copy of SECURITY.EXE runs per user session.
///
/// The FIRST instance becomes primary and owns a named mutex for its whole
/// lifetime; it also listens on a named auto-reset event. A LATER instance
/// detects the mutex is taken, pokes the event (so the primary can restore
/// its window), and exits immediately — without touching the database,
/// camera, or host.
///
/// SECURITY NOTE: this coordinates the operator's own copies of the app; it
/// never launches processes, never elevates, and the names are user-session
/// scoped (no Global\ prefix), so it cannot be used to interfere with other
/// users' sessions.
/// </summary>
public sealed class SingleInstanceGuard : IDisposable
{
    private readonly Mutex _mutex;
    private readonly EventWaitHandle _activationEvent;
    private readonly RegisteredWaitHandle? _listener;
    private bool _disposed;

    /// <summary>True when this process owns the single-instance mutex.</summary>
    public bool IsPrimary { get; }

    /// <summary>
    /// Raised (on a thread-pool thread) when another instance asks this
    /// primary instance to come to the foreground. Never raised for the
    /// secondary instance.
    /// </summary>
    public event EventHandler? ActivationRequested;

    /// <param name="instanceKey">
    /// Namespacing key so tests (and future multi-edition builds) do not
    /// collide with each other. Defaults to the product name.
    /// </param>
    public SingleInstanceGuard(string instanceKey = "Security.Exe")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceKey);

        // Local\ (the default) scopes the objects to the current login
        // session — one operator, one copy, no cross-session interference.
        var mutexName = @"Local\" + instanceKey + ".SingleInstance";
        var eventName = @"Local\" + instanceKey + ".Activate";

        _mutex = new Mutex(initiallyOwned: true, name: mutexName, out var createdNew);
        IsPrimary = createdNew;

        _activationEvent = new EventWaitHandle(initialState: false, EventResetMode.AutoReset, eventName);

        if (IsPrimary)
        {
            // Thread-pool based wait: no dedicated thread, no UI dependency,
            // and it stops automatically when the handle is disposed.
            _listener = ThreadPool.RegisterWaitForSingleObject(
                _activationEvent,
                static (state, timedOut) => ((SingleInstanceGuard)state!).RaiseActivation(),
                this,
                Timeout.Infinite,
                executeOnlyOnce: false);
        }
    }

    /// <summary>
    /// Ask an already-running primary instance to show itself. Called by a
    /// secondary instance before it exits. Safe when no primary exists.
    /// </summary>
    public void SignalActivation()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _activationEvent.Set();
    }

    private void RaiseActivation()
    {
        if (_disposed)
            return;

        try
        {
            ActivationRequested?.Invoke(this, EventArgs.Empty);
        }
        catch
        {
            // A subscriber failing must never take down the wait loop.
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        _listener?.Unregister(null);

        _activationEvent.Dispose();

        if (IsPrimary)
        {
            try
            {
                _mutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
                // Not the owning thread anymore — disposal still closes it.
            }
        }

        _mutex.Dispose();
    }
}
