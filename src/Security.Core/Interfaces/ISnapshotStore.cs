using OpenCvSharp;

namespace Security.Core.Interfaces;

/// <summary>
/// Persists single-frame camera snapshots for unknown-face events.
///
/// PRIVACY CONTRACT:
///  - snapshots are JPEG frames from the application's own camera stream only
///    (never the screen, never the lock screen);
///  - files live under the local <c>data/events/</c> directory — never a URL,
///    never a network share;
///  - filenames are generated (timestamp + random suffix) — never derived from
///    user input;
///  - nothing here uploads or transmits; the store is local-disk only.
/// </summary>
public interface ISnapshotStore
{
    /// <summary>Absolute directory the snapshots are written to (<c>data/events</c>).</summary>
    string DirectoryPath { get; }

    /// <summary>
    /// Encode <paramref name="frame"/> as JPEG and write it to the snapshot
    /// directory. Returns the RELATIVE path (<c>data/events/…</c>) to store on
    /// the event, or null when the frame could not be written — snapshot
    /// failure must never break the recognition pipeline.
    /// </summary>
    string? Save(Mat frame, CancellationToken cancellationToken = default);

    /// <summary>Delete snapshot files older than <paramref name="maxAge"/>.
    /// Returns the number of files removed.</summary>
    int DeleteOlderThan(TimeSpan maxAge, CancellationToken cancellationToken = default);

    /// <summary>Delete one snapshot by its stored relative path. Safe on missing files.</summary>
    bool TryDelete(string? relativePath, CancellationToken cancellationToken = default);
}
