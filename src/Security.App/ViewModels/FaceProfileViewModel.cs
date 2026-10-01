using System;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using Microsoft.Extensions.Logging;
using Security.App.Mvvm;
using Security.App.Services;
using Security.Core.Interfaces;
using Security.Core.Models;

namespace Security.App.ViewModels;

/// <summary>
/// Five-step face enrollment wizard (Welcome → Position → Capture →
/// Processing → Complete) with a Validation failed branch.
///
/// All capture work stays in the shared <see cref="CameraCoordinator"/>; this
/// class only owns which step is showing and how coordinator state maps onto
/// it. Nothing here blocks: every command is async and all quality guidance
/// arrives through coordinator notifications raised off the camera thread.
/// </summary>
public sealed class FaceProfileViewModel : ViewModelBase
{
    private readonly CameraCoordinator _coordinator;
    private readonly ISettingsService _settings;
    private readonly IUserProfileRepository _profiles;
    private readonly IFaceEmbeddingRepository _embeddings;
    private readonly ISecurityEventService _events;
    private readonly IUserDialogService _dialogs;
    private readonly IToastService _toasts;
    private readonly ILogger<FaceProfileViewModel>? _logger;

    private EnrollmentWizardStep _step = EnrollmentWizardStep.Welcome;
    private string _instruction = "Start enrollment when you are ready.";
    private string _detail = string.Empty;
    private double _percent;
    private string _capturedLabel = "0 / 0";
    private string _headline = "No face profile";
    private string _failureMessage = string.Empty;
    private string _completedProfileName = string.Empty;
    private string _completedDateText = string.Empty;
    private string _completedSamplesText = string.Empty;
    private string _completedModelVersion = string.Empty;

    /// <summary>
    /// True only when this wizard opened the camera itself, so leaving the
    /// wizard never closes a session the operator had already running.
    /// </summary>
    private bool _startedCamera;

    /// <summary>
    /// Ensures the completion/failure toast fires exactly once per run even if
    /// both the dispatcher sync and a navigation hook observe the same result.
    /// </summary>
    private bool _outcomeAnnounced;

    public FaceProfileViewModel(
        CameraCoordinator coordinator,
        ISettingsService settings,
        IUserProfileRepository profiles,
        IFaceEmbeddingRepository embeddings,
        ISecurityEventService events,
        IUserDialogService dialogs,
        IToastService toasts,
        ILogger<FaceProfileViewModel>? logger = null)
    {
        _coordinator = coordinator;
        _settings = settings;
        _profiles = profiles;
        _embeddings = embeddings;
        _events = events;
        _dialogs = dialogs;
        _toasts = toasts;
        _logger = logger;

        NextCommand = new AsyncRelayCommand(GoToPositionAsync, () => Step == EnrollmentWizardStep.Welcome && EngineReady);
        BackCommand = new RelayCommand(() => GoToWelcome(), () => Step is EnrollmentWizardStep.Position);
        BeginCaptureCommand = new AsyncRelayCommand(BeginCaptureAsync, () => CanBeginCapture);
        RetryCommand = new AsyncRelayCommand(BeginCaptureAsync, () => Step == EnrollmentWizardStep.Failed && EngineReady);
        DoneCommand = new RelayCommand(() => GoToWelcome(), () => Step == EnrollmentWizardStep.Complete);
        CancelCommand = new RelayCommand(CancelWizard, () => Step is not EnrollmentWizardStep.Welcome);
        DeleteCommand = new AsyncRelayCommand(DeleteProfileAsync, () => ProfileExists && !IsEnrolling);

        _coordinator.PropertyChanged += OnCoordinatorPropertyChanged;
    }

    #region Bindings — wizard chrome

    public EnrollmentWizardStep Step
    {
        get => _step;
        private set
        {
            if (!SetProperty(ref _step, value))
                return;

            OnPropertyChanged(nameof(IsWelcome));
            OnPropertyChanged(nameof(IsPosition));
            OnPropertyChanged(nameof(IsCapture));
            OnPropertyChanged(nameof(IsProcessing));
            OnPropertyChanged(nameof(IsComplete));
            OnPropertyChanged(nameof(IsFailed));
            OnPropertyChanged(nameof(ShowPreview));
            OnPropertyChanged(nameof(StepNumberText));
            OnPropertyChanged(nameof(StepCaption));

            RaiseCommandStates();
        }
    }

