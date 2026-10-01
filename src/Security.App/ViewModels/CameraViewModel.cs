using System;
using System.ComponentModel;
using System.Threading.Tasks;
using System.Windows.Input;
using Microsoft.Extensions.Logging;
using Security.App.Mvvm;
using Security.App.Services;
using Security.Core.Interfaces;
using Security.Core.Models;

namespace Security.App.ViewModels;

/// <summary>
/// Live camera screen: device selection, start/stop, preview, detection
/// overlay, and the current recognition verdict. All capture work is delegated
/// to the shared <see cref="CameraCoordinator"/>.
/// </summary>
public sealed class CameraViewModel : ViewModelBase
{
    private readonly CameraCoordinator _coordinator;
    private readonly ISettingsService _settings;
    private readonly IToastService _toasts;
    private readonly ILogger<CameraViewModel>? _logger;

    public CameraViewModel(
        CameraCoordinator coordinator,
        ISettingsService settings,
        IToastService toasts,
        ILogger<CameraViewModel>? logger = null)
    {
        _coordinator = coordinator;
        _settings = settings;
        _toasts = toasts;
        _logger = logger;

        StartCommand = new AsyncRelayCommand(StartAsync, () => !_coordinator.IsRunning && !_coordinator.IsConnecting);
        StopCommand = new AsyncRelayCommand(StopAsync, () => _coordinator.IsRunning);
        RestartCommand = new AsyncRelayCommand(RestartAsync, () => _coordinator.Cameras.Count > 0);
        RefreshCommand = new AsyncRelayCommand(RefreshAsync);
        SwitchCommand = new AsyncRelayCommand(
            SwitchAsync,
            () => _coordinator.SelectedCamera is not null);

        _coordinator.PropertyChanged += OnCoordinatorPropertyChanged;
    }

    #region Bindings

    public System.Collections.ObjectModel.ObservableCollection<CameraDevice> Cameras => _coordinator.Cameras;

    public CameraDevice? SelectedCamera
    {
        get => _coordinator.SelectedCamera;
        set
        {
            if (ReferenceEquals(_coordinator.SelectedCamera, value))
                return;

            _coordinator.SelectedCamera = value;
            OnPropertyChanged();
            RaiseCommandStates();
            _ = SwitchIfRunningAsync();
        }
    }

    public bool IsRunning => _coordinator.IsRunning;

    // ---- Camera state: drives the status badge and the preview overlays ----
    public CameraState State => _coordinator.State;

    public bool IsLive => _coordinator.IsLive;

    public bool IsConnecting => _coordinator.IsConnecting;

    /// <summary>Camera stopped with no failure: show CAMERA OFFLINE + Start.</summary>
    public bool IsOffline => _coordinator.IsOffline;

    /// <summary>Camera failed: show CAMERA UNAVAILABLE + causes + Retry.</summary>
    public bool IsError => _coordinator.IsError;

    public string StatusBadgeText => _coordinator.StatusBadgeText;

    public System.Collections.Generic.IReadOnlyList<string> FailureReasons => _coordinator.FailureReasons;

    public bool HasFailureReasons => _coordinator.HasFailureReasons;

    /// <summary>Empty device list — drives the "connect a camera" hint.</summary>
    public bool NoCameras => Cameras.Count == 0;

    // ---- Status block (Status / Resolution / FPS / Face detection) ----
    public string ResolutionText => _coordinator.ResolutionText;

    public string FpsText => _coordinator.FpsText;

    /// <summary>Live face-detection state, phrased for the status block.</summary>
    public string DetectionText
    {
        get
        {
            if (!IsRunning)
                return "Not running";

            return FaceCount switch
            {
                0 => "No face detected",
                1 => "Face detected",
                _ => "Multiple faces detected",
            };
        }
    }

    public string CameraStatus => _coordinator.CameraStatus;

    public string StatusText => _coordinator.StatusText;

    public string QualityMessage => _coordinator.QualityMessage;

