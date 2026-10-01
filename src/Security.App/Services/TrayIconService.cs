using System;
using System.Drawing;
using System.Windows.Forms;
using Security.Core.Interfaces;

namespace Security.App.Services;

/// <summary>
/// What the tray asked the application to do. Kept as plain events so the
/// service itself contains no navigation or shutdown logic (and stays
/// testable without a message loop).
/// </summary>
public sealed class TrayCommands
{
    public event EventHandler? OpenWindowRequested;

    public event EventHandler? OpenEventsRequested;

    public event EventHandler? OpenSettingsRequested;

    public event EventHandler? ExitRequested;

    /// <summary>Raised with true = pause monitoring, false = resume.</summary>
    public event EventHandler<bool>? PauseToggleRequested;

    internal void RaiseOpenWindow() => OpenWindowRequested?.Invoke(this, EventArgs.Empty);

    internal void RaiseOpenEvents() => OpenEventsRequested?.Invoke(this, EventArgs.Empty);

    internal void RaiseOpenSettings() => OpenSettingsRequested?.Invoke(this, EventArgs.Empty);

    internal void RaiseExit() => ExitRequested?.Invoke(this, EventArgs.Empty);

    internal void RaisePauseToggle(bool paused) => PauseToggleRequested?.Invoke(this, paused);
}

/// <summary>
/// Notification-area icon (WinForms <see cref="NotifyIcon"/>) for the
/// background monitor: status line, Open / Pause / Events / Settings / Exit.
///
/// HONESTY CONTRACT: the status line shows exactly what the monitor reports
/// (MONITORING ACTIVE / PAUSED / OFF) — never "Fully Protected", never a
/// made-up score. The icon simply disappears when the app is exiting (no
/// ghost icon left in the tray).
///
/// THREADING: menu clicks arrive on the WinForms/UI thread (same thread as
/// WPF here); <see cref="UpdateStatus"/> may be called from any thread —
/// NotifyIcon property sets are marshalled internally by the shell timer,
/// and we keep the surface minimal to avoid cross-thread surprises.
/// </summary>
public sealed class TrayIconService : IDisposable
{
    private readonly TrayCommands _commands = new();
    private readonly NotifyIcon _icon;
    private readonly ToolStripMenuItem _statusItem;
    private readonly ToolStripMenuItem _pauseItem;
    private bool _paused;
    private bool _disposed;

    public TrayIconService(string iconPath)
    {
        _statusItem = new ToolStripMenuItem("MONITORING OFF")
        {
            Enabled = false,
        };

        var openItem = new ToolStripMenuItem("Open Security");
        openItem.Click += (_, _) => _commands.RaiseOpenWindow();

        _pauseItem = new ToolStripMenuItem("Pause Monitoring");
        _pauseItem.Click += (_, _) => _commands.RaisePauseToggle(!_paused);

        var eventsItem = new ToolStripMenuItem("Open Events");
        eventsItem.Click += (_, _) => _commands.RaiseOpenEvents();

        var settingsItem = new ToolStripMenuItem("Settings");
        settingsItem.Click += (_, _) => _commands.RaiseOpenSettings();

        var exitItem = new ToolStripMenuItem("Exit");
        exitItem.Click += (_, _) => _commands.RaiseExit();

        var menu = new ContextMenuStrip();
        menu.Items.Add(_statusItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(openItem);
        menu.Items.Add(_pauseItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(eventsItem);
        menu.Items.Add(settingsItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(exitItem);

        _icon = new NotifyIcon
        {
            ContextMenuStrip = menu,
            Text = "Security",
            Visible = true,
        };

        try
        {
            if (System.IO.File.Exists(iconPath))
                _icon.Icon = new Icon(iconPath);
            else
                _icon.Icon = SystemIcons.Application;
        }
        catch (Exception)
        {
            // A bad icon must never stop the app from showing a tray menu.
            _icon.Icon = SystemIcons.Application;
        }

        // Double-click = open the window, the platform convention.
        _icon.DoubleClick += (_, _) => _commands.RaiseOpenWindow();

        // Clicking the balloon opens the window too — the notification is
        // only useful if the operator can act on it.
        _icon.BalloonTipClicked += (_, _) => _commands.RaiseOpenWindow();
    }

    /// <summary>Application-level commands raised by the tray menu.</summary>
    public TrayCommands Commands => _commands;

    /// <summary>
    /// Publish the monitor's status line and pause label. The text is passed
    /// in verbatim by the caller from <c>MonitoringStatusText</c> — the tray
    /// never invents or embellishes state.
    /// </summary>
    public void UpdateStatus(string statusText, bool isPaused)
    {
        if (_disposed)
            return;

        _statusItem.Text = statusText;

        // NotifyIcon caps its tooltip at 127 characters.
        _icon.Text = Truncate(statusText, 127);

        if (_paused != isPaused)
        {
            _paused = isPaused;
            _pauseItem.Text = isPaused ? "Resume Monitoring" : "Pause Monitoring";
        }
    }

    /// <summary>Balloon tip for an unknown-face notification (neutral wording).</summary>
    public void ShowNotification(string title, string message)
    {
        if (_disposed)
            return;

        _icon.ShowBalloonTip(3000, title, message, ToolTipIcon.Warning);
    }

    /// <summary>Remove the icon immediately (used before the app exits).</summary>
    public void Hide()
    {
        if (!_disposed)
            _icon.Visible = false;
    }

    private static string Truncate(string value, int maxLength)
        => value.Length <= maxLength ? value : value[..(maxLength - 1)] + "…";

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _icon.Visible = false;
        _icon.Dispose();
        _statusItem.Dispose();
        _pauseItem.Dispose();
    }
}