    public bool IsWelcome => Step == EnrollmentWizardStep.Welcome;

    public bool IsPosition => Step == EnrollmentWizardStep.Position;

    public bool IsCapture => Step == EnrollmentWizardStep.Capture;

    public bool IsProcessing => Step == EnrollmentWizardStep.Processing;

    public bool IsComplete => Step == EnrollmentWizardStep.Complete;

    public bool IsFailed => Step == EnrollmentWizardStep.Failed;

    /// <summary>The live preview appears on Position and Capture only.</summary>
    public bool ShowPreview => Step is EnrollmentWizardStep.Position or EnrollmentWizardStep.Capture;

    /// <summary>"Step 3 of 5" — the failed branch counts as part of capture.</summary>
    public string StepNumberText
    {
        get
        {
            var number = Step switch
            {
                EnrollmentWizardStep.Welcome => 1,
                EnrollmentWizardStep.Position => 2,
                EnrollmentWizardStep.Capture or EnrollmentWizardStep.Failed => 3,
                EnrollmentWizardStep.Processing => 4,
                _ => 5,
            };

            return $"Step {number} of 5";
        }
    }

    public string StepCaption => Step switch
    {
        EnrollmentWizardStep.Welcome => "Welcome",
        EnrollmentWizardStep.Position => "Position your face",
        EnrollmentWizardStep.Capture => "Capture samples",
        EnrollmentWizardStep.Processing => "Processing",
        EnrollmentWizardStep.Complete => "Complete",
        _ => "Validation failed",
    };

    #endregion

    #region Bindings — capture progress

    public string Headline
    {
        get => _headline;
        private set => SetProperty(ref _headline, value);
    }

    public string Instruction
    {
        get => _instruction;
        private set => SetProperty(ref _instruction, value);
    }

    public string Detail
    {
        get => _detail;
        private set => SetProperty(ref _detail, value);
    }

    /// <summary>0–100 for the progress bar.</summary>
    public double Percent
    {
        get => _percent;
        private set => SetProperty(ref _percent, value);
    }

    /// <summary>e.g. "Samples captured: 8 / 20".</summary>
    public string CapturedLabel
    {
        get => _capturedLabel;
        private set => SetProperty(ref _capturedLabel, value);
    }

    public string FailureMessage
    {
        get => _failureMessage;
        private set => SetProperty(ref _failureMessage, value);
    }

    public string CompletedProfileName
    {
        get => _completedProfileName;
        private set => SetProperty(ref _completedProfileName, value);
    }

    public string CompletedDateText
    {
        get => _completedDateText;
        private set => SetProperty(ref _completedDateText, value);
    }

    public string CompletedSamplesText
    {
        get => _completedSamplesText;
        private set => SetProperty(ref _completedSamplesText, value);
    }

    public string CompletedModelVersion
    {
        get => _completedModelVersion;
        private set => SetProperty(ref _completedModelVersion, value);
    }

    public bool IsEnrolling => _coordinator.IsEnrolling;

    public bool ProfileExists => _coordinator.ProfileExists;

    public bool EngineReady => _coordinator.EngineReady;

    public bool CameraRunning => _coordinator.IsRunning;

    public string SampleTargetText => $"Target: {_settings.Recognition.EnrollmentSampleCount} samples";

    public string SampleCountText => $"0 / {_settings.Recognition.EnrollmentSampleCount}";

    public string EngineNotice => EngineReady
        ? string.Empty
        : "Recognition engine is not ready — models must be present before enrolling.";

    /// <summary>Engine readiness wording shown in the guidance sidebar.</summary>
    public string EngineStatusText => _coordinator.EngineStatus;

    #endregion

    #region Bindings — live preview (Position and Capture steps)

    public System.Windows.Media.Imaging.WriteableBitmap? Preview => _coordinator.Preview;

    public bool HasPreview => _coordinator.Preview is not null;

    public double SurfaceWidth => HasPreview ? _coordinator.PreviewPixelWidth : 1280;

    public double SurfaceHeight => HasPreview ? _coordinator.PreviewPixelHeight : 720;

    public System.Collections.Generic.IReadOnlyList<OverlayBox> Boxes => _coordinator.Boxes;

