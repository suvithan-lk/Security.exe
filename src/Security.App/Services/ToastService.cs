using System;
using System.Collections.ObjectModel;
using System.Windows.Threading;
using Security.App.Mvvm;

namespace Security.App.Services;

public enum ToastLevel
{
    Info,
    Success,
    Warning,
    Error,
}

/// <summary>
/// One transient, non-blocking notification.
///
/// Carries its own colours rather than a level enum the view would have to
/// convert: the accent and tint are fixed per level, and pushing them here
/// keeps the XAML free of a converter and of duplicated colour literals.
/// </summary>
public sealed class ToastNotification
{
    internal ToastNotification(ToastLevel level, string title, string message, DateTime expiresAtUtc)
    {
        Level = level;
        Title = title;
        Message = message;
        ExpiresAtUtc = expiresAtUtc;

        (Accent, Tint) = level switch
        {
            ToastLevel.Success => (
                (System.Windows.Media.Brush)System.Windows.Media.Brushes.MediumSeaGreen,
                Box("#0B2B22")),
            ToastLevel.Warning => (
                (System.Windows.Media.Brush)System.Windows.Media.Brushes.Gold,
                Box("#3B2E0A")),
            ToastLevel.Error => (
                (System.Windows.Media.Brush)System.Windows.Media.Brushes.Salmon,
                Box("#3A1216")),
            _ => (
                (System.Windows.Media.Brush)System.Windows.Media.Brushes.SkyBlue,
                Box("#10202E")),
        };
    }

    public ToastLevel Level { get; }

    public string Title { get; }

    public string Message { get; }

    public string Time { get; } = DateTime.Now.ToString("HH:mm:ss");

    /// <summary>Accent colour for the title, icon and border.</summary>
    public System.Windows.Media.Brush Accent { get; }

    /// <summary>Panel tint. Never opaque — the page behind must stay readable.</summary>
    public System.Windows.Media.Brush Tint { get; }

    /// <summary>Manual close. Assigned by the service when the toast is created.</summary>
    public System.Windows.Input.ICommand? DismissCommand { get; set; }

    internal DateTime ExpiresAtUtc { get; }

    private static System.Windows.Media.Brush Box(string hex)
        => new System.Windows.Media.SolidColorBrush(
            (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex)!);
}

/// <summary>
/// Application-level toast notifications: auto-dismissing, manually closable,
/// and non-blocking.
///
/// Toasts are rendered inside the main window only. Nothing here ever creates
/// a window, a tray balloon, or a Windows notification — so no toast can reach
/// the Secure Desktop, the lock screen, or the Action Center.
/// </summary>
public interface IToastService
{
    /// <summary>Bound directly by the toast host in the shell.</summary>
    ObservableCollection<ToastNotification> Items { get; }

    void Show(ToastLevel level, string title, string message);

    void Info(string title, string message);

    void Success(string title, string message);

    void Warning(string title, string message);

    void Error(string title, string message);

    void Dismiss(ToastNotification toast);
}

public sealed class ToastService : IToastService, IDisposable
{
    /// <summary>How long an ordinary toast stays up.</summary>
    private static readonly TimeSpan DefaultDuration = TimeSpan.FromSeconds(6);

    /// <summary>Errors linger — they are the ones worth reading.</summary>
    private static readonly TimeSpan ErrorDuration = TimeSpan.FromSeconds(12);

    /// <summary>
    /// Oldest toasts are evicted past this count so a storm of events can
    /// never bury the page the operator is trying to read.
    /// </summary>
    private const int MaxVisible = 4;

    private readonly DispatcherTimer _timer;
    private bool _disposed;

    public ToastService()
    {
        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(500),
        };
        _timer.Tick += OnTick;
        _timer.Start();
    }

    public ObservableCollection<ToastNotification> Items { get; } = new();

    public void Info(string title, string message) => Show(ToastLevel.Info, title, message);

    public void Success(string title, string message) => Show(ToastLevel.Success, title, message);

    public void Warning(string title, string message) => Show(ToastLevel.Warning, title, message);

    public void Error(string title, string message) => Show(ToastLevel.Error, title, message);

    public void Show(ToastLevel level, string title, string message)
    {
        if (string.IsNullOrWhiteSpace(title) && string.IsNullOrWhiteSpace(message))
            return;

        var duration = level == ToastLevel.Error ? ErrorDuration : DefaultDuration;
        var toast = new ToastNotification(level, title.Trim(), message?.Trim() ?? string.Empty, DateTime.UtcNow + duration);

        // Assigned after construction so the command can capture an already
        // definitely-assigned local.
        toast.DismissCommand = new Mvvm.RelayCommand(() => Dismiss(toast));

        RunOnUi(() =>
        {
            Items.Add(toast);

            // The host renders newest-first, so capping means dropping the
            // oldest entry from the far end of the stack.
            while (Items.Count > MaxVisible)
                Items.RemoveAt(0);
        });
    }

    public void Dismiss(ToastNotification toast)
    {
        if (toast is null)
            return;

        RunOnUi(() => Items.Remove(toast));
    }

    private void OnTick(object? sender, EventArgs e)
    {
        if (_disposed || Items.Count == 0)
            return;

        var now = DateTime.UtcNow;

        for (var i = Items.Count - 1; i >= 0; i--)
        {
            if (Items[i].ExpiresAtUtc <= now)
                Items.RemoveAt(i);
        }
    }

    /// <summary>
    /// The timer may be started before a message loop exists, and toasts can be
    /// raised from camera or worker threads — marshal either way.
    /// </summary>
    private static void RunOnUi(Action action)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;

        if (dispatcher is null || dispatcher.CheckAccess())
        {
            action();
            return;
        }

        dispatcher.BeginInvoke(DispatcherPriority.Normal, action);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _timer.Stop();
        _timer.Tick -= OnTick;
    }
}
