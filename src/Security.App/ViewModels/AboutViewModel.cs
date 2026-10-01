using System;
using Security.Core.Interfaces;

namespace Security.App.ViewModels;

/// <summary>
/// Static informational screen. Exposes only read-only facts about the build,
/// the models, and the security/privacy posture.
/// </summary>
public sealed class AboutViewModel : ViewModelBase
{
    private readonly IFaceRecognitionService _recognition;
    private readonly ISettingsService _settings;

    public AboutViewModel(IFaceRecognitionService recognition, ISettingsService settings)
    {
        _recognition = recognition;
        _settings = settings;
    }

    public string ProductName => "SECURITY.EXE";

    /// <summary>Shown directly under the product name in About.</summary>
    public string Tagline => "Local Personal Security";

    public string DisplayName => "Security";

    public string Version => VersionInfo.Version;

    public string Phase => VersionInfo.Phase;

    /// <summary>"Version 0.3.0 — Phase 3"</summary>
    public string VersionLine => $"Version {Version} — {Phase}";

    public string BuildLine => $"{ProductName} · {Tagline}";

    public string VersionAndRuntimeLine => $"{VersionLine} · .NET {Environment.Version}";

    public string RuntimeLine => $".NET {Environment.Version} · {Environment.OSVersion.VersionString}";

    public string ModelLine => _recognition.IsReady
        ? $"SFace (recognition) + YuNet (detection) · threshold {_settings.Recognition.Threshold:F2}"
        : "Models unavailable";

    public string ThresholdNote =>
        $"Threshold is currently {_settings.Recognition.Threshold:F2}. This is a starting default, " +
        "not a universal constant — calibrate it against your own camera and model before relying on verdicts.";

    public string SecurityPosture =>
        "• Runs entirely on this machine — no cloud calls, no external biometric APIs.\n" +
        "• Face embeddings are encrypted with Windows DPAPI (CurrentUser) before they reach disk.\n" +
        "• Raw camera frames are never logged; they reach disk only as opt-in unknown-face snapshots (OFF by default).\n" +
        "• No passwords, PINs, or credentials are ever stored.\n" +
        "• Windows sign-in is never bypassed, modified, or simulated.";

    public string PrivacyPosture =>
        $"Security events stored: {(_settings.Current.StoreSecurityEvents ? "ON" : "OFF")}\n" +
        $"Image snapshots stored: {(_settings.Current.StoreSnapshots ? "ON (opt-in, local only)" : "OFF")}\n" +
        "Events record timestamps and outcomes only — never biometric samples.\n" +
        $"Events are kept for {_settings.Current.EventRetentionDays} day(s), then cleaned up automatically.";

    public string LivenessPosture =>
        "Liveness is a non-functional placeholder. It detects NO presentation attacks " +
        "and must not be treated as verification.";

    public string Limitations =>
        "• SECURITY.EXE does not replace Windows authentication — Windows is NOT unlocked, locked, or otherwise modified by this application.\n" +
        "• Recognition quality depends on lighting, camera, and pose.\n" +
        "• One enrolled profile only.\n" +
        "• Unknown-face snapshots are OFF by default; enabling them stores images on disk (retention-limited).\n" +
        "• Camera capture pauses whenever Windows locks; nothing is recorded on the lock screen.\n" +
        "• Thresholds are starting defaults — calibrate before relying on verdicts.\n" +
        "• Not certified for access control or compliance use.";

    /// <summary>Technology stack, listed explicitly for the About page.</summary>
    public string TechStackLine =>
        "C# / .NET 10 · WPF (MVVM) · OpenCvSharp 4.13 · ONNX Runtime 1.30 · " +
        "SQLite + EF Core 10 · Microsoft.Extensions DI / Logging · Serilog";

    public string PrivacyStatement =>
        "Everything runs on this machine: there are no cloud calls and no external biometric " +
        "APIs. Face templates are encrypted with Windows DPAPI (CurrentUser) before they reach " +
        "disk, and raw camera frames are never written to logs. No password, PIN, or " +
        "credential is ever stored. Security events record a timestamp and an outcome — never a " +
        "biometric sample — and image snapshots are OFF by default (opt-in, local only, " +
        "retention-limited). SECURITY.EXE does not replace Windows authentication.";

    public string OpenCvLine { get; } = "OpenCvSharp 4.13 · ONNX Runtime 1.30 · SQLite / EF Core 10";
}