    public int FaceCount => Boxes.Count;

    public string QualityMessage => _coordinator.QualityMessage;

    /// <summary>
    /// Whether the operator may start sampling. Exactly one face is required:
    /// enrollment rejects zero-face and multi-face frames outright, so letting
    /// the button through would only produce an immediate rejection.
    /// </summary>
    public bool CanBeginCapture => IsPosition && CameraRunning && FaceCount == 1 && EngineReady;

    /// <summary>What the operator should do right now to become capturable.</summary>
    public string PositionHint => (IsPosition, FaceCount, CameraRunning) switch
    {
        (_, _, false) => "The camera is not running. Start it from the Camera screen, then try again.",
        (_, 0, _) => "No face detected. Move into the frame until the guide box appears.",
        (_, > 1, _) => "Only one person should be visible. Step out of frame if someone else is present.",
        _ => "Good. Keep that position — you can start sampling now.",
    };

    #endregion

    #region Bindings — commands

    public AsyncRelayCommand NextCommand { get; }

    public RelayCommand BackCommand { get; }

    public AsyncRelayCommand BeginCaptureCommand { get; }

    public AsyncRelayCommand RetryCommand { get; }

    public RelayCommand DoneCommand { get; }

    public RelayCommand CancelCommand { get; }

    public AsyncRelayCommand DeleteCommand { get; }

    #endregion

