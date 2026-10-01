using System;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.Logging;
using Security.App.Mvvm;
using Security.App.ViewModels;
using Security.Core.Entities;
using Security.Core.Enums;
using Security.Core.Paths;
using System.Windows.Input;

namespace Security.App.Views;

/// <summary>
/// Read-only dialog for a single security event: every persisted field plus
/// the optional snapshot. The snapshot is loaded defensively — a recorded
/// path whose file has since been cleaned up by retention shows an honest
/// message instead of a broken image.
/// </summary>
public partial class EventDetailsWindow : Window
{
    public EventDetailsWindow(SecurityEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);

        InitializeComponent();
        DataContext = new EventDetailsViewModel(evt, Close);
    }
}

/// <summary>
/// Projection of one <see cref="SecurityEvent"/> for the details dialog.
/// Owns the only tricky bit: turning a relative snapshot path into a decoded
/// image, or explaining why there is none.
/// </summary>
public sealed class EventDetailsViewModel : ViewModelBase
{
    private readonly ILogger<EventDetailsViewModel>? _logger;

    public EventDetailsViewModel(SecurityEvent evt, Action? onClose = null, ILogger<EventDetailsViewModel>? logger = null)
    {
        _logger = logger;

        EventType = evt.EventType.ToString();
        Result = evt.Result;
        Confidence = evt.Confidence;
        SessionState = evt.SessionState;
        Description = string.IsNullOrWhiteSpace(evt.Description) ? "—" : evt.Description;
        LocalTime = evt.Timestamp.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
        UtcTime = $"UTC {evt.Timestamp:yyyy-MM-dd HH:mm:ss}";

        CloseCommand = new RelayCommand(() => onClose?.Invoke());

        ResolveSnapshot(evt.SnapshotPath);
    }

    public string EventType { get; }

    public SecurityEventResult Result { get; }

    public double? Confidence { get; }

    public SessionState? SessionState { get; }

    public string Description { get; }

    public string LocalTime { get; }

    public string UtcTime { get; }

    public bool HasSnapshot { get; private set; }

    public string SnapshotPath { get; private set; } = string.Empty;

    public BitmapImage? SnapshotImage { get; private set; }

    public string SnapshotError { get; private set; } = string.Empty;

    public ICommand CloseCommand { get; }

    private void ResolveSnapshot(string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            // Honest empty state: no snapshot was ever stored for this event.
            HasSnapshot = false;
            OnPropertyChanged(nameof(HasSnapshot));
            return;
        }

        HasSnapshot = true;
        SnapshotPath = relativePath;
        OnPropertyChanged(nameof(HasSnapshot));
        OnPropertyChanged(nameof(SnapshotPath));

        try
        {
            // Snapshot paths are app-relative ("data/events/…"); never accept
            // an absolute or foreign path from the database.
            var full = AppPaths.ResolvePath(relativePath);

            if (!File.Exists(full))
            {
                SnapshotError = "The snapshot file is no longer on disk " +
                                "(it may have been removed by retention).";
                OnPropertyChanged(nameof(SnapshotError));
                return;
            }

            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.UriSource = new Uri(full, UriKind.Absolute);
            image.EndInit();
            image.Freeze();

            SnapshotImage = image;
            OnPropertyChanged(nameof(SnapshotImage));
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Could not load snapshot {Path}", relativePath);
            SnapshotError = "The snapshot file could not be displayed.";
            OnPropertyChanged(nameof(SnapshotError));
        }
    }
}
