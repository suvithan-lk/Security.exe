using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Security.Core.Paths;

namespace Security.Data.Database;

/// <summary>
/// Design-time factory so `dotnet ef migrations` can build the model without
/// running the application. The database lives in <c>data/security.db</c>
/// relative to the repository root.
/// </summary>
public class SecurityDbContextFactory : IDesignTimeDbContextFactory<SecurityDbContext>
{
    public SecurityDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<SecurityDbContext>()
            .UseSqlite($"Data Source={DatabasePathResolver.ResolveDefaultDatabasePath()}")
            .Options;

        return new SecurityDbContext(options);
    }
}

/// <summary>
/// Resolves where security.db lives. Prefer the directory supplied by
/// configuration; otherwise use <c>{baseDirectory}/data</c>.
/// </summary>
public static class DatabasePathResolver
{
    public const string DefaultFileName = "security.db";

    /// <summary>Optional override set by the application at startup.</summary>
    public static string? OverridePath { get; set; }

    public static string ResolveDefaultDatabasePath()
    {
        if (!string.IsNullOrWhiteSpace(OverridePath))
            return OverridePath;

        // Prefer the repository's data/ folder (see AppPaths); fall back to the
        // executable directory so a published build still has somewhere to write.
        var dataDir = AppPaths.ResolveDirectory("data");
        return Path.Combine(dataDir, DefaultFileName);
    }
}
