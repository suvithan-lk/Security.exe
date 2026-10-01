using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using OpenCvSharp;
using Security.Core.Interfaces;
using Security.Face.Models;

namespace Security.Face.Recognition;

/// <summary>
/// Face embedding via ONNX Runtime running the SFace model
/// (opencv_zoo face_recognition_sface, Apache-2.0).
///
/// Pipeline per call:
///   112x112 BGR crop -> RGB -> normalize -> NCHW tensor -> inference -> L2-normalize.
///
/// Input/output names and shapes are read from the model itself at load time;
/// nothing about the graph layout is hard-coded beyond the documented
/// preprocessing contract of this model family.
/// </summary>
public sealed class FaceEmbeddingService : IFaceEmbeddingService
{
    /// <summary>Documented preprocessing constants for SFace (see opencv_zoo README).</summary>
    private const float PixelMean = 127.5f;

    private const float PixelScale = 127.5f;

    private readonly IModelLocator _modelLocator;
    private readonly ILogger<FaceEmbeddingService>? _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private InferenceSession? _session;
    private string _inputName = string.Empty;
    private string _outputName = string.Empty;
    private int _inputHeight;
    private int _inputWidth;
    private bool _loadFailed;
    private bool _missingLogged;
    private bool _disposed;

    public FaceEmbeddingService(IModelLocator modelLocator, ILogger<FaceEmbeddingService>? logger = null)
    {
        _modelLocator = modelLocator;
        _logger = logger;
        ModelVersion = "sface-2021dec";
    }

    public string ModelVersion { get; }

    public int Dimension { get; private set; }

    /// <summary>
    /// True once the SFace session has actually been created.
    ///
    /// Forces the load rather than reporting "have we been called yet", so the
    /// dashboard and the preflight get a truthful answer. Loading SFace costs
    /// a few hundred milliseconds, so callers that run on the UI thread should
    /// probe from the background (see CameraCoordinator.RefreshEngineStatusAsync).
    /// </summary>
    public bool IsReady => EnsureLoaded();

    public async Task<float[]> GenerateEmbeddingAsync(Mat alignedFace, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(alignedFace);

        if (alignedFace.Empty())
            throw new ArgumentException("Face image is empty.", nameof(alignedFace));

        if (!EnsureLoaded())
            throw new InvalidOperationException(
                "Face embedding model is not available. See logs for details.");

        // Preprocessing happens outside the session lock; only inference is serialized
        // because InferenceSession.Run is shared state.
        var tensor = BuildTensor(alignedFace);

        float[] raw;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            var inputs = new[] { NamedOnnxValue.CreateFromTensor(_inputName, tensor) };
            using var results = _session!.Run(inputs);
            var output = results.First(r => r.Name == _outputName || string.IsNullOrEmpty(_outputName));
            var span = output.AsTensor<float>().ToArray();
            raw = span;
        }
        finally
        {
            _gate.Release();
        }

        if (raw.Length == 0)
            throw new InvalidOperationException("Face embedding model returned an empty vector.");

        Dimension = raw.Length;

        // Cosine similarity is scale invariant, but normalizing keeps stored
        // vectors canonical and makes the threshold meaningful across versions.
        return Security.Core.Services.RecognitionDecider.L2Normalize(raw);
    }

    private static Tensor<float> BuildTensor(Mat face)
    {
        // Model expects 112x112. Anything else is resized defensively.
        Mat? temp = null;
        var working = face;
        if (face.Width != 112 || face.Height != 112)
        {
            temp = new Mat();
            Cv2.Resize(face, temp, new Size(112, 112));
            working = temp;
        }

        try
        {
            var tensor = new DenseTensor<float>(new[] { 1, 3, 112, 112 });

            // working is BGR, HWC. Convert to RGB and normalize to [-1, 1].
            unsafe
            {
                for (var y = 0; y < 112; y++)
                {
                    var bytes = (byte*)working.Ptr(y);

                    for (var x = 0; x < 112; x++)
                    {
                        var b = bytes[x * 3 + 0];
                        var g = bytes[x * 3 + 1];
                        var r = bytes[x * 3 + 2];

                        tensor[0, 0, y, x] = (r - PixelMean) / PixelScale;
                        tensor[0, 1, y, x] = (g - PixelMean) / PixelScale;
                        tensor[0, 2, y, x] = (b - PixelMean) / PixelScale;
                    }
                }
            }

            return tensor;
        }
        finally
        {
            temp?.Dispose();
        }
    }

    private bool EnsureLoaded()
    {
        lock (_gate)
        {
            if (_disposed)
                return false;

            if (_session is not null)
                return true;

            if (_loadFailed)
                return false;

            var path = _modelLocator.FaceEmbeddingModelPath;
            if (!_modelLocator.FaceEmbeddingModelExists)
            {
                // Not latched: a missing model is expected before the first-run
                // download completes, and readiness must recover once it lands.
                if (!_missingLogged)
                {
                    _missingLogged = true;
                    _logger?.LogWarning(
                        "Face embedding model missing at {Path}. Recognition is disabled until it is provided. " +
                        "Run scripts/download-models.ps1.",
                        path);
                }

                return false;
            }

            _missingLogged = false;

            try
            {
                var options = new SessionOptions
                {
                    InterOpNumThreads = 1,
                    IntraOpNumThreads = Math.Max(1, Environment.ProcessorCount / 2),
                    GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
                };

                _session = new InferenceSession(path, options);

                // Read the graph contract from the model — do not assume names.
                var input = _session.InputMetadata.First();
                var output = _session.OutputMetadata.First();

                _inputName = input.Key;
                _outputName = output.Key;

                var dims = input.Value.Dimensions;
                // Expect [N, C, H, W] or [N, H, W, C].
                _inputHeight = dims.Length == 4 ? Math.Abs(dims[2]) : 112;
                _inputWidth = dims.Length == 4 ? Math.Abs(dims[3]) : 112;

                var outDims = output.Value.Dimensions;
                Dimension = outDims.Length >= 2 && outDims[1] > 0 ? outDims[1] : 0;

                _logger?.LogInformation(
                    "Face embedding model loaded: input={Input} {H}x{W}, output={Output} dim={Dim}",
                    _inputName, _inputHeight, _inputWidth, _outputName, Dimension);

                return true;
            }
            catch (Exception ex)
            {
                _loadFailed = true;
                _session?.Dispose();
                _session = null;
                _logger?.LogError(ex, "Failed to load face embedding model from {Path}", path);
                return false;
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        lock (_gate)
        {
            _session?.Dispose();
            _session = null;
        }

        _gate.Dispose();
        GC.SuppressFinalize(this);
    }
}
