using Microsoft.Extensions.Logging;
using OpenCvSharp;
using Security.Core.Enums;
using Security.Core.Interfaces;
using Security.Core.Models;
using Security.Core.Services;
using Security.Face.Detection;

namespace Security.Face.Recognition;

/// <summary>
/// Full recognition pipeline: detect -> quality gate -> align -> embed -> compare.
///
/// Never throws for expected conditions; returns UnableToDetermine instead.
/// </summary>
public sealed class FaceRecognitionService : IFaceRecognitionService
{
    private readonly IFaceDetectionService _detection;
    private readonly IFaceEmbeddingService _embedding;
    private readonly IUserProfileRepository _profiles;
    private readonly IFaceEmbeddingRepository _embeddings;
    private readonly IDataProtectionService _protection;
    private readonly ISettingsService _settings;
    private readonly ILogger<FaceRecognitionService>? _logger;

    private readonly SemaphoreSlim _profileGate = new(1, 1);

    private CachedProfile? _cached;
    private bool _cacheLoaded;

    public FaceRecognitionService(
        IFaceDetectionService detection,
        IFaceEmbeddingService embedding,
        IUserProfileRepository profiles,
        IFaceEmbeddingRepository embeddings,
        IDataProtectionService protection,
        ISettingsService settings,
        ILogger<FaceRecognitionService>? logger = null)
    {
        _detection = detection;
        _embedding = embedding;
        _profiles = profiles;
        _embeddings = embeddings;
        _protection = protection;
        _settings = settings;
        _logger = logger;
    }

    public bool IsReady => _detection.IsReady && _embedding.IsReady;

    public double Threshold => RecognitionDecider.NormalizeThreshold(_settings.Recognition.Threshold);

    public async Task<FaceRecognitionResult> RecognizeAsync(Mat frame, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(frame);

        if (frame.Empty())
            return FaceRecognitionResult.UnableToDetermine("Invalid frame.");

        if (!IsReady)
            return FaceRecognitionResult.UnableToDetermine("Recognition engine is not ready.");

        if (!_settings.Current.RecognitionEnabled)
            return FaceRecognitionResult.UnableToDetermine("Recognition is disabled in settings.");

        // Detect including multi-face frames so we can refuse them explicitly.
        var detection = _detection.Detect(frame);
        var options = _settings.Recognition;

        // The same "how many people are in this frame" rule the preview overlay
        // and the quality gate use. Without it the overlay could report Face
        // detected while recognition refused the identical frame for having two
        // faces, and neither of them was lying.
        var countable = detection.CountableFaces(frame.Width, options.MinimumFaceSize);

        if (countable.Count == 0)
            return FaceRecognitionResult.UnableToDetermine("No face detected.");

        if (countable.Count > 1)
            return FaceRecognitionResult.UnableToDetermine("Multiple faces detected.");

        var face = countable[0];

        if (!ProfileValidator.IsFaceLargeEnough(face.Width, frame.Width, options))
            return FaceRecognitionResult.UnableToDetermine("Face is too small or too far away.");

        var profile = await GetProfileAsync(cancellationToken);
        if (profile is null)
            return FaceRecognitionResult.UnableToDetermine("No face profile has been enrolled.");

        float[] observed;
        try
        {
            using var aligned = FaceAligner.Align(frame, face);
            observed = await _embedding.GenerateEmbeddingAsync(aligned, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Embedding generation failed");
            return FaceRecognitionResult.UnableToDetermine("Could not analyse the face.");
        }

        if (!ProfileValidator.IsValidEmbedding(observed) ||
            !ProfileValidator.IsValidEmbedding(profile.Embedding))
        {
            return FaceRecognitionResult.UnableToDetermine("Stored profile is unreadable.");
        }

        var similarity = RecognitionDecider.CosineSimilarity(observed, profile.Embedding);
        var threshold = Threshold;
        var status = RecognitionDecider.Decide(similarity, threshold);

        return FaceRecognitionResult.Create(status, similarity, threshold, profile.ProfileId, DateTime.UtcNow);
    }

    public async Task<bool> HasEnrolledProfileAsync(CancellationToken cancellationToken = default)
        => await GetProfileAsync(cancellationToken) is not null;

    /// <summary>
    /// Best-effort synchronous view of enrollment state for status displays.
    /// Uses the cache when populated; otherwise performs a short bounded read.
    /// </summary>
    public bool HasEnrolledProfile
    {
        get
        {
            lock (_profileGate)
            {
                if (_cacheLoaded)
                    return _cached is not null;
            }

            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                return GetProfileAsync(cts.Token).GetAwaiter().GetResult() is not null;
            }
            catch
            {
                // Never let a status query throw.
                return false;
            }
        }
    }

    /// <summary>
    /// Drop the cached profile so the next recognition re-reads the database.
    /// Called after enrollment or profile deletion.
    /// </summary>
    public void InvalidateCache()
    {
        lock (_profileGate)
        {
            _cached = null;
            _cacheLoaded = false;
        }
    }

    private async Task<CachedProfile?> GetProfileAsync(CancellationToken cancellationToken)
    {
        lock (_profileGate)
        {
            if (_cacheLoaded)
                return _cached;
        }

        CachedProfile? loaded = null;
        try
        {
            var profile = await _profiles.GetActiveAsync(cancellationToken);
            if (profile?.FaceEmbedding is not null)
            {
                var embedding = _protection.UnprotectEmbedding(profile.FaceEmbedding.EmbeddingData);
                if (ProfileValidator.IsValidEmbedding(embedding))
                {
                    loaded = new CachedProfile(profile.Id, embedding, profile.FaceEmbedding.ModelVersion);
                }
                else
                {
                    _logger?.LogWarning("Stored embedding failed validation for profile {Id}", profile.Id);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Covers DPAPI failures (different user/machine) and DB errors.
            _logger?.LogWarning(ex, "Could not load the enrolled profile");
        }

        lock (_profileGate)
        {
            _cached = loaded;
            _cacheLoaded = true;
        }

        return loaded;
    }

    private sealed record CachedProfile(int ProfileId, float[] Embedding, string ModelVersion);
}