    public System.Windows.Media.Imaging.WriteableBitmap? Preview => _coordinator.Preview;

    public int PreviewPixelWidth => _coordinator.PreviewPixelWidth;

    public int PreviewPixelHeight => _coordinator.PreviewPixelHeight;

    public System.Collections.Generic.IReadOnlyList<OverlayBox> Boxes => _coordinator.Boxes;

    public bool HasPreview => _coordinator.Preview is not null;

    /// <summary>
    /// Preview surface size. Falls back to 16:9 before the first frame so the
    /// placeholder renders with the right aspect ratio.
    /// </summary>
    public double SurfaceWidth => HasPreview ? PreviewPixelWidth : 1280;

    public double SurfaceHeight => HasPreview ? PreviewPixelHeight : 720;

    public int FaceCount => Boxes.Count;

    public FaceRecognitionResult? Recognition => _coordinator.LastRecognition;

    /// <summary>Spec's three recognition statuses: KNOWN / UNKNOWN / UNCERTAIN.</summary>
    public string VerdictText => Recognition switch
    {
        null => "—",
        { Status: Core.Enums.RecognitionStatus.Known } => "KNOWN",
        { Status: Core.Enums.RecognitionStatus.Unknown } => "UNKNOWN",
        _ => "UNCERTAIN",
    };

    /// <summary>
    /// Confidence as a percentage, which is the figure an operator can act on.
    /// Reported only for a resolved verdict: for an indeterminate result there
    /// is no score to show and "0%" would read as a confident rejection.
    /// </summary>
    public string ConfidenceText
    {
        get
        {
            if (Recognition is null)
                return "—";

            if (Recognition.Status == Core.Enums.RecognitionStatus.UnableToDetermine)
                return "N/A";

            return Recognition.Confidence.ToString("P0");
        }
    }

    public string VerdictDetail
    {
        get
        {
            if (Recognition is null)
                return "No recognition verdict yet.";

            var stamp = Recognition.Timestamp.ToLocalTime().ToString("HH:mm:ss");

            if (Recognition.Status == Core.Enums.RecognitionStatus.UnableToDetermine)
            {
                var reason = string.IsNullOrWhiteSpace(Recognition.Reason)
                    ? "Could not determine a verdict."
                    : Recognition.Reason;
                return $"{reason} · {stamp}";
            }

            var line = $"Confidence {Recognition.Confidence:P0} · similarity {Recognition.Similarity:F3}" +
                       $" · threshold {_settings.Recognition.Threshold:F2} · {stamp}";

            return string.IsNullOrWhiteSpace(Recognition.Reason) ? line : $"{Recognition.Reason} — {line}";
        }
    }

    public string EngineStatus => _coordinator.EngineStatus;

    public string EngineNotice => _coordinator.EngineReady
        ? string.Empty
        : "Recognition engine not ready — models could not be loaded. Detection overlay still works.";

    public AsyncRelayCommand StartCommand { get; }

    public AsyncRelayCommand StopCommand { get; }

    /// <summary>Stop then start the current device — the recovery path for a wedged camera.</summary>
    public AsyncRelayCommand RestartCommand { get; }

    public AsyncRelayCommand RefreshCommand { get; }

    public AsyncRelayCommand SwitchCommand { get; }

    #endregion

    public override Task OnNavigatedAsync()
    {
        RaiseAll();
        return _coordinator.Cameras.Count > 0 ? Task.CompletedTask : RefreshAsync();
    }

    private async Task StartAsync()
    {
        ClearError();
        await _coordinator.StartAsync();

        // Camera start failures are rendered by the CAMERA UNAVAILABLE panel in
        // the preview (reason + causes + Retry). ReportError would repeat them
        // in a second banner, so it stays reserved for everything else — the
        // toast is a transient nudge in case the operator is on another page.
        if (_coordinator.State == CameraState.Error)
            _toasts.Error("Camera unavailable", _coordinator.StatusText);

        RaiseAll();
    }

