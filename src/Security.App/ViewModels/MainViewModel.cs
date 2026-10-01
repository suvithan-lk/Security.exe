using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using Security.App.Mvvm;
using Security.App.Services;

namespace Security.App.ViewModels;

/// <summary>Left-rail navigation entry.</summary>
public sealed class NavItem
{
    public required string Key { get; init; }

    public required string Label { get; init; }

    /// <summary>Segoe MDL2 Assets glyph codepoint.</summary>
    public required string Icon { get; init; }

    public required ViewModelBase ViewModel { get; init; }
}

/// <summary>
/// Shell view model: owns the navigation rail and the single active view model.
/// Each screen instance is created once by DI and reused across navigations, so
/// camera preview, event filters, and scroll positions survive switching.
/// </summary>
public sealed class MainViewModel : ObservableObject
{
    private readonly INavigationService _navigation;
    private readonly CameraCoordinator _coordinator;
    private readonly SecurityMonitorService _monitor;
    private readonly IToastService _toasts;

    private NavItem? _selectedNav;
    private object _currentViewModel;
    private bool _suppressSelectionCallback;
    private bool _alertVisible;
    private bool _cameraActive;
    private string _alertMessage = string.Empty;
    private string _alertTime = string.Empty;

    public MainViewModel(
        DashboardViewModel dashboard,
        FaceProfileViewModel faceProfile,
        CameraViewModel camera,
        EventsViewModel events,
        SettingsViewModel settings,
        AboutViewModel about,
        INavigationService navigation,
        CameraCoordinator coordinator,
        SecurityMonitorService monitor,
        IToastService toasts)
    {
        _navigation = navigation;
        _coordinator = coordinator;
        _monitor = monitor;
        _toasts = toasts;

        NavItems = new ObservableCollection<NavItem>
        {
            new() { Key = "dashboard",  Label = "Dashboard",    Icon = "\uE72E", ViewModel = dashboard },
            new() { Key = "profile",    Label = "Face Profile", Icon = "\uE77B", ViewModel = faceProfile },
            new() { Key = "camera",     Label = "Camera",       Icon = "\uE722", ViewModel = camera },
            new() { Key = "events",     Label = "Events",       Icon = "\uE7BA", ViewModel = events },
            new() { Key = "settings",   Label = "Settings",     Icon = "\uE713", ViewModel = settings },
            new() { Key = "about",      Label = "About",        Icon = "\uE946", ViewModel = about },
        };

        _currentViewModel = dashboard;
        _selectedNav = NavItems[0];

        _navigation.Navigated += OnNavigated;

        // Alerts come from the monitor (cooldown- and session-gated), NOT raw
        // camera detections — the banner must not re-fire on every frame.
        _monitor.NotificationRequested += OnNotificationRequested;
        _coordinator.PropertyChanged += OnCoordinatorPropertyChanged;
        _cameraActive = _coordinator.IsRunning;

        DismissAlertCommand = new RelayCommand(DismissAlert);
    }

    #region In-app security alert

    /// <summary>
    /// In-app banner for an unknown face. Deliberately NOT a Windows Secure
    /// Desktop or lock-screen notification — Phase 3 never interrupts Windows.
    /// Shown only when the background monitor's notification cooldown allows it.
    /// </summary>
    public bool AlertVisible
    {
        get => _alertVisible;
        private set => SetProperty(ref _alertVisible, value);
    }

    public string AlertMessage
    {
        get => _alertMessage;
        private set => SetProperty(ref _alertMessage, value);
    }

    public string AlertTime
    {
        get => _alertTime;
        private set => SetProperty(ref _alertTime, value);
    }

    /// <summary>
    /// True only while the camera is actually capturing — drives the CAMERA
    /// ACTIVE badge in the top bar. False while stopped or session-paused.
    /// </summary>
    public bool CameraActive
    {
        get => _cameraActive;
        private set => SetProperty(ref _cameraActive, value);
    }

    public System.Windows.Input.ICommand DismissAlertCommand { get; }

    /// <summary>Application toast stack, rendered by the shell.</summary>
    public IToastService Toasts => _toasts;

    private void OnNotificationRequested(object? sender, SecurityAlertEventArgs e)
    {
        var alert = e.Result;

        System.Windows.Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
        {
            // Fixed, neutral wording — never "intruder", never a verdict.
            AlertMessage =
                $"Unknown person detected (similarity {alert.Similarity:F2}). " +
                "This notification is shown inside the app only.";
            AlertTime = DateTime.Now.ToString("HH:mm:ss");
            AlertVisible = true;
        }));
    }

    private void OnCoordinatorPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(CameraCoordinator.IsRunning))
            return;

        System.Windows.Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
            CameraActive = _coordinator.IsRunning));
    }

    private void DismissAlert() => AlertVisible = false;

    #endregion

    public ObservableCollection<NavItem> NavItems { get; }

    public string WindowTitle => "SECURITY.EXE — Security";

    public string VersionLabel => VersionInfo.Display;

    public object CurrentViewModel
    {
        get => _currentViewModel;
        private set => SetProperty(ref _currentViewModel, value);
    }

    public NavItem? SelectedNav
    {
        get => _selectedNav;
        set
        {
            if (value is null || ReferenceEquals(_selectedNav, value))
                return;

            _selectedNav = value;
            OnPropertyChanged();

            CurrentViewModel = value.ViewModel;

            // Programmatic updates already ran the hook; avoid doing it twice.
            if (!_suppressSelectionCallback)
                _ = NavigateAsync(value.ViewModel);
        }
    }

    private void OnNavigated(object? sender, string key)
    {
        // Raised from a command on any thread; the binding needs the UI thread.
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(new Action(() => NavigateTo(key)));
    }

    /// <summary>Jump to a screen programmatically (used by dashboard shortcuts).</summary>
    public void NavigateTo(string key)
    {
        foreach (var item in NavItems)
        {
            if (!string.Equals(item.Key, key, StringComparison.OrdinalIgnoreCase))
                continue;

            if (ReferenceEquals(_selectedNav, item))
                return;

            _suppressSelectionCallback = true;
            try
            {
                SelectedNav = item;
            }
            finally
            {
                _suppressSelectionCallback = false;
            }

            _ = NavigateAsync(item.ViewModel);
            return;
        }
    }

    /// <summary>
    /// Called once by the shell after the window is shown. The initial view is
    /// assigned in the constructor rather than via navigation, so without this
    /// the dashboard would sit on its defaults until the operator clicked away
    /// and back.
    /// </summary>
    public Task OnShellReadyAsync() => NavigateAsync(_currentViewModel as ViewModelBase ?? NavItems[0].ViewModel);

    private async Task NavigateAsync(ViewModelBase viewModel)
    {
        try
        {
            await viewModel.OnNavigatedAsync();
        }
        catch
        {
            // Each view model handles and reports its own failures; a throwing
            // navigation hook must never break the shell.
        }
    }
}
