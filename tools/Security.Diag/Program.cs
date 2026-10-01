using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenCvSharp;
using Security.Core.Interfaces;
using Security.Core.Models;
using Security.Core.Paths;
using Security.Face;
using Security.Data;
using Security.Infrastructure;

namespace Security.Diag;

/// <summary>
/// Hardware / pipeline diagnostic harness.
///
/// Drives the REAL camera, REAL ONNX models, REAL DPAPI encryption and REAL
/// SQLite persistence through the exact same DI wiring the WPF app uses, and
/// reports a PASS/FAIL per stage. It exists so "the camera should work" can be
/// replaced by measured evidence.
///
/// No UI thread is involved, so a failure here isolates the pipeline from WPF.
/// </summary>
public static class Program
{
    private static int _pass;
    private static int _fail;
    private static int _warn;

    public static async Task<int> Main(string[] args)
    {
        Console.WriteLine("SECURITY.EXE — Phase 2 diagnostic harness");
        Console.WriteLine(new string('=', 64));

        using var host = BuildServices();

        var logger = host.GetRequiredService<ILoggerFactory>().CreateLogger("Diag");
        logger.LogInformation("Diagnostic run starting");

        // Settings must be loaded from the database before anything reads them,
        // otherwise quality gates run on constructor defaults.
        try
        {
            await host.EnsureSecurityDatabaseAsync();
            await host.GetRequiredService<ISettingsService>().LoadAsync();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  [WARN] settings load failed: {ex.Message}");
        }

        var modelsOk = await host.EnsureFaceModelsAsync();
        Check("ONNX models present and loadable", modelsOk);

        // ---- Stage 1: camera enumeration -----------------------------------
        var camera = host.GetRequiredService<ICameraService>();
        var cameras = await Timed("Camera enumeration", () => camera.GetAvailableCamerasAsync());
        foreach (var c in cameras)
            Console.WriteLine($"           -> [{c.Index}] {c.Name}");
        Check("At least one camera enumerated", cameras.Count > 0);

        if (cameras.Count == 0)
            return Finish("No camera found — cannot continue hardware stages.");

        // ---- Stage 2: start + frame delivery -------------------------------
        var detector = host.GetRequiredService<IFaceDetectionService>();
        var embedding = host.GetRequiredService<IFaceEmbeddingService>();
        var recognition = host.GetRequiredService<IFaceRecognitionService>();
        var enrollment = host.GetRequiredService<IEnrollmentService>();
        var settings = host.GetRequiredService<ISettingsService>();

        var frameCount = 0;
        long firstFrameMs = -1;
        var startSw = Stopwatch.StartNew();
        var frameTimes = new List<long>();

        void OnFrame(object? s, CameraFrameEventArgs e)
        {
            var n = Interlocked.Increment(ref frameCount);
            if (n == 1) firstFrameMs = startSw.ElapsedMilliseconds;
            lock (frameTimes) frameTimes.Add(Environment.TickCount64);
        }

        camera.FrameReceived += OnFrame;

        var target = args.Length > 0 && int.TryParse(args[0], out var idx)
            ? cameras.FirstOrDefault(c => c.Index == idx)
            : cameras[0];

        Console.WriteLine($"\n--- Starting camera: {target} ---");
        var openSw = Stopwatch.StartNew();
        Exception? openError = null;
        try
        {
            await camera.StartAsync(target);
        }
        catch (Exception ex)
        {
            openError = ex;
        }
        openSw.Stop();

        Check("StartAsync returned without throwing", openError is null);
        if (openError is not null)
            Console.WriteLine($"           !! {openError.GetType().Name}: {openError.Message}");

        Check("CameraService.IsRunning == true after start", camera.IsRunning);

        // Wait up to 5s for the first frame.
        var deadline = Environment.TickCount64 + 5000;
        while (frameCount == 0 && Environment.TickCount64 < deadline)
            await Task.Delay(50);

        Check($"First frame received within 5s (got {firstFrameMs} ms)", firstFrameMs >= 0);
        if (firstFrameMs < 0)
        {
            camera.FrameReceived -= OnFrame;
            return Finish("Camera opened but delivered NO frames. Preview cannot work.");
        }

        await Task.Delay(4000);
        var elapsedS = frameTimes.Count >= 2
            ? (frameTimes[^1] - frameTimes[0]) / 1000.0
            : 0;
        var fps = elapsedS > 0 ? (frameTimes.Count - 1) / elapsedS : 0;
        Console.WriteLine($"           frames={frameCount}  measured FPS={fps:0.0}");
        Check("Camera delivers frames at >= 10 FPS", fps >= 10);

        // ---- Stage 3: capture a frame for analysis -------------------------
        using var sample = camera.CaptureFrame();
        Check("CaptureFrame() returned a usable Mat", sample is not null && !sample.Empty());
        if (sample is null || sample.Empty())
        {
            camera.FrameReceived -= OnFrame;
            await camera.StopAsync();
            return Finish("CaptureFrame returned nothing to analyse.");
        }

        Console.WriteLine($"           frame = {sample.Width}x{sample.Height}");

        Check("Detection model reports ready", detector.IsReady);
        Check("Embedding model reports ready", embedding.IsReady);
        Check("Recognition service reports ready", recognition.IsReady);

        // ---- Stage 4: face detection ---------------------------------------
        var det = detector.Detect(sample);
        Console.WriteLine($"           faces={det.FaceCount} status=\"{det.StatusText}\"");
        for (var i = 0; i < det.Faces.Count; i++)
        {
            var f = det.Faces[i];
            Console.WriteLine($"             face[{i}] x={f.X} y={f.Y} w={f.Width} h={f.Height} score={f.Score:0.00}");
        }

        var facePresent = det.FaceCount >= 1;
        WarnCheck("A face is visible in the frame",
            facePresent,
            "Sit in front of the camera and re-run to exercise detection/enrollment/recognition.");

        if (!facePresent)
        {
            camera.FrameReceived -= OnFrame;
            await camera.StopAsync();
            return Finish("No face in frame — hardware stages requiring a person were NOT verified.");
        }

        // ---- Stage 5: embedding correctness --------------------------------
        using var aligned = await AlignSample(detector, sample);
        if (aligned is not null)
        {
            var vec = await embedding.GenerateEmbeddingAsync(aligned);
            Check($"Embedding produced (length {vec?.Length ?? 0})", vec is { Length: > 0 });

            if (vec is { Length: > 0 })
            {
                var norm = Math.Sqrt(vec.Sum(v => (double)v * v));
                Console.WriteLine($"           L2 norm = {norm:0.000000}");
                Check("Embedding is L2-normalized (|v| ~ 1.0)", Math.Abs(norm - 1.0) < 0.01);

                var vec2 = await embedding.GenerateEmbeddingAsync(aligned);
                if (vec2 is { Length: > 0 })
                {
                    var self = Cosine(vec, vec2);
                    Console.WriteLine($"           cosine(self, self-again) = {self:0.0000}");
                    Check("Embedding is deterministic for identical input", self > 0.999);
                }
            }
        }

        // ---- Stage 6: enrollment end-to-end --------------------------------
        Console.WriteLine("\n--- Enrollment end-to-end ---");
        var targetSamples = settings.Recognition.EnrollmentSampleCount > 0
            ? settings.Recognition.EnrollmentSampleCount
            : 20;
        Console.WriteLine($"           target samples = {targetSamples}");

        enrollment.Begin(targetSamples);
        var issueTally = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var accepted = 0;
        EnrollmentProgress progress = new() { Captured = 0, Target = targetSamples };
        var enrollSw = Stopwatch.StartNew();

        while (!progress.IsComplete && enrollSw.Elapsed < TimeSpan.FromSeconds(60))
        {
            using var f = camera.CaptureFrame();
            if (f is null || f.Empty())
            {
                await Task.Delay(60);
                continue;
            }

            var capturedBefore = progress.Captured;
            progress = await enrollment.SubmitFrameAsync(f);

            // Classify honestly: an empty LastIssue is returned by the pacing
            // paths (every-other-frame and the per-sample cooldown), so it does
            // NOT mean a sample was accepted. Counting those as accepted buried
            // the real rejection breakdown.
            var issue = progress.Captured > capturedBefore
                ? "(sample captured)"
                : string.IsNullOrWhiteSpace(progress.LastIssue)
                    ? "(skipped: pacing)"
                    : progress.LastIssue;
            issueTally[issue] = issueTally.TryGetValue(issue, out var n) ? n + 1 : 1;

            if (progress.Captured > accepted)
            {
                accepted = progress.Captured;
                Console.WriteLine($"           sample {progress.Captured}/{progress.Target}  {issue}");
            }

            if (progress.IsComplete)
                break;

            await Task.Delay(60);
        }

        Console.WriteLine("           rejection tally:");
        foreach (var kv in issueTally.OrderByDescending(k => k.Value))
            Console.WriteLine($"             {kv.Value,4} x  {kv.Key}");

        Check($"Enrollment reached {targetSamples} samples", progress.IsComplete);

        if (progress.IsComplete)
        {
            var result = await enrollment.CompleteAsync();
            Check("Enrollment completed and persisted", result.Succeeded);
            Console.WriteLine($"           {result.Message} (samples={result.SampleCount})");

            // ---- Stage 7: recognition --------------------------------------
            Console.WriteLine("\n--- Recognition ---");

            // Sample several frames so one unlucky blink or head turn does not
            // produce a false negative for the whole stage.
            Mat? recognisable = null;
            for (var attempt = 0; attempt < 10 && recognisable is null; attempt++)
            {
                var candidate = camera.CaptureFrame();
                if (candidate is null)
                    continue;

                if (detector.Detect(candidate).IsSingleFace)
                {
                    recognisable = candidate;
                }
                else
                {
                    candidate.Dispose();
                    await Task.Delay(120);
                }
            }

            if (recognisable is null)
            {
                WarnCheck("Captured a frame suitable for recognition", false,
                    "No single-face frame obtained within 10 attempts.");
            }
            else
            {
                using (recognisable)
                {
                    var known = await recognition.RecognizeAsync(recognisable);
                    Console.WriteLine($"           status={known.Status} similarity={known.Similarity:0.0000} confidence={known.Confidence:0.0000} threshold={settings.Recognition.Threshold:0.00} reason=\"{known.Reason}\"");
                    Check("Recognition returns a determinate verdict",
                        known.Status != Core.Enums.RecognitionStatus.UnableToDetermine);
                    Check("Recognition returns KNOWN for the enrolled person",
                        known.Status == Core.Enums.RecognitionStatus.Known);
                }
            }

            // ---- Stage 8: negative control --------------------------------
            // A frame containing NO face must never yield a match; this proves
            // the verdict path is gated on real detection.
            using var noFace = new Mat(720, 1280, MatType.CV_8UC3, Scalar.Black);
            var absent = await recognition.RecognizeAsync(noFace);
            Console.WriteLine($"           control(no face): status={absent.Status} reason=\"{absent.Reason}\"");
            Check("A frame with no face is never reported as KNOWN",
                absent.Status != Core.Enums.RecognitionStatus.Known);
        }

        // ---- Stage 9: the REAL app analysis path (FrameProcessor) --------
        // The WPF app never calls RecognizeAsync directly; it drives
        // FrameProcessor. This stage exercises throttling, stability gating,
        // the recognition cooldown, the enrollment frame sink, and repeated
        // Attach/Detach (the SemaphoreFullException regression).
        Console.WriteLine("\n--- FrameProcessor (app analysis path) ---");
        var processor = host.GetRequiredService<IFrameProcessor>();
        var analyzed = 0;
        var verdicts = 0;
        var unknownAlerts = 0;
        var statusTally = new Dictionary<string, int>(StringComparer.Ordinal);
        FaceRecognitionResult? lastVerdict = null;

        void OnAnalyzed(object? s, FrameAnalysisEventArgs e)
        {
            Interlocked.Increment(ref analyzed);
            lock (statusTally)
                statusTally[e.StatusText] = statusTally.TryGetValue(e.StatusText, out var n) ? n + 1 : 1;
        }
        void OnVerdict(object? s, FaceRecognitionResult r)
        {
            Interlocked.Increment(ref verdicts);
            lastVerdict = r;
        }
        void OnUnknownAlert(object? s, FaceRecognitionResult r) => Interlocked.Increment(ref unknownAlerts);

        processor.FrameAnalyzed += OnAnalyzed;
        processor.RecognitionCompleted += OnVerdict;
        processor.UnknownFaceDetected += OnUnknownAlert;

        // Repeated attach/detach must not throw (SemaphoreFullException regression).
        var attachOk = true;
        try
        {
            processor.Attach(camera);
            processor.Detach();
            processor.Attach(camera);
        }
        catch (Exception ex)
        {
            attachOk = false;
            Console.WriteLine($"           !! {ex.GetType().Name}: {ex.Message}");
        }
        Check("Attach -> Detach -> Attach does not throw", attachOk && processor.IsProcessing);

        // Non-blocking heap sample. A forced GC (GetTotalMemory(true)) blocks in
        // WaitForPendingFinalizers and demonstrably never returns while the
        // OpenCV capture loop and ONNX session are alive, so force-collection is
        // deliberately avoided here — leak detection uses the trend instead.
        Console.WriteLine("           [probe] sampling heap (non-blocking)…");
        var memBefore = SafeHeapBytes();
        var wsBefore = WorkingSetBytes();
        Console.WriteLine($"           [probe] heap={Mb(memBefore)} MB workingSet={Mb(wsBefore)} MB");

        var procSw = Stopwatch.StartNew();
        var ticks = 0;
        while (procSw.Elapsed < TimeSpan.FromSeconds(8))
        {
            await Task.Delay(100);
            if (++ticks % 10 == 0)
                Console.WriteLine($"           [probe] t={procSw.Elapsed.TotalSeconds:0.0}s analyses={analyzed} verdicts={verdicts}");
        }
        Console.WriteLine("           [probe] analysis window elapsed");
        var memAfter = SafeHeapBytes();
        var wsAfter = WorkingSetBytes();

        Console.WriteLine($"           analyses={analyzed}  verdicts={verdicts}  unknownAlerts={unknownAlerts}");
        foreach (var kv in statusTally.OrderByDescending(k => k.Value))
            Console.WriteLine($"             \"{kv.Key}\" x {kv.Value}");
        if (lastVerdict is not null)
            Console.WriteLine($"           last verdict: {lastVerdict.Status} sim={lastVerdict.Similarity:0.0000}");

        Check($"FrameAnalyzed raised during live capture (got {analyzed})", analyzed > 0);
        Check($"Recognition produced at least one verdict (got {verdicts})", verdicts >= 1);

        // 8 s window with a 5 s cooldown must NOT yield a verdict per frame.
        var maxExpected = 8 / Math.Max(1, settings.Recognition.RecognitionCooldownSeconds) + 2;
        Check($"Recognition cooldown throttles verdicts (got {verdicts}, max expected {maxExpected})",
            verdicts <= maxExpected);

        var memDeltaMb = (memAfter - memBefore) / (1024.0 * 1024.0);
        var wsDeltaMb = (wsAfter - wsBefore) / (1024.0 * 1024.0);
        Console.WriteLine($"           managed heap delta over 8 s: {memDeltaMb:+0.0;-0.0} MB");
        Console.WriteLine($"           working set delta over 8 s: {wsDeltaMb:+0.0;-0.0} MB");
        Check("Managed heap stable during sustained analysis", memDeltaMb < 30);
        Check("Working set stable during sustained analysis", wsDeltaMb < 80);

        // ---- Stage 10: enrollment THROUGH the app's own frame sink ----------
        // This is the path that actually failed in Phase 1: the app never calls
        // SubmitFrameAsync directly, it installs FrameProcessor.FrameSink. If
        // the detection throttle never opens, this stage stalls at 0 samples —
        // which is precisely what the Phase 1 log showed (EnrollmentStarted
        // recorded, EnrollmentCompleted never appearing).
        Console.WriteLine("\n--- Enrollment via FrameProcessor.FrameSink (real app path) ---");
        var sinkTarget = 5;
        var sinkProgress = new EnrollmentProgress { Captured = 0, Target = sinkTarget };
        var sinkTally = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        enrollment.Begin(sinkTarget);
        processor.RecognitionSuppressed = true;
        processor.FrameSink = async (frame, ct) =>
        {
            var before = sinkProgress.Captured;
            var p = await enrollment.SubmitFrameAsync(frame, ct).ConfigureAwait(false);
            sinkProgress = p;

            // See Stage 6: an empty LastIssue means the frame was skipped for
            // pacing, not that a sample was captured.
            var reason = p.Captured > before
                ? "(sample captured)"
                : string.IsNullOrWhiteSpace(p.LastIssue)
                    ? "(skipped: pacing)"
                    : p.LastIssue;
            lock (sinkTally)
                sinkTally[reason] = sinkTally.TryGetValue(reason, out var n) ? n + 1 : 1;
        };

        var sinkSw = Stopwatch.StartNew();
        while (!sinkProgress.IsComplete && sinkSw.Elapsed < TimeSpan.FromSeconds(45))
            await Task.Delay(200);

        processor.FrameSink = null;
        processor.RecognitionSuppressed = false;

        Console.WriteLine($"           samples {sinkProgress.Captured}/{sinkProgress.Target} in {sinkSw.Elapsed.TotalSeconds:0.0}s");
        if (sinkTally.Count > 0)
        {
            Console.WriteLine("           rejection tally:");
            lock (sinkTally)
            {
                foreach (var kv in sinkTally.OrderByDescending(k => k.Value))
                    Console.WriteLine($"             {kv.Value,4} x  {kv.Key}");
            }
        }

        Check($"Enrollment reached {sinkTarget} samples via the FrameSink",
            sinkProgress.IsComplete);

        if (!sinkProgress.IsComplete)
        {
            enrollment.Cancel();
        }
        else
        {
            var persisted = await enrollment.CompleteAsync();
            Check("Sink-driven enrollment persisted", persisted.Succeeded);
        }

        processor.FrameAnalyzed -= OnAnalyzed;
        processor.RecognitionCompleted -= OnVerdict;
        processor.UnknownFaceDetected -= OnUnknownAlert;
        processor.FrameSink = null;
        processor.Detach();

        // ---- Stage 10: start/stop reliability -----------------------------
        Console.WriteLine("\n--- Start/stop cycles ---");
        camera.FrameReceived -= OnFrame;
        var cyclesOk = 0;
        for (var i = 0; i < 3; i++)
        {
            try
            {
                await camera.StopAsync();
                await Task.Delay(250);
                frameCount = 0;
                startSw.Restart();
                camera.FrameReceived += OnFrame;
                await camera.StartAsync(target);
                var wait = Environment.TickCount64 + 4000;
                while (frameCount == 0 && Environment.TickCount64 < wait)
                    await Task.Delay(50);
                camera.FrameReceived -= OnFrame;

                var ok = camera.IsRunning && frameCount > 0;
                Console.WriteLine($"           cycle {i + 1}: running={camera.IsRunning} frames={frameCount} -> {(ok ? "OK" : "FAIL")}");
                if (ok) cyclesOk++;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"           cycle {i + 1}: EXCEPTION {ex.GetType().Name}: {ex.Message}");
            }
        }
        Check("3/3 start/stop cycles delivered frames", cyclesOk == 3);