    private async Task StopAsync()
    {
        ClearError();
        await _coordinator.StopAsync();
        RaiseAll();
    }

    private async Task RestartAsync()
    {
        ClearError();
        try
        {
            await _coordinator.RestartAsync();

            if (_coordinator.State == CameraState.Live)
                _toasts.Success("Camera restarted", $"Capturing from {_coordinator.SelectedCamera?.ToString() ?? "the default device"}.");
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Camera restart failed");

            if (_coordinator.State != CameraState.Error)
                ReportError("Could not restart the camera.", ex);
        }

        if (_coordinator.State == CameraState.Error)
            _toasts.Error("Camera unavailable", _coordinator.StatusText);

        RaiseAll();
    }

    private async Task RefreshAsync()
    {
        ClearError();
        try
        {
            await _coordinator.RefreshCamerasAsync();
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Camera refresh failed");

            // The coordinator already renders enumeration failures as an error
            // state with causes; only surface unexpected ones a second time.
            if (_coordinator.State != CameraState.Error)
                ReportError("Could not list cameras.", ex);
        }

        RaiseAll();
    }

    private async Task SwitchAsync()
    {
        if (_coordinator.SelectedCamera is null)
            return;

        ClearError();
        await _coordinator.SwitchAsync(_coordinator.SelectedCamera);

        // Switching goes through the normal start path, so a device that cannot
        // be reopened already surfaces as CAMERA UNAVAILABLE in the preview.
        RaiseAll();
    }

    private async Task SwitchIfRunningAsync()
    {
        if (!_coordinator.IsRunning || _coordinator.SelectedCamera is null)
            return;

        try
        {
            await _coordinator.SwitchAsync(_coordinator.SelectedCamera);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Camera switch failed");
            ReportError("Could not switch cameras.", ex);
        }

        RaiseAll();
    }

    private void OnCoordinatorPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null)
            return;

        dispatcher.BeginInvoke(new Action(RaiseAll));
    }

    private void RaiseCommandStates()
    {
        StartCommand.RaiseCanExecuteChanged();
        StopCommand.RaiseCanExecuteChanged();
        RestartCommand.RaiseCanExecuteChanged();
        SwitchCommand.RaiseCanExecuteChanged();
    }

    private void RaiseAll()
    {
        OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(State));
        OnPropertyChanged(nameof(IsLive));
        OnPropertyChanged(nameof(IsConnecting));
        OnPropertyChanged(nameof(IsOffline));
        OnPropertyChanged(nameof(IsError));
        OnPropertyChanged(nameof(StatusBadgeText));
        OnPropertyChanged(nameof(FailureReasons));
        OnPropertyChanged(nameof(HasFailureReasons));
        OnPropertyChanged(nameof(NoCameras));
        OnPropertyChanged(nameof(ResolutionText));
        OnPropertyChanged(nameof(FpsText));
        OnPropertyChanged(nameof(DetectionText));
        OnPropertyChanged(nameof(CameraStatus));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(QualityMessage));
        OnPropertyChanged(nameof(Preview));
        OnPropertyChanged(nameof(PreviewPixelWidth));
        OnPropertyChanged(nameof(PreviewPixelHeight));
        OnPropertyChanged(nameof(HasPreview));
        OnPropertyChanged(nameof(SurfaceWidth));
        OnPropertyChanged(nameof(SurfaceHeight));
        OnPropertyChanged(nameof(Boxes));
        OnPropertyChanged(nameof(FaceCount));
        OnPropertyChanged(nameof(Recognition));
        OnPropertyChanged(nameof(VerdictText));
        OnPropertyChanged(nameof(ConfidenceText));
        OnPropertyChanged(nameof(VerdictDetail));
        OnPropertyChanged(nameof(EngineStatus));
        OnPropertyChanged(nameof(EngineNotice));
        RaiseCommandStates();
    }
}