    public override Task OnNavigatedAsync()
    {
        // Returning to this page must not resurrect a step whose run ended
        // while the operator was elsewhere, and must not bury a finished run's
        // outcome either — there may be no notification left to deliver it.
        if (Step is EnrollmentWizardStep.Capture or EnrollmentWizardStep.Processing)
            TryApplyOutcome();

        if (Step == EnrollmentWizardStep.Welcome)
            CapturedLabel = $"Samples captured: {SampleCountText}";

        RefreshStaticState();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Moves Capture/Processing to Complete or Validation failed once a run has
    /// produced a result. Returns false while the run is still in flight (or
    /// was cancelled) so the caller leaves the current step alone.
    /// </summary>
    private bool TryApplyOutcome()
    {
        var result = _coordinator.LastEnrollmentResult;

        if (_coordinator.IsEnrolling || result is null)
            return false;

        if (result.Succeeded)
        {
            Headline = "Face profile created";
            Instruction = "Enrollment complete.";
            Detail = string.Empty;
            Percent = 100;
            Step = EnrollmentWizardStep.Complete;
            _ = LoadCompletedSummaryAsync();

            if (!_outcomeAnnounced)
            {
                _outcomeAnnounced = true;
                _toasts.Success(
                    "Face profile created",
                    $"{result.SampleCount} samples encrypted and saved · model {result.ModelVersion}.");
            }
        }
        else
        {
            FailureMessage = string.IsNullOrWhiteSpace(result.Message)
                ? "Enrollment failed."
                : result.Message;
            Instruction = "Validation failed";
            Step = EnrollmentWizardStep.Failed;

            if (!_outcomeAnnounced)
            {
                _outcomeAnnounced = true;
                _toasts.Error("Enrollment failed", FailureMessage);
            }
        }

        return true;
    }

    #region Step transitions

    /// <summary>Welcome → Position. Starts the camera so the preview is live.</summary>
    private async Task GoToPositionAsync()
    {
        ClearError();

        if (!EngineReady)
        {
            FailureMessage = "The recognition engine is not ready. Ensure the ONNX models are present in the models folder.";
            Step = EnrollmentWizardStep.Failed;
            return;
        }

        if (!_coordinator.IsRunning)
        {
            // Enumerate on demand — listing devices touches each device, so it
            // only happens once the operator has actually asked to enroll.
            if (_coordinator.Cameras.Count == 0)
                await _coordinator.RefreshCamerasAsync();

            if (_coordinator.Cameras.Count == 0)
            {
                FailureMessage = "No camera available.";
                Step = EnrollmentWizardStep.Failed;
                return;
            }

            await _coordinator.StartAsync();
            _startedCamera = _coordinator.IsRunning;
        }

        if (!_coordinator.IsRunning)
        {
            FailureMessage = _coordinator.CameraStatus;
            Step = EnrollmentWizardStep.Failed;
            return;
        }

        Instruction = ProfileExists ? "Re-enrolling: position your face." : "Position your face.";
        Detail = string.Empty;
        Step = EnrollmentWizardStep.Position;
        RaiseCommandStates();
    }

    /// <summary>Position → Capture. Starts the multi-sample collection.</summary>
    private async Task BeginCaptureAsync()
    {
        ClearError();

        if (!EngineReady)
        {
            FailureMessage = "Model unavailable. The recognition engine could not be loaded.";
            Step = EnrollmentWizardStep.Failed;
            return;
        }

        if (!_coordinator.IsRunning)
        {
            await _coordinator.StartAsync();
            _startedCamera = _coordinator.IsRunning;
        }

        if (!_coordinator.IsRunning)
        {
            FailureMessage = _coordinator.CameraStatus;
            Step = EnrollmentWizardStep.Failed;
            return;
        }

        if (FaceCount == 0)
        {
            FailureMessage = "No samples captured: no face was visible.";
            Step = EnrollmentWizardStep.Failed;
            return;
        }

        if (FaceCount > 1)
        {
            FailureMessage = "Multiple faces were detected. Only one person may be visible during enrollment.";
            Step = EnrollmentWizardStep.Failed;
            return;
        }

        var target = _settings.Recognition.EnrollmentSampleCount;
        Headline = ProfileExists ? "Re-enrolling" : "Enrolling";
        Instruction = "Look directly at the camera.";
        Detail = string.Empty;
        Percent = 0;
        CapturedLabel = $"Samples captured: 0 / {target}";
        FailureMessage = string.Empty;
        _outcomeAnnounced = false;

        _coordinator.BeginEnrollment(target);
        Step = EnrollmentWizardStep.Capture;
        RefreshStaticState();
    }

    /// <summary>Capture → Welcome, abandoning any run in flight.</summary>
    private void CancelWizard()
    {
        if (_coordinator.IsEnrolling)
            _coordinator.CancelEnrollment();

        GoToWelcome();
    }

    private void GoToWelcome()
    {
        if (_coordinator.IsEnrolling)
            _coordinator.CancelEnrollment();

        ReleaseCameraIfWeStartedIt();

        Detail = string.Empty;
        Percent = 0;
        FailureMessage = string.Empty;
        Headline = ProfileExists ? "Face profile enrolled" : "No face profile";
        CapturedLabel = SampleCountText;
        Step = EnrollmentWizardStep.Welcome;
        RefreshStaticState();
    }

    private void ReleaseCameraIfWeStartedIt()
    {
        if (!_startedCamera || !_coordinator.IsRunning)
            return;

        _startedCamera = false;
        _ = SafeStopCameraAsync();
    }

    private async Task SafeStopCameraAsync()
    {
        try
        {
            await _coordinator.StopAsync();
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Could not stop the camera after leaving the enrollment wizard");
        }
    }

    #endregion

    #region Completion

    private async Task LoadCompletedSummaryAsync()
    {
        var result = _coordinator.LastEnrollmentResult;

        CompletedSamplesText = $"{result?.SampleCount ?? 0} samples";
        CompletedModelVersion = string.IsNullOrWhiteSpace(result?.ModelVersion)
            ? "unavailable"
            : result!.ModelVersion;
        CompletedDateText = result is null
            ? DateTime.Now.ToString("f")
            : result.Timestamp.ToLocalTime().ToString("f");
        CompletedProfileName = "Default User";

        try
        {
            var profile = await _profiles.GetActiveAsync();
            if (profile is not null)
            {
                CompletedProfileName = profile.DisplayName;
                CompletedDateText = profile.CreatedAt.ToLocalTime().ToString("f");
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Could not read the profile summary for the completion screen");
        }
    }

    #endregion

    #region Profile management

    private async Task DeleteProfileAsync()
    {
        var confirmed = await _dialogs.ConfirmAsync(
            "Delete face profile",
            "This permanently removes the stored face profile and its encrypted embedding.\n\n" +
            "You will need to enroll again before recognition can work.\n\nContinue?");

        if (!confirmed)
            return;

        try
        {
            var profile = await _profiles.GetActiveAsync();
            if (profile is null)
                return;

            await _embeddings.DeleteByProfileIdAsync(profile.Id);
            await _profiles.DeleteAsync(profile.Id);

            await _events.RecordAsync(
                Core.Enums.SecurityEventType.EnrollmentFailed,
                Core.Enums.SecurityEventResult.Info,
                "Face profile deleted by user.");

            _coordinator.ProfileExists = false;
            Instruction = "Face profile deleted. Start enrollment to create a new one.";
            Detail = string.Empty;
            Headline = "No face profile";
            RefreshStaticState();
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Deleting the face profile failed");
            ReportError("Could not delete the face profile. See logs for details.", ex);
        }
    }

    #endregion

    private void OnCoordinatorPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(CameraCoordinator.IsEnrolling)
            or nameof(CameraCoordinator.EnrollmentProgress)
            or nameof(CameraCoordinator.EnrollmentMessage)
            or nameof(CameraCoordinator.LastEnrollmentResult)
            or nameof(CameraCoordinator.ProfileExists)
            or nameof(CameraCoordinator.EngineReady)
            or nameof(CameraCoordinator.IsRunning)
            or nameof(CameraCoordinator.Boxes)
            or nameof(CameraCoordinator.Preview)
            or nameof(CameraCoordinator.QualityMessage))
        {
            System.Windows.Application.Current?.Dispatcher.BeginInvoke(new Action(SyncFromCoordinator));
        }
    }

