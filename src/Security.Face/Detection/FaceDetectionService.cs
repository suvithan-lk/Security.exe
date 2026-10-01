using Microsoft.Extensions.Logging;
using OpenCvSharp;
using Security.Core.Interfaces;
using Security.Core.Models;
using Security.Face.Models;

namespace Security.Face.Detection;

/// <summary>
/// YuNet (ONNX) face detection via OpenCV's FaceDetectorYN.
/// Reports five landmarks so the embedding stage can align the crop.
///
/// Note: this OpenCvSharp version exposes only FaceDetectorYN.Create/Detect —
/// there is no SetInputSize. FaceDetectorYN.Create takes the input size, so the
/// detector is (re)created whenever the incoming frame size changes, which is
/// exactly equivalent to calling setInputSize on each size change.
/// </summary>
public sealed class FaceDetectionService : IFaceDetectionService
{
    private const float MinimumScore = 0.6f;

    /// <summary>
    /// Column index of the detection score. Verified empirically: sweeping the
    /// detector's score threshold makes detections disappear between 0.90 and
    /// 0.92, matching the value at index 14 (0.909) — so score is the LAST of
    /// the 15 columns, not index 4.
    /// </summary>
    private const int ScoreIndex = 14;

    private readonly IModelLocator _modelLocator;
    private readonly ILogger<FaceDetectionService>? _logger;
    private readonly object _gate = new();

    private FaceDetectorYN? _detector;
    private Size _detectorInputSize;
    private bool _loadFailed;
    private bool _missingLogged;
    private bool _disposed;

    /// <summary>
    /// Size used when the detector is created from a readiness probe rather
    /// than from a real frame. YuNet is recreated on any frame-size change, so
    /// this costs one extra create on the first real frame.
    /// </summary>
    private static readonly Size ProbeSize = new(640, 480);

    public FaceDetectionService(IModelLocator modelLocator, ILogger<FaceDetectionService>? logger = null)
    {
        _modelLocator = modelLocator;
        _logger = logger;
    }

    /// <summary>
    /// True once the YuNet session has actually been created.
    ///
    /// This deliberately forces the load instead of reporting "have we seen a
    /// frame yet": readiness is what the dashboard, the preflight, and the
    /// recognition gate all ask about, and it has to mean "the model loads",
    /// not "a camera happened to deliver a frame".
    /// </summary>
    public bool IsReady
    {
        get
        {
            lock (_gate)
            {
                if (_disposed || _loadFailed)
                    return false;

                // Prefer the frame size we already have. Passing the probe size
                // while a detector sized to real frames exists would dispose and
                // recreate it on every readiness check.
                var size = _detector is not null ? _detectorInputSize : ProbeSize;
                return EnsureLoaded(size) is not null;
            }
        }
    }

    public FaceDetectionResult Detect(Mat frame)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(frame);

        if (frame.Empty() || frame.Width <= 0 || frame.Height <= 0)
            return new FaceDetectionResult();

        var detector = EnsureLoaded(new Size(frame.Width, frame.Height));
        if (detector is null)
            return new FaceDetectionResult();

