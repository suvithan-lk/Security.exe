namespace Security.App.Services;

/// <summary>
/// Coarse capture state as presented to the operator.
///
/// Kept separate from <c>ICameraService.IsRunning</c> because the two answer
/// different questions: "is a device open?" versus "what should this page show?".
/// A failed start leaves the device stopped, but the page must show
/// CAMERA UNAVAILABLE with causes and a Retry button — which a plain boolean
/// cannot express, and which Phase 1 silently lost by reporting success.
/// </summary>
public enum CameraState
{
    /// <summary>No session. The page shows CAMERA OFFLINE with a Start button.</summary>
    Offline,

    /// <summary>Start requested, first frame not yet seen. Show CONNECTING.</summary>
    Connecting,

    /// <summary>Capturing and rendering. Show LIVE.</summary>
    Live,

    /// <summary>Start or capture failed. Show CAMERA UNAVAILABLE with causes and Retry.</summary>
    Error,
}
