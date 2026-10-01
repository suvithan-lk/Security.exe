using Microsoft.Extensions.Logging;
using OpenCvSharp;
using Security.Core.Entities;
using Security.Core.Enums;
using Security.Core.Interfaces;
using Security.Core.Models;
using Security.Core.Services;
using Security.Face.Detection;
using Security.Face.Recognition;

namespace Security.Face.Enrollment;

/// <summary>
/// Multi-sample face enrollment.
///
/// Flow: SubmitFrameAsync (many times) -> CompleteAsync.
/// Each submitted frame is detected, quality-checked, aligned, and embedded.
/// Samples are rejected outright on: no face, multiple faces, too small, too far,
/// blurry, too dark, too bright. Once the target count is reached, CompleteAsync
/// averages the embeddings, L2-normalizes, encrypts, and persists.
///
/// Pose variety is encouraged via <see cref="EnrollmentProgress.Instruction"/>
/// (center / left / right / up / down) but not enforced.
/// </summary>
public sealed class EnrollmentService : IEnrollmentService
{
    private static readonly string[] PoseInstructions =
    [
        "Look directly at the camera.",
        "Turn your head slightly left.",
        "Turn your head slightly right.",
        "Tilt your head slightly up.",
        "Tilt your head slightly down.",
        "Keep only one face visible.",
    ];

    private readonly IFaceDetectionService _detection;
    private readonly IFaceEmbeddingService _embedding;
    private readonly IUserProfileRepository _profiles;
    private readonly IFaceEmbeddingRepository _embeddings;
    private readonly IDataProtectionService _protection;
    private readonly ISettingsService _settings;
    private readonly ISecurityEventService _events;
    private readonly IFaceQualityService _quality;
    private readonly ILogger<EnrollmentService>? _logger;

    private readonly object _gate = new();

    /// <summary>
    /// Minimum gap between accepted samples, in milliseconds.
    ///
    /// Without it a stationary subject yields the whole target in a couple of
    /// seconds and the averaged profile is built from near-identical frames —
    /// the exact opposite of the pose variety enrollment exists to capture.
    /// </summary>
    private const int SampleCooldownMs = 450;

    private List<float[]> _samples = new();
    private int _target;
    private int _frameCounter;
    private bool _enrolling;
    private long _nextSampleMs;
    private CancellationTokenSource? _runCts;

    public EnrollmentService(
        IFaceDetectionService detection,
        IFaceEmbeddingService embedding,
        IUserProfileRepository profiles,
        IFaceEmbeddingRepository embeddings,
        IDataProtectionService protection,
        ISettingsService settings,
        ISecurityEventService events,
        IFaceQualityService quality,
        ILogger<EnrollmentService>? logger = null)
    {
        _detection = detection;
        _embedding = embedding;
        _profiles = profiles;
        _embeddings = embeddings;
        _protection = protection;
        _settings = settings;
        _events = events;
        _quality = quality;
        _logger = logger;
    }

    public event EventHandler<EnrollmentProgress>? ProgressChanged;

    public bool IsEnrolling
    {
        get
        {
            lock (_gate)
            {
                return _enrolling;
            }
        }
    }

