namespace Security.Core.Interfaces;

/// <summary>
/// Locates the ONNX model files on disk and reports readiness.
/// Keeps path logic out of the ML services so it can be faked in tests.
/// </summary>
public interface IModelLocator
{
    /// <summary>Directory holding model files.</summary>
    string ModelsDirectory { get; }

    string FaceDetectionModelPath { get; }

    string FaceEmbeddingModelPath { get; }

    bool FaceDetectionModelExists { get; }

    bool FaceEmbeddingModelExists { get; }

    /// <summary>
    /// Ensure both models exist, downloading them if configured to do so.
    /// Returns true when both models are ready to load.
    /// </summary>
    Task<bool> EnsureModelsAsync(CancellationToken cancellationToken = default);
}
