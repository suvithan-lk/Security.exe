using Security.Core.Paths;
using Xunit;

namespace Security.Core.Tests;

/// <summary>
/// The repository root drives where the database, logs, and ONNX models are
/// written, so it has to be found no matter which directory the process was
/// launched from (dotnet run, test host, shortcut, file dialog...).
/// </summary>
public class AppPathsTests
{
    [Fact]
    public void Repository_root_is_found_by_walking_up_from_the_executable()
    {
        var root = AppPaths.RepositoryRootDirectory;

        Assert.NotNull(root);
        Assert.True(
            File.Exists(Path.Combine(root!, "Security.slnx")) || File.Exists(Path.Combine(root!, "Security.sln")),
            $"Expected a solution file under '{root}'.");
        Assert.True(Directory.Exists(Path.Combine(root!, "src")));
    }

    [Fact]
    public void ResolveDirectory_returns_an_existing_folder_under_the_repository_root()
    {
        var root = AppPaths.RepositoryRootDirectory;
        Assert.NotNull(root);

        var dir = AppPaths.ResolveDirectory("data");

        Assert.True(Directory.Exists(dir));
        Assert.StartsWith(Path.GetFullPath(root!), Path.GetFullPath(dir), StringComparison.OrdinalIgnoreCase);
        Assert.Equal("data", new DirectoryInfo(dir).Name);
    }

    [Fact]
    public void ResolveDirectory_creates_missing_folders()
    {
        // Deliberately scratch: removed again so the test leaves no trace.
        var name = "app-paths-scratch-" + Guid.NewGuid().ToString("N")[..8];
        var dir = AppPaths.ResolveDirectory(name);

        try
        {
            Assert.True(Directory.Exists(dir));
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void ResolveDirectory_rejects_blank_names()
    {
        Assert.ThrowsAny<ArgumentException>(() => AppPaths.ResolveDirectory(null!));
        Assert.ThrowsAny<ArgumentException>(() => AppPaths.ResolveDirectory(""));
        Assert.ThrowsAny<ArgumentException>(() => AppPaths.ResolveDirectory("   "));
    }

    [Fact]
    public void ResolvePath_keeps_absolute_paths_untouched()
    {
        var absolute = Path.Combine(Path.GetTempPath(), "models", "x.onnx");

        Assert.Equal(Path.GetFullPath(absolute), AppPaths.ResolvePath(absolute));
    }

    [Fact]
    public void ResolvePath_resolves_relative_paths_against_the_repository_root()
    {
        var root = AppPaths.RepositoryRootDirectory;
        Assert.NotNull(root);

        // The working directory is deliberately not the repository root here;
        // a working-directory-based resolver would produce a different answer.
        var resolved = AppPaths.ResolvePath(Path.Combine("models", "file.onnx"));

        Assert.Equal(
            Path.GetFullPath(Path.Combine(root!, "models", "file.onnx")),
            resolved);
    }

    [Fact]
    public void ResolvePath_rejects_blank_input()
    {
        Assert.ThrowsAny<ArgumentException>(() => AppPaths.ResolvePath(null!));
        Assert.ThrowsAny<ArgumentException>(() => AppPaths.ResolvePath(""));
    }
}