        Mat? faces = null;
        try
        {
            lock (_gate)
            {
                // Zero rows when nothing was found — the detector writes an
                // empty Nx15 matrix either way.
                faces = new Mat();
                detector.Detect(frame, faces);
            }

            return Parse(faces, frame.Width, frame.Height);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Face detection failed for a {W}x{H} frame", frame.Width, frame.Height);
            return new FaceDetectionResult();
        }
        finally
        {
            faces?.Dispose();
        }
    }

    public FaceDetectionResult DetectPrimary(Mat frame)
    {
        var all = Detect(frame);
        if (all.FaceCount <= 1)
            return all;

        // Multi-face: collapse to empty so downstream stages cannot act on a crowd.
        return new FaceDetectionResult();
    }

    /// <summary>
    /// Full detection including multi-face frames — used by the camera overlay,
    /// which must SHOW that several faces are present.
    /// </summary>
    public FaceDetectionResult DetectAll(Mat frame) => Detect(frame);

    private static FaceDetectionResult Parse(Mat faces, int frameWidth, int frameHeight)
    {
        if (faces is null || faces.Empty())
            return new FaceDetectionResult();

        var rows = faces.Rows;
        if (rows <= 0 || faces.Cols < 15)
            return new FaceDetectionResult();

        var list = new List<DetectedFace>(rows);

        for (var i = 0; i < rows; i++)
        {
            // Row layout verified empirically against the shipped model:
            //   [x, y, w, h,
            //    re_x, re_y, le_x, le_y, nose_x, nose_y, rm_x, rm_y, lm_x, lm_y,
            //    score]
            // Score is the LAST column (index 14), not index 4 — confirmed by
            // sweeping the detector's score threshold: detections disappear
            // between 0.90 and 0.92, matching the value at index 14 (0.909),
            // while index 4 held a non-probability value.
            float Get(int col) => faces.At<float>(i, col);

            var score = Get(ScoreIndex);
            if (score < MinimumScore)
                continue;

            var w = Get(2);
            var h = Get(3);
            if (w <= 1 || h <= 1)
                continue;

            var landmarks = new List<(float X, float Y)>(5)
            {
                (Get(4), Get(5)),   // right eye
                (Get(6), Get(7)),   // left eye
                (Get(8), Get(9)),   // nose
                (Get(10), Get(11)), // right mouth corner
                (Get(12), Get(13)), // left mouth corner
            };

            // Discard nonsensical geometry rather than trusting the model blindly.
            if (!IsSane(landmarks, frameWidth, frameHeight))
                continue;

            list.Add(new DetectedFace
            {
                X = Get(0),
                Y = Get(1),
                Width = w,
                Height = h,
                Score = score,
                Landmarks = landmarks,
            });
        }

        // Largest area first = "primary" face.
        var ordered = list.OrderByDescending(f => f.Area).ToList();
        return new FaceDetectionResult { Faces = ordered };
    }

    private static bool IsSane(IReadOnlyList<(float X, float Y)> landmarks, int width, int height)
    {
        if (width <= 0 || height <= 0)
            return false;

        foreach (var (x, y) in landmarks)
        {
            if (!float.IsFinite(x) || !float.IsFinite(y))
                return false;

            if (x < -width || x > width * 2 || y < -height || y > height * 2)
                return false;
        }

        return true;
    }

    private FaceDetectorYN? EnsureLoaded(Size frameSize)
    {
        lock (_gate)
        {
            if (_disposed)
                return null;

            // Only a genuine load failure latches. A *missing* file must not:
            // the model can be downloaded a moment later, and readiness is
            // probed during startup before that download necessarily finishes.
            if (_loadFailed)
                return null;

            var path = _modelLocator.FaceDetectionModelPath;
            if (!_modelLocator.FaceDetectionModelExists)
            {
                if (!_missingLogged)
                {
                    _missingLogged = true;
                    _logger?.LogWarning(
                        "Face detection model missing at {Path}. Face detection is disabled until it is provided. " +
                        "Run scripts/download-models.ps1.",
                        path);
                }

                return null;
            }

            _missingLogged = false;

            // Reuse unless the frame geometry changed.
            if (_detector is not null && _detectorInputSize == frameSize)
                return _detector;

            try
            {
                _detector?.Dispose();
                _detector = null;

                _detector = FaceDetectorYN.Create(
                    path,
                    "",
                    frameSize,
                    scoreThreshold: MinimumScore,
                    nmsThreshold: 0.3f,
                    topK: 5000);

                _detectorInputSize = frameSize;

                return _detector;
            }
            catch (Exception ex)
            {
                _loadFailed = true;
                _detector?.Dispose();
                _detector = null;
                _logger?.LogError(ex, "Failed to load face detection model from {Path}", path);
                return null;
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
            _detector?.Dispose();
            _detector = null;
        }

        GC.SuppressFinalize(this);
    }
}
