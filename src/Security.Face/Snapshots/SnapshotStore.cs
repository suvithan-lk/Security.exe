using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using OpenCvSharp;
using Security.Core.Interfaces;
using Security.Core.Paths;

namespace Security.Face.Snapshots;

/// <summary>
/// Local-disk JPEG snapshot store for unknown-face events.
///
/// Files land in <c>data/events/</c> next to the rest of the application data,
/// named <c>unknown_yyyyMMdd_HHmmss_{random}.jpg</c> so they are unique without
/// any user input, sortable by time, and carry no identity information.
/// Every failure is swallowed and logged: a snapshot is evidence, never a
/// reason to interrupt recognition.
/// </summary>
public sealed class SnapshotStore : ISnapshotStore, IDisposable
{
    private const string RelativeDirectory = "data/events";

    private readonly ILogger<SnapshotStore>? _logger;
    private readonly int _jpegQuality;
    private bool _disposed;

    public SnapshotStore(ILogger<SnapshotStore>? logger = null, int jpegQuality = 90)
    {
        _logger = logger;
        _jpegQuality = Math.Clamp(jpegQuality, 1, 100);
        DirectoryPath = AppPaths.ResolveDirectory(RelativeDirectory);
    }

    public string DirectoryPath { get; }

    public string? Save(Mat frame, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(frame);

        if (frame.Empty())
            return null;

        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            Directory.CreateDirectory(DirectoryPath);

            var parameters = new[]
            {
                new ImageEncodingParam(ImwriteFlags.JpegQuality, _jpegQuality),
            };

            // ImEncode returns void and signals failure by throwing.
            Cv2.ImEncode(".jpg", frame, out var buffer, parameters);
            if (buffer is null || buffer.Length == 0)
            {
                _logger?.LogWarning("Snapshot JPEG encoding returned no data");
                return null;
            }

            var fileName =
                $"unknown_{DateTime.Now:yyyyMMdd_HHmmss}_{RandomSuffix()}.jpg";
            var absolutePath = Path.Combine(DirectoryPath, fileName);

            File.WriteAllBytes(absolutePath, buffer);

            // Store the relative path on the event: it stays valid if the
            // repository root ever moves, and never leaks absolute paths.
            return $"{RelativeDirectory}/{fileName}";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to save snapshot frame");
            return null;
        }
    }

    public int DeleteOlderThan(TimeSpan maxAge, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (maxAge <= TimeSpan.Zero)
            return 0;

        var cutoff = DateTime.UtcNow - maxAge;
        var deleted = 0;

        try
        {
            if (!Directory.Exists(DirectoryPath))
                return 0;

            foreach (var file in Directory.EnumerateFiles(DirectoryPath, "*.jpg"))
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    if (File.GetLastWriteTimeUtc(file) >= cutoff)
                        continue;

                    File.Delete(file);
                    deleted++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _logger?.LogWarning(ex, "Could not delete expired snapshot {File}", Path.GetFileName(file));
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Snapshot retention sweep failed");
        }

        if (deleted > 0)
            _logger?.LogInformation("Snapshot retention removed {Count} expired file(s)", deleted);

        return deleted;
    }

    public bool TryDelete(string? relativePath, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (string.IsNullOrWhiteSpace(relativePath))
            return false;

        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Stored paths look like "data/events/unknown_….jpg". Strip our own
            // prefix and refuse everything else (rooted paths, traversal, UNC,
            // URLs): only files directly inside the snapshot directory can go.
            var normalized = relativePath.Replace('\\', '/').TrimStart('/');
            const string prefix = RelativeDirectory + "/";

            if (!normalized.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                _logger?.LogWarning("Refused snapshot delete outside the snapshot directory");
                return false;
            }

            var fileName = normalized[prefix.Length..];
            if (fileName.Length == 0 || fileName.Contains('/') || fileName.Contains(".."))
                return false;

            var fullPath = Path.Combine(DirectoryPath, fileName);
            if (!File.Exists(fullPath))
                return false;

            File.Delete(fullPath);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to delete snapshot {Path}", relativePath);
            return false;
        }
    }

    /// <summary>8 random hex characters — enough to make collisions irrelevant.</summary>
    private static string RandomSuffix()
    {
        Span<byte> bytes = stackalloc byte[4];
        RandomNumberGenerator.Fill(bytes);

        var builder = new StringBuilder(8);
        foreach (var b in bytes)
            builder.Append(b.ToString("x2"));

        return builder.ToString();
    }

    public void Dispose() => _disposed = true;
}
