namespace Security.Core.Paths;

/// <summary>
/// Resolves the directories the application writes to.
///
/// Everything the app persists (database, logs, ONNX models) must land in the
/// repository's <c>data/</c>, <c>logs/</c>, and <c>models/</c> folders, which
/// is what the documentation and .gitignore describe. Resolving relative to the
/// process working directory would be wrong: a WPF app inherits its working
/// directory from whatever launched it (shortcut, file dialog, `dotnet run`),
/// so the folders would silently scatter.
///
/// Strategy: walk up from the executable looking for the solution file. If it
/// isn't found (e.g. a published, self-contained build), fall back to the
/// executable's own directory, which is always valid.
/// </summary>
public static class AppPaths
{
    private static readonly string[] SolutionFileNames = ["Security.slnx", "Security.sln"];

    private static readonly Lazy<string?> RepositoryRoot = new(FindRepositoryRoot, isThreadSafe: true);

    /// <summary>
    /// Repository root (the directory holding Security.slnx), or null when the
    /// app is running from a location with no solution above it.
    /// </summary>
    public static string? RepositoryRootDirectory => RepositoryRoot.Value;

    /// <summary>
    /// Create (if needed) and return <c>{root}/{name}</c>, where root is the
    /// repository when available and the executable directory otherwise.
    /// </summary>
    public static string ResolveDirectory(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var root = RepositoryRootDirectory ?? AppContext.BaseDirectory;
        var directory = Path.Combine(root, name);

        try
        {
            Directory.CreateDirectory(directory);
        }
        catch (Exception)
        {
            // Read-only install locations are handled by the caller: it will
            // fail with a useful error when it actually tries to write.
        }

        return directory;
    }

    /// <summary>
    /// Resolve a path that may be absolute (returned as-is) or relative
    /// (resolved against the repository root, falling back to the working
    /// directory).
    /// </summary>
    public static string ResolvePath(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        if (Path.IsPathRooted(path))
            return Path.GetFullPath(path);

        var root = RepositoryRootDirectory ?? AppContext.BaseDirectory;
        return Path.GetFullPath(Path.Combine(root, path));
    }

    private static string? FindRepositoryRoot()
    {
        // Probe both the executable directory and the working directory: when
        // running under `dotnet run` from the repo, either one gets us there.
        var starts = new[]
        {
            AppContext.BaseDirectory,
            SafeCurrentDirectory(),
        };

        foreach (var start in starts.Where(s => !string.IsNullOrWhiteSpace(s)))
        {
            try
            {
                var dir = new DirectoryInfo(start);
                while (dir is not null)
                {
                    foreach (var solution in SolutionFileNames)
                    {
                        if (File.Exists(Path.Combine(dir.FullName, solution)))
                            return dir.FullName;
                    }

                    dir = dir.Parent;
                }
            }
            catch (Exception)
            {
                // Unreadable ancestor (permissions, reparse point) — try the next start.
            }
        }

        return null;
    }

    private static string SafeCurrentDirectory()
    {
        try
        {
            return Directory.GetCurrentDirectory();
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }
}
