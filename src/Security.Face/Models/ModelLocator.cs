using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Security.Core.Interfaces;
using Security.Core.Paths;

namespace Security.Face.Models;

/// <summary>
/// Locates the ONNX models under <c>models/</c> and (when enabled) downloads
/// them from the official opencv_zoo repository.
///
/// Source &amp; license:
///  - face_detection_yunet_2023mar.onnx — opencv_zoo, Apache-2.0
///  - face_recognition_sface_2021dec.onnx — opencv_zoo, Apache-2.0
///
/// Nothing is downloaded at runtime unless the files are missing and
/// <see cref="ModelLocatorOptions.AllowDownload"/> is true.
/// </summary>
public sealed class ModelLocator : IModelLocator
{
    public const string DetectionModelFileName = "face_detection_yunet_2023mar.onnx";
    public const string EmbeddingModelFileName = "face_recognition_sface_2021dec.onnx";

    public const string DetectionModelUrl =
        "https://media.githubusercontent.com/media/opencv/opencv_zoo/main/models/face_detection_yunet/face_detection_yunet_2023mar.onnx";

    public const string EmbeddingModelUrl =
        "https://media.githubusercontent.com/media/opencv/opencv_zoo/main/models/face_recognition_sface/face_recognition_sface_2021dec.onnx";

    private readonly ModelLocatorOptions _options;
    private readonly ILogger<ModelLocator>? _logger;

    public ModelLocator(ModelLocatorOptions? options = null, ILogger<ModelLocator>? logger = null)
    {
        _options = options ?? new ModelLocatorOptions();
        _logger = logger;

        // Relative paths resolve against the repository root, not the process
        // working directory — otherwise models/ would be re-downloaded into
        // whatever folder happened to launch the app.
        ModelsDirectory = AppPaths.ResolvePath(_options.ModelsDirectory);
    }

    public string ModelsDirectory { get; }

    public string FaceDetectionModelPath => Path.Combine(ModelsDirectory, DetectionModelFileName);

    public string FaceEmbeddingModelPath => Path.Combine(ModelsDirectory, EmbeddingModelFileName);

    public bool FaceDetectionModelExists => IsNonEmptyFile(FaceDetectionModelPath);

    public bool FaceEmbeddingModelExists => IsNonEmptyFile(FaceEmbeddingModelPath);

    public async Task<bool> EnsureModelsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            Directory.CreateDirectory(ModelsDirectory);

            if (!FaceDetectionModelExists)
                await TryDownloadAsync(DetectionModelUrl, FaceDetectionModelPath, cancellationToken);

            if (!FaceEmbeddingModelExists)
                await TryDownloadAsync(EmbeddingModelUrl, FaceEmbeddingModelPath, cancellationToken);

            return FaceDetectionModelExists && FaceEmbeddingModelExists;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Model preflight failed");
            return false;
        }
    }

    private async Task TryDownloadAsync(string url, string destination, CancellationToken cancellationToken)
    {
        if (!_options.AllowDownload)
        {
            _logger?.LogWarning(
                "Model {File} is missing and automatic download is disabled. " +
                "Run scripts/download-models.ps1 to fetch it.",
                Path.GetFileName(destination));
            return;
        }

        if (_options.DownloadTimeoutSeconds <= 0)
            return;

        _logger?.LogInformation("Downloading model {File}...", Path.GetFileName(destination));

        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(_options.DownloadTimeoutSeconds) };
            using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();

            var tempPath = destination + ".download";
            await using (var body = await response.Content.ReadAsStreamAsync(cancellationToken))
            await using (var file = File.Create(tempPath))
            {
                await body.CopyToAsync(file, cancellationToken);
            }

            // Refuse obviously-wrong payloads (HTML error pages, LFS pointers).
            var info = new FileInfo(tempPath);
            if (info.Length < 1024)
            {
                File.Delete(tempPath);
                _logger?.LogWarning("Downloaded model {File} was too small and has been discarded", Path.GetFileName(destination));
                return;
            }

            if (File.Exists(destination))
                File.Delete(destination);

            File.Move(tempPath, destination);
            _logger?.LogInformation("Model {File} downloaded ({Bytes} bytes)", Path.GetFileName(destination), info.Length);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex,
                "Could not download {File}. Place it manually under {Dir}",
                Path.GetFileName(destination), ModelsDirectory);
        }
    }

    private static bool IsNonEmptyFile(string path)
        => File.Exists(path) && new FileInfo(path).Length > 1024;

    /// <summary>
    /// SHA-256 of a model file — used by tests/tooling to pin exact model builds.
    /// </summary>
    public static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }
}

public sealed class ModelLocatorOptions
{
    /// <summary>
    /// Models directory. Absolute paths are used as-is; relative paths resolve
    /// against the repository root (falling back to the executable directory).
    /// </summary>
    public string ModelsDirectory { get; set; } = "models";

    /// <summary>
    /// When true, missing models are fetched from opencv_zoo on startup.
    /// Set false for air-gapped deployments.
    /// </summary>
    public bool AllowDownload { get; set; } = true;

    public int DownloadTimeoutSeconds { get; set; } = 120;
}