        try { await camera.StopAsync(); } catch { /* already stopped */ }
        Check("IsRunning == false after StopAsync", !camera.IsRunning);

        // ---- Stage 10: memory ------------------------------------------------
        using var proc = Process.GetCurrentProcess();
        Console.WriteLine($"\nWorking set after run: {proc.WorkingSet64 / (1024.0 * 1024.0):0.0} MB");
        Console.WriteLine($"GC total memory:       {GC.GetTotalMemory(false) / (1024.0 * 1024.0):0.0} MB");

        return Finish(null);
    }

    private static async Task<Mat?> AlignSample(IFaceDetectionService detector, Mat frame)
    {
        try
        {
            var det = detector.Detect(frame);
            if (det.Faces.Count == 0) return null;
            var f = det.Faces[0];
            var rect = new Rect(
                Math.Max(0, (int)f.X),
                Math.Max(0, (int)f.Y),
                Math.Min((int)f.Width, frame.Width - Math.Max(0, (int)f.X)),
                Math.Min((int)f.Height, frame.Height - Math.Max(0, (int)f.Y)));
            if (rect.Width <= 0 || rect.Height <= 0) return null;
            using var roi = new Mat(frame, rect);
            var outMat = new Mat();
            Cv2.Resize(roi, outMat, new Size(112, 112));
            return outMat;
        }
        catch
        {
            return null;
        }
    }

    private static double Cosine(float[] a, float[] b)
    {
        var n = Math.Min(a.Length, b.Length);
        double dot = 0, na = 0, nb = 0;
        for (var i = 0; i < n; i++)
        {
            dot += (double)a[i] * b[i];
            na += (double)a[i] * a[i];
            nb += (double)b[i] * b[i];
        }
        return na <= 0 || nb <= 0 ? 0 : dot / (Math.Sqrt(na) * Math.Sqrt(nb));
    }

    /// <summary>Managed heap size without forcing a collection (never blocks).</summary>
    private static long SafeHeapBytes()
    {
        try { return GC.GetTotalMemory(false); }
        catch { return 0; }
    }

    /// <summary>Process working set. Cheap and never blocks.</summary>
    private static long WorkingSetBytes()
    {
        try
        {
            using var p = Process.GetCurrentProcess();
            p.Refresh();
            return p.WorkingSet64;
        }
        catch { return 0; }
    }

    private static double Mb(long bytes) => bytes / (1024.0 * 1024.0);

    private static async Task<T> Timed<T>(string label, Func<Task<T>> action)
    {
        var sw = Stopwatch.StartNew();
        var result = await action();
        sw.Stop();
        Console.WriteLine($"{label}: {sw.ElapsedMilliseconds} ms");
        return result;
    }

    private static ServiceProvider BuildServices()
    {
        var modelsDir = Path.Combine(AppPaths.RepositoryRootDirectory ?? AppContext.BaseDirectory, "models");

        var services = new ServiceCollection();
        services.AddLogging(b =>
        {
            b.AddSimpleConsole(o =>
            {
                o.SingleLine = true;
                o.TimestampFormat = "HH:mm:ss ";
            });
            b.SetMinimumLevel(Microsoft.Extensions.Logging.LogLevel.Information);
        });

        services.AddSecurityData();
        services.AddSecurityInfrastructure();
        services.AddSecurityFace(modelsDirectory: modelsDir, allowModelDownload: false);

        return services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true,
            ValidateOnBuild = false,
        });
    }

    private static void Check(string label, bool ok)
    {
        if (ok)
        {
            _pass++;
            Console.WriteLine($"  [PASS] {label}");
        }
        else
        {
            _fail++;
            Console.WriteLine($"  [FAIL] {label}");
        }
    }

    private static void WarnCheck(string label, bool ok, string hint)
    {
        if (ok)
        {
            _pass++;
            Console.WriteLine($"  [PASS] {label}");
        }
        else
        {
            _warn++;
            Console.WriteLine($"  [WARN] {label}");
            Console.WriteLine($"         {hint}");
        }
    }

    private static int Finish(string? message)
    {
        Console.WriteLine();
        Console.WriteLine(new string('=', 64));
        if (message is not null)
            Console.WriteLine(message);
        Console.WriteLine($"RESULT: {_pass} passed, {_fail} failed, {_warn} warnings");
        return _fail > 0 ? 1 : 0;
    }
}
