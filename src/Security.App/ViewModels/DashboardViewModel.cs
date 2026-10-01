using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using Microsoft.Extensions.Logging;
using Security.App.Mvvm;
using Security.App.Services;
using Security.Core.Entities;
using Security.Core.Interfaces;

namespace Security.App.ViewModels;

/// <summary>
/// Landing screen: system status cards plus the most recent security events.
/// All values are projections of the shared <see cref="CameraCoordinator"/>
/// state and the event store — this view model owns no capture logic.
/// </summary>
public sealed class DashboardViewModel : ViewModelBase
{
    private readonly CameraCoordinator _coordinator;
    private readonly ISecurityEventService _events;
    private readonly INavigationService _navigation;
    private readonly ILogger<DashboardViewModel>? _logger;

    private string _lastDetection = "No detections yet";
    private string _lastDetectionTime = "—";
    private string _recentEventsHeader = "Recent events";

    public DashboardViewModel(
        CameraCoordinator coordinator,
        ISecurityEventService events,
        INavigationService navigation,
        ILogger<DashboardViewModel>? logger = null)
    {
        _coordinator = coordinator;
        _events = events;
        _navigation = navigation;
        _logger = logger;

        RecentEvents = new ObservableCollection<SecurityEvent>();

        RefreshCommand = new AsyncRelayCommand(RefreshAsync);
        GoToProfileCommand = new RelayCommand(() => _navigation.NavigateTo("profile"));
        GoToCameraCommand = new RelayCommand(() => _navigation.NavigateTo("camera"));
        GoToEventsCommand = new RelayCommand(() => _navigation.NavigateTo("events"));

        _coordinator.PropertyChanged += OnCoordinatorPropertyChanged;
        _events.EventRecorded += OnEventRecorded;
    }

    #region Bindings

    /// <summary>Overall tile — SYSTEM READY unless a core subsystem is down.</summary>
    public string SystemStatus
    {
        get
        {
            if (!_coordinator.EngineReady)
                return "SYSTEM ATTENTION";
            if (!_coordinator.ProfileExists)
                return "SETUP REQUIRED";
            return "SYSTEM READY";
        }
    }

    public string SystemStatusDetail => !_coordinator.EngineReady
        ? "Recognition models are not available."
        : !_coordinator.ProfileExists
            ? "No face profile enrolled yet."
            : "All subsystems reporting normally.";

    public string SystemStatusTone => SystemStatus switch
    {
        "SYSTEM READY" => "ok",
        "SETUP REQUIRED" => "warn",
        _ => "bad",
    };

    public string FaceProfileStatus => _coordinator.ProfileExists ? "Enrolled" : "Not enrolled";

    public string FaceProfileDetail => _coordinator.ProfileExists
        ? "Biometric template stored encrypted (DPAPI)."
        : "Run enrollment to enable recognition.";

    public string FaceProfileTone => _coordinator.ProfileExists ? "ok" : "warn";

    public string CameraStatus => _coordinator.IsRunning
        ? $"{_coordinator.CameraStatus}"
        : "Camera stopped";

    public string CameraDetail => _coordinator.SelectedCamera?.Name ?? "No camera selected";

    public string CameraTone => _coordinator.IsRunning ? "ok" : "idle";

    public string EngineStatus => _coordinator.EngineReady ? "Recognition engine ready" : "Recognition engine not ready";

    public string EngineDetail => _coordinator.EngineReady
        ? "SFace + YuNet models loaded locally."
        : "Models missing or failed to load.";

    public string EngineTone => _coordinator.EngineReady ? "ok" : "bad";

    public string LastDetection
    {
        get => _lastDetection;
        private set => SetProperty(ref _lastDetection, value);
    }

    public string LastDetectionTime
    {
        get => _lastDetectionTime;
        private set => SetProperty(ref _lastDetectionTime, value);
    }

    public string LastDetectionTone { get; private set; } = "idle";

    public string RecentEventsHeader
    {
        get => _recentEventsHeader;
        private set => SetProperty(ref _recentEventsHeader, value);
    }

    public ObservableCollection<SecurityEvent> RecentEvents { get; }

    public ICommand RefreshCommand { get; }

    public ICommand GoToProfileCommand { get; }

    public ICommand GoToCameraCommand { get; }

    public ICommand GoToEventsCommand { get; }

    #endregion

    public override async Task OnNavigatedAsync()
    {
        await RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        IsBusy = true;
        ClearError();

        try
        {
            await _coordinator.RefreshEngineStatusAsync();

            var recent = await _events.GetRecentAsync(8);
            RecentEvents.Clear();
            foreach (var evt in recent)
                RecentEvents.Add(evt);

            RecentEventsHeader = RecentEvents.Count == 0
                ? "No security events recorded yet."
                : "Recent events";

            RaiseAll();
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Dashboard refresh failed");
            ReportError("Could not refresh the dashboard. See logs for details.", ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void OnCoordinatorPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Only re-project when one of the card inputs changes.
        switch (e.PropertyName)
        {
            case nameof(CameraCoordinator.EngineReady):
            case nameof(CameraCoordinator.ProfileExists):
            case nameof(CameraCoordinator.IsRunning):
            case nameof(CameraCoordinator.CameraStatus):
            case nameof(CameraCoordinator.SelectedCamera):
            case nameof(CameraCoordinator.EngineStatus):
                System.Windows.Application.Current?.Dispatcher.BeginInvoke(
                    new Action(RaiseAll));
                break;
        }
    }

    private void OnEventRecorded(object? sender, SecurityEvent e)
    {
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
        {
            // Keep newest first and bounded.
            RecentEvents.Insert(0, e);
            while (RecentEvents.Count > 8)
                RecentEvents.RemoveAt(RecentEvents.Count - 1);

            if (RecentEvents.Count == 1)
                RecentEventsHeader = "Recent events";

            switch (e.EventType)
            {
                case Core.Enums.SecurityEventType.KnownFaceDetected:
                    SetDetection("KNOWN face detected", e);
                    break;
                case Core.Enums.SecurityEventType.UnknownFaceDetected:
                    SetDetection("UNKNOWN face detected", e);
                    break;
                case Core.Enums.SecurityEventType.FaceDetected:
                    SetDetection(e.Description, e);
                    break;
                case Core.Enums.SecurityEventType.RecognitionFailed:
                    SetDetection("Unable to determine", e);
                    break;
            }
        }));
    }

    private void SetDetection(string text, SecurityEvent e)
    {
        LastDetection = text;
        LastDetectionTime = e.Timestamp.ToLocalTime().ToString("HH:mm:ss");
        LastDetectionTone = e.EventType switch
        {
            Core.Enums.SecurityEventType.KnownFaceDetected => "ok",
            Core.Enums.SecurityEventType.UnknownFaceDetected => "bad",
            _ => "warn",
        };
        OnPropertyChanged(nameof(LastDetectionTone));
    }

    private void RaiseAll()
    {
        OnPropertyChanged(nameof(SystemStatus));
        OnPropertyChanged(nameof(SystemStatusDetail));
        OnPropertyChanged(nameof(SystemStatusTone));
        OnPropertyChanged(nameof(FaceProfileStatus));
        OnPropertyChanged(nameof(FaceProfileDetail));
        OnPropertyChanged(nameof(FaceProfileTone));
        OnPropertyChanged(nameof(CameraStatus));
        OnPropertyChanged(nameof(CameraDetail));
        OnPropertyChanged(nameof(CameraTone));
        OnPropertyChanged(nameof(EngineStatus));
        OnPropertyChanged(nameof(EngineDetail));
        OnPropertyChanged(nameof(EngineTone));
    }
}
