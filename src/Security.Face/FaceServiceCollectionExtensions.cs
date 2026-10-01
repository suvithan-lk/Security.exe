using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Security.Core.Interfaces;
using Security.Core.Models;
using Security.Face.Camera;
using Security.Face.Detection;
using Security.Face.Enrollment;
using Security.Face.Models;
using Security.Face.Pipeline;
using Security.Face.Quality;
using Security.Face.Recognition;

namespace Security.Face;

public static class FaceServiceCollectionExtensions
{
    /// <summary>
    /// Register camera, detection, embedding, recognition, enrollment,
    /// liveness, and the frame pipeline.
    ///
    /// <paramref name="modelsDirectory"/> defaults to <c>models</c> next to the
    /// executable. Set <paramref name="allowModelDownload"/> to false for
    /// air-gapped deployments (models must then be supplied manually).
    /// </summary>
    public static IServiceCollection AddSecurityFace(
        this IServiceCollection services,
        string? modelsDirectory = null,
        bool allowModelDownload = true,
        CameraOptions? cameraOptions = null)
    {
        services.AddSingleton(cameraOptions ?? new CameraOptions());

        services.AddSingleton<IModelLocator>(sp => new ModelLocator(
            new ModelLocatorOptions
            {
                ModelsDirectory = string.IsNullOrWhiteSpace(modelsDirectory) ? "models" : modelsDirectory,
                AllowDownload = allowModelDownload,
            },
            sp.GetService<ILogger<ModelLocator>>()));

        services.AddSingleton<ICameraService>(sp => new CameraService(
            sp.GetRequiredService<CameraOptions>(),
            sp.GetService<ILogger<CameraService>>()));

        services.AddSingleton<IFaceDetectionService>(sp => new FaceDetectionService(
            sp.GetRequiredService<IModelLocator>(),
            sp.GetService<ILogger<FaceDetectionService>>()));

        services.AddSingleton<IFaceEmbeddingService>(sp => new FaceEmbeddingService(
            sp.GetRequiredService<IModelLocator>(),
            sp.GetService<ILogger<FaceEmbeddingService>>()));

        services.AddSingleton<ILivenessService>(sp => new LivenessService(
            sp.GetService<ILogger<LivenessService>>()));

        services.AddSingleton<IFaceRecognitionService, FaceRecognitionService>();
        services.AddSingleton<IEnrollmentService, EnrollmentService>();
        services.AddSingleton<IFaceQualityService, FaceQualityService>();

        // Local-only JPEG snapshots for unknown-face events (data/events/).
        services.AddSingleton<ISnapshotStore>(sp => new Snapshots.SnapshotStore(
            sp.GetService<ILogger<Snapshots.SnapshotStore>>()));

        services.AddSingleton<IFrameProcessor, FrameProcessor>();

        return services;
    }

    /// <summary>
    /// Best-effort model preflight. Returns true when both ONNX models are
    /// present and loadable. Never throws — the app runs degraded without them.
    /// </summary>
    /// <remarks>
    /// The services are resolved as singletons, so they must NOT be disposed
    /// here: <c>using</c> would tear them down while the rest of the
    /// application still holds references to them. Lifetime is owned by the
    /// container.
    /// </remarks>
    public static async Task<bool> EnsureFaceModelsAsync(this IServiceProvider services, CancellationToken cancellationToken = default)
    {
        var logger = services.GetService<ILoggerFactory>()?.CreateLogger("Security.Face.ModelPreflight");
        var locator = services.GetRequiredService<IModelLocator>();

        logger?.LogInformation("Model directory: {Dir}", locator.ModelsDirectory);

        bool present;
        try
        {
            present = await locator.EnsureModelsAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "Model preflight failed while checking for the ONNX files");
            return false;
        }

        if (!present)
        {
            logger?.LogWarning(
                "One or both ONNX models are missing under {Dir}. " +
                "Run scripts/download-models.ps1 or connect to the internet to fetch them.",
                locator.ModelsDirectory);
            return false;
        }

        // Verify they actually LOAD, not merely exist. IsReady forces session
        // creation (SFace is tens of megabytes), so run it on the thread pool —
        // EnsureModelsAsync can complete synchronously and would otherwise
        // leave the load on the caller's (UI) thread.
        try
        {
            var detection = services.GetRequiredService<IFaceDetectionService>();
            var embedding = services.GetRequiredService<IFaceEmbeddingService>();

            var (detectionReady, embeddingReady) = await Task.Run(() =>
            {
                var d = detection.IsReady;
                var e = embedding.IsReady;
                return (d, e);
            }, cancellationToken).ConfigureAwait(false);

            if (embeddingReady)
            {
                using var probe = new OpenCvSharp.Mat(112, 112, OpenCvSharp.MatType.CV_8UC3, OpenCvSharp.Scalar.All(128));
                _ = await embedding.GenerateEmbeddingAsync(probe, cancellationToken).ConfigureAwait(false);
            }

            var ready = detectionReady && embeddingReady;

            if (!ready)
                logger?.LogWarning(
                    "Model load finished with detectionReady={Detection} embeddingReady={Embedding}",
                    detectionReady, embeddingReady);

            return ready;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Previously swallowed silently, which made an unstartable
            // recognition engine look like a mystery. Log the real reason —
            // it never contains biometric data, only file paths and ONNX errors.
            logger?.LogError(ex, "ONNX models are present but could not be loaded or executed");
            return false;
        }
    }
}
