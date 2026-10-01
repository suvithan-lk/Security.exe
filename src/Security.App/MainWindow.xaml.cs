using System.ComponentModel;
using System.Windows;

namespace Security.App;

/// <summary>
/// Interaction logic for MainWindow.xaml.
///
/// Phase 3: X honours the close-to-tray setting — the window hides and the
/// background monitor keeps running; the tray's Exit performs the real
/// shutdown. When close-to-tray is off (or Exit was requested) the window
/// closes normally.
/// </summary>
public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (Application.Current is App app && app.ShouldMinimizeToTray())
        {
            e.Cancel = true;
            Hide();
            return;
        }

        base.OnClosing(e);
    }
}