    public bool ProfileExists
    {
        get
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                return _profiles.HasActiveProfileAsync(cts.Token).GetAwaiter().GetResult();
            }
            catch
            {
                return false;
            }
        }
    }

    public void Begin(int targetSamples)
    {
        var target = Math.Clamp(targetSamples, 5, 100);

        lock (_gate)
        {
            _runCts?.Cancel();
            _runCts?.Dispose();
            _runCts = new CancellationTokenSource();

            _samples = new List<float[]>(target);
            _target = target;
            _frameCounter = 0;
            _nextSampleMs = 0;
            _enrolling = true;
        }

        _ = _events.RecordAsync(SecurityEventType.EnrollmentStarted, SecurityEventResult.Info,
            $"Enrollment started (target {target} samples).");

        RaiseProgress(lastIssue: string.Empty);
    }

    public async Task<EnrollmentProgress> SubmitFrameAsync(Mat frame, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(frame);

        if (!_detection.IsReady || !_embedding.IsReady)
        {
            var notReady = new EnrollmentProgress
            {
                Captured = 0,
                Target = _target,
                Instruction = "Recognition engine is not ready. Check that models are present.",
                LastIssue = "Model unavailable",
            };
            RaiseProgress(notReady.LastIssue);
            return notReady;
        }

        List<float[]>? snapshot;
        int target;
        bool enrolling;

        lock (_gate)
        {
            enrolling = _enrolling;
            target = _target;
            snapshot = _samples;
        }

        if (!enrolling)
            return new EnrollmentProgress { Captured = snapshot!.Count, Target = target, Instruction = "Not started" };

        // Sample every other frame so successive captures are not near-identical
        // (varied samples produce a more robust profile).
        _frameCounter++;
        if (_frameCounter % 2 != 0)
        {
            return new EnrollmentProgress
            {
                Captured = snapshot!.Count,
                Target = target,
                Instruction = CurrentInstruction(snapshot.Count, target),
            };
        }

        // Per-sample cooldown: hold the pacing (and the operator's attention on
        // the pose prompt) instead of firing every quality-passing frame.
        // Returning before detection also skips the embedding pass, so the
        // pipeline keeps its frame budget while waiting.
        long nextSampleMs;
        lock (_gate)
        {
            nextSampleMs = _nextSampleMs;
        }

        if (Environment.TickCount64 < nextSampleMs)
        {
            return new EnrollmentProgress
            {
                Captured = snapshot!.Count,
                Target = target,
                Instruction = CurrentInstruction(snapshot.Count, target),
            };
        }

        // Multi-face frames must not enroll anyone, and DetectPrimary already
        // collapses them to zero faces — but report the reason precisely.
        var raw = _detection.Detect(frame);
        FaceQualityResult quality;
        float[]? embedding = null;

        try
        {
            // Single source of truth: the wizard's live guidance and the actual
            // enrollment gate are the same service, so the operator is never
            // told a frame is good and then have it silently refused.
            quality = _quality.Evaluate(frame, raw);

            if (quality.IsAcceptable)
            {
                using var aligned = FaceAligner.Align(frame, raw.PrimaryFace!);
                embedding = await _embedding.GenerateEmbeddingAsync(aligned, cancellationToken).ConfigureAwait(false);

                if (!ProfileValidator.IsValidEmbedding(embedding))
                    embedding = null;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Embedding generation failed during enrollment");
            quality = FaceQualityResult.Reject("Could not process the frame.", 0);
        }

        int captured;
        bool complete;
        var accepted = quality.IsAcceptable && embedding is not null;

        lock (_gate)
        {
            if (accepted && _samples.Count < _target)
            {
                _samples.Add(embedding!);
                _nextSampleMs = Environment.TickCount64 + SampleCooldownMs;
            }

            captured = _samples.Count;
            complete = captured >= _target;
        }

        var lastIssue = accepted ? string.Empty : quality.Reason;
        var progress = new EnrollmentProgress
        {
            Captured = captured,
            Target = target,
            Instruction = complete ? "Sample collection complete." : CurrentInstruction(captured, target),
            LastIssue = lastIssue,
            CapturedPoseHints = PoseInstructions.Take(Math.Min(PoseInstructions.Length, Math.Max(1, captured / 4))).ToArray(),
        };

        RaiseProgress(progress.LastIssue);

        if (complete)
        {
            // Leave _enrolling true so CompleteAsync can persist; the UI reacts
            // to the progress bar reaching 100%.
        }

        return progress;
    }

    public async Task<EnrollmentResult> CompleteAsync(CancellationToken cancellationToken = default)
    {
        List<float[]> samples;
        int target;

        lock (_gate)
        {
            samples = _samples;
            target = _target;
            _enrolling = false;
        }

        if (!ProfileValidator.CanFinalize(samples, target))
        {
            var failMsg = $"Not enough valid samples ({samples.Count}/{target}).";
            await _events.RecordAsync(SecurityEventType.EnrollmentFailed, SecurityEventResult.Failure, failMsg, null, cancellationToken)
                .ConfigureAwait(false);

            return EnrollmentResult.Fail(failMsg);
        }

        try
        {
            // Average samples then normalize -> one stable profile vector.
            var averaged = RecognitionDecider.AverageAndNormalize(samples);

            if (!ProfileValidator.IsValidEmbedding(averaged))
            {
                await _events.RecordAsync(SecurityEventType.EnrollmentFailed, SecurityEventResult.Failure,
                    "Generated profile embedding was invalid.", null, cancellationToken).ConfigureAwait(false);

                return EnrollmentResult.Fail("Generated profile embedding was invalid.");
            }

            var protectedPayload = _protection.ProtectEmbedding(averaged);

            // Upsert the active profile + its embedding.
            var profile = await _profiles.GetActiveAsync(cancellationToken).ConfigureAwait(false);
            if (profile is null)
            {
                profile = new UserProfile
                {
                    DisplayName = "Default User",
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow,
                    IsActive = true,
                };
                profile = await _profiles.AddAsync(profile, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                profile.UpdatedAt = DateTime.UtcNow;
                await _profiles.UpdateAsync(profile, cancellationToken).ConfigureAwait(false);
            }

            var existing = await _embeddings.GetByProfileIdAsync(profile.Id, cancellationToken).ConfigureAwait(false);
            var record = new FaceEmbedding
            {
                UserProfileId = profile.Id,
                EmbeddingData = protectedPayload,
                ModelVersion = _embedding.ModelVersion,
                SampleCount = samples.Count,
                CreatedAt = DateTime.UtcNow,
            };

            if (existing is null)
                await _embeddings.AddAsync(record, cancellationToken).ConfigureAwait(false);
            else
                await _embeddings.UpdateAsync(record, cancellationToken).ConfigureAwait(false);

            await _events.RecordAsync(SecurityEventType.EnrollmentCompleted, SecurityEventResult.Success,
                $"Face profile created from {samples.Count} samples.", null, cancellationToken).ConfigureAwait(false);

            _logger?.LogInformation("Enrollment completed: {Count} samples, model {Model}",
                samples.Count, _embedding.ModelVersion);

            return new EnrollmentResult
            {
                Succeeded = true,
                ProfileId = profile.Id,
                SampleCount = samples.Count,
                ModelVersion = _embedding.ModelVersion,
                Message = "Face profile created.",
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Enrollment finalization failed");
            await _events.RecordAsync(SecurityEventType.EnrollmentFailed, SecurityEventResult.Failure,
                "Enrollment could not be saved.", null, CancellationToken.None).ConfigureAwait(false);

            return EnrollmentResult.Fail("Enrollment could not be saved. See logs for details.");
        }
        finally
        {
            lock (_gate)
            {
                _samples = new List<float[]>();
                _runCts?.Cancel();
                _runCts?.Dispose();
                _runCts = null;
            }
        }
    }

    public void Cancel()
    {
        lock (_gate)
        {
            _enrolling = false;
            _samples.Clear();
            _runCts?.Cancel();
            _runCts?.Dispose();
            _runCts = null;
        }

        _ = _events.RecordAsync(SecurityEventType.EnrollmentFailed, SecurityEventResult.Info, "Enrollment cancelled by user.");
    }

    private static string CurrentInstruction(int captured, int target)
    {
        if (target <= 0)
            return "Look directly at the camera.";

        // Rotate guidance as progress advances so samples vary in pose.
        var step = Math.Min(captured * PoseInstructions.Length / target, PoseInstructions.Length - 1);
        return PoseInstructions[Math.Max(0, step)];
    }

    private void RaiseProgress(string lastIssue)
    {
        EnrollmentProgress progress;
        List<float[]> snapshot;
        int target;

        lock (_gate)
        {
            snapshot = _samples;
            target = _target;
            progress = new EnrollmentProgress
            {
                Captured = snapshot.Count,
                Target = target,
                Instruction = CurrentInstruction(snapshot.Count, target),
                LastIssue = lastIssue,
            };
        }

        try
        {
            ProgressChanged?.Invoke(this, progress);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "An enrollment progress subscriber threw");
        }
    }

    private static Mat ToGray(Mat frame)
    {
        if (frame.Channels() == 1)
            return frame.Clone();

        var gray = new Mat();
        Cv2.CvtColor(frame, gray, ColorConversionCodes.BGR2GRAY);
        return gray;
    }
}