    private void SyncFromCoordinator()
    {
        var progress = _coordinator.EnrollmentProgress;
        var target = Math.Max(progress.Target, _settings.Recognition.EnrollmentSampleCount);

        Percent = progress.Percent;
        CapturedLabel = $"Samples captured: {progress.Captured} / {target}";

        // The only automatic transitions: samples finished, then the run's
        // outcome. Everything else is driven by an explicit button press so a
        // late notification can never move a wizard the operator already left.
        if (Step == EnrollmentWizardStep.Capture && _coordinator.IsEnrolling && progress.IsComplete)
        {
            Instruction = "Building your profile…";
            Detail = "Averaging samples, encrypting the template and saving it to the local database.";
            Step = EnrollmentWizardStep.Processing;
        }
        else if (Step is EnrollmentWizardStep.Capture or EnrollmentWizardStep.Processing)
        {
            TryApplyOutcome();
        }

        if (Step == EnrollmentWizardStep.Capture && _coordinator.IsEnrolling)
        {
            if (!string.IsNullOrWhiteSpace(progress.LastIssue))
                Detail = progress.LastIssue;
            else if (!string.IsNullOrWhiteSpace(progress.Instruction))
                Instruction = progress.Instruction;
        }

        RefreshStaticState();
    }

    private void RefreshStaticState()
    {
        OnPropertyChanged(nameof(IsEnrolling));
        OnPropertyChanged(nameof(ProfileExists));
        OnPropertyChanged(nameof(EngineReady));
        OnPropertyChanged(nameof(CameraRunning));
        OnPropertyChanged(nameof(SampleTargetText));
        OnPropertyChanged(nameof(SampleCountText));
        OnPropertyChanged(nameof(EngineNotice));
        OnPropertyChanged(nameof(EngineStatusText));
        OnPropertyChanged(nameof(CanBeginCapture));
        OnPropertyChanged(nameof(PositionHint));
        OnPropertyChanged(nameof(Preview));
        OnPropertyChanged(nameof(HasPreview));
        OnPropertyChanged(nameof(SurfaceWidth));
        OnPropertyChanged(nameof(SurfaceHeight));
        OnPropertyChanged(nameof(Boxes));
        OnPropertyChanged(nameof(FaceCount));
        OnPropertyChanged(nameof(QualityMessage));

        if (Headline is "No face profile" or "Face profile enrolled")
            Headline = ProfileExists ? "Face profile enrolled" : "No face profile";

        RaiseCommandStates();
    }

    private void RaiseCommandStates()
    {
        NextCommand.RaiseCanExecuteChanged();
        BackCommand.RaiseCanExecuteChanged();
        BeginCaptureCommand.RaiseCanExecuteChanged();
        RetryCommand.RaiseCanExecuteChanged();
        DoneCommand.RaiseCanExecuteChanged();
        CancelCommand.RaiseCanExecuteChanged();
        DeleteCommand.RaiseCanExecuteChanged();
    }
}
