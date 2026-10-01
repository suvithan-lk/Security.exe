using OpenCvSharp;
using Security.Core.Models;

namespace Security.Core.Interfaces;

/// <summary>
/// Turns an aligned face image into a fixed-length, L2-normalized embedding.
/// </summary>
public interface IFaceEmbeddingService : IDisposable
{
    /// <summary>True when the ONNX model is loaded and inference is available.</summary>
    bool IsReady { get; }

    /// <summary>Model identifier stored alongside embeddings (e.g. "sface-2021dec").</summary>
    string ModelVersion { get; }

    /// <summary>Embedding dimensionality. 0 until the model has been loaded once.</summary>
    int Dimension { get; }

    /// <summary>
    /// Generate an embedding for an aligned face image.
    /// The returned vector is L2-normalized. Throws if the model is unavailable.
    /// </summary>
    Task<float[]> GenerateEmbeddingAsync(Mat alignedFace, CancellationToken cancellationToken = default);
}
