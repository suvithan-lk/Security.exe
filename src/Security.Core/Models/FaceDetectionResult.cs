using Security.Core.Enums;

namespace Security.Core.Models;

/// <summary>
/// Result of analysing a single frame for faces.
/// </summary>
public sealed class FaceDetectionResult
{
    public IReadOnlyList<DetectedFace> Faces { get; init; } = Array.Empty<DetectedFace>();

    public int FaceCount => Faces.Count;

    public bool HasFace => Faces.Count > 0;

    public bool IsSingleFace => Faces.Count == 1;

    /// <summary>
    /// The largest face in the frame (largest ≈ nearest the camera), used for
    /// enrollment, recognition, and the quality gate.
    ///
    /// Deliberately not <c>Faces[0]</c>: detector output order is not
    /// guaranteed, and picking whatever the model happened to emit first lets a
    /// background face, or a stray detection, decide what the subject's
    /// bounding box and embedding are taken from.
    /// </summary>
    public DetectedFace? PrimaryFace
    {
        get
        {
            if (Faces.Count == 0)
                return null;

            var best = Faces[0];
            for (var i = 1; i < Faces.Count; i++)
            {
                if (Faces[i].Area > best.Area)
                    best = Faces[i];
            }

            return best;
        }
    }

    public string StatusText => Faces.Count switch
    {
        0 => "No face detected",
        1 => "Face detected",
        _ => "Multiple faces detected. Please ensure only one person is visible.",
    };

    /// <summary>
    /// Width, in pixels, below which a detection is treated as detector noise
    /// rather than as a person.
    ///
    /// Anchored on half the configured minimum face size — the size at which
    /// recognition would give up anyway — but never below 24px and never more
    /// than 5% of the frame width, so the rule stays sensible at small or very
    /// large capture sizes.
    ///
    /// YuNet emits confident-looking ~15px false positives on texture and hands.
    /// Counting those as a second person put "Only one person should be visible"
    /// on about half of all frames in front of a perfectly good face, which is
    /// what stopped enrollment from ever completing on real hardware.
    /// </summary>
    public static double NoiseFloorWidth(int frameWidth, double minimumFaceSize)
    {
        var configured = minimumFaceSize / 2.0;
        return Math.Max(24, Math.Min(configured, frameWidth * 0.05));
    }

    /// <summary>
    /// The detections big enough to be people, in the same order the detector
    /// reported them.
    ///
    /// This is the single definition of "how many people are in this frame" for
    /// the quality gate, the preview status, and recognition — so the overlay
    /// can never claim one face while recognition refuses the same frame for
    /// having two.
    /// </summary>
    public IReadOnlyList<DetectedFace> CountableFaces(int frameWidth, double minimumFaceSize)
    {
        if (Faces.Count == 0)
            return Array.Empty<DetectedFace>();

        var floor = NoiseFloorWidth(frameWidth, minimumFaceSize);

        // Nothing to filter: return the original list rather than a copy of it.
        return Faces.All(f => f.Width >= floor)
            ? Faces
            : Faces.Where(f => f.Width >= floor).ToArray();
    }
}

/// <summary>
/// A face located within a frame, in frame pixel coordinates.
/// </summary>
public sealed class DetectedFace
{
    public float X { get; init; }

    public float Y { get; init; }

    public float Width { get; init; }

    public float Height { get; init; }

    public float Score { get; init; } = 1f;

    /// <summary>Five landmarks (left eye, right eye, nose, left mouth corner, right mouth corner).</summary>
    public IReadOnlyList<(float X, float Y)> Landmarks { get; init; } = Array.Empty<(float, float)>();

    public float Area => Width * Height;

    public float CenterX => X + Width / 2f;

    public float CenterY => Y + Height / 2f;
}
