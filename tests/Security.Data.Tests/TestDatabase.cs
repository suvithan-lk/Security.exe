using Microsoft.EntityFrameworkCore;
using Security.Data.Database;

namespace Security.Data.Tests;

/// <summary>
/// A throwaway SQLite database file per test, so repository tests exercise the
/// real SQL provider (including relational constraints) without touching the
/// application's data/security.db.
/// </summary>
public sealed class TestDatabase : IDisposable
{
    private bool _disposed;

    public TestDatabase()
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"security-tests-{Guid.NewGuid():N}.db");

        var options = new DbContextOptionsBuilder<SecurityDbContext>()
            .UseSqlite($"Data Source={Path}")
            .Options;

        Factory = new SimpleFactory(options);

        using var db = Factory.CreateDbContext();
        db.Database.EnsureCreated();
    }

    /// <summary>
    /// Minimal IDbContextFactory — DbContextFactory&lt;T&gt; is internal to EF,
    /// and pooling would hold the file open during cleanup.
    /// </summary>
    private sealed class SimpleFactory : IDbContextFactory<SecurityDbContext>
    {
        private readonly DbContextOptions<SecurityDbContext> _options;

        public SimpleFactory(DbContextOptions<SecurityDbContext> options) => _options = options;

        public SecurityDbContext CreateDbContext() => new(_options);
    }

    public string Path { get; }

    public IDbContextFactory<SecurityDbContext> Factory { get; }

    public SecurityDbContext CreateContext() => Factory.CreateDbContext();

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        // SQLite keeps the file handle inside any live connection pools.
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        try
        {
            if (File.Exists(Path))
                File.Delete(Path);
        }
        catch (IOException)
        {
            // Best-effort cleanup of a temp file.
        }
    }
}
