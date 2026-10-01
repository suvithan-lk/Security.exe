using OpenCvSharp;
using Security.Core.Models;

namespace Security.Core.Interfaces;

/// <summary>
/// Decides whether a live camera frame is good enough to be used as an
/// enrollment sample or as input to recognition.
///
/// The checks are deliberately ordered from most to least actionable so the
/// operator is always told the ONE thing to fix next, rather than being
/// flooded with every possible complaint.
/// </summary>
public interface IFaceQualityService
{
    /// <summary>
    /// Evaluate a frame that has already been through detection.
    ///
    /// <paramref name="detection"/> supplies face count and geometry;
    /// <paramref name="frame"/> is inspected for blur and exposure. The frame is
    /// read-only and is not retained.
    /// </summary>
    FaceQualityResult Evaluate(Mat frame, FaceDetectionResult detection);

    /// <summary>
    /// Convenience overload that runs detection itself. Prefer passing an
    /// existing detection result when you already have one.
    /// </summary>
    FaceQualityResult Evaluate(Mat frame);
}
