using System;
using System.Windows;
using Security.App.Mvvm;

namespace Security.App.Views;

/// <summary>
/// Phase 3 unknown-face alert window (spec §10-§12). Owned and shown by
/// <see cref="App"/> only when the monitor's notification cooldown allows a
/// notification in the normal desktop session. One instance at a time.
/// </summary>
public partial class SecurityAlertWindow : Window
{
    /// <summary>Raised when the operator asks to jump to the Events screen.</summary>
    public event EventHandler? OpenEventsRequested;

    public SecurityAlertWindow()
    {
        InitializeComponent();

        var viewModel = new SecurityAlertViewModel(
            onClose: Close,
            onOpenEvents: () =>
            {
                OpenEventsRequested?.Invoke(this, EventArgs.Empty);
                Close();
            });

        DataContext = viewModel;

        Loaded += (_, _) => PositionNearTray();
    }

    /// <summary>Anchor bottom-right, just above the taskbar / tray balloon area.</summary>
    private void PositionNearTray()
    {
        var area = SystemParameters.WorkArea;
        Left = area.Right - Width - 16;
        Top = area.Bottom - Height - 16;
    }
}

/// <summary>Display state for <see cref="SecurityAlertWindow"/>.</summary>
public sealed class SecurityAlertViewModel
{
    public SecurityAlertViewModel(Action onClose, Action onOpenEvents)
    {
        DismissCommand = new RelayCommand(onClose);
        OpenEventsCommand = new RelayCommand(onOpenEvents);
        Time = DateTime.Now.ToString("HH:mm:ss");
    }

    /// <summary>Fixed, neutral wording — never "intruder", never a verdict.</summary>
    public string Message { get; init; } =
        "A face that does not match your enrolled profile was detected. " +
        "This alert is shown on the desktop only — never on the lock screen.";

    public string Time { get; }

    public RelayCommand DismissCommand { get; }

    public RelayCommand OpenEventsCommand { get; }
}
