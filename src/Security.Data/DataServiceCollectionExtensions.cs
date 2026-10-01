using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Security.Core.Interfaces;
using Security.Data.Database;
using Security.Data.Repositories;

namespace Security.Data;

public static class DataServiceCollectionExtensions
{
    /// <summary>
    /// Register the SQLite database, context factory, and repositories.
    /// <paramref name="databasePath"/> may be null to use the default
    /// <c>data/security.db</c> in the repository root.
    /// </summary>
    public static IServiceCollection AddSecurityData(
        this IServiceCollection services,
        string? databasePath = null)
    {
        var resolvedPath = string.IsNullOrWhiteSpace(databasePath)
            ? DatabasePathResolver.ResolveDefaultDatabasePath()
            : databasePath;

        DatabasePathResolver.OverridePath = resolvedPath;

        services.AddDbContextFactory<SecurityDbContext>(options =>
            options.UseSqlite($"Data Source={resolvedPath}"));

        services.AddSingleton<IUserProfileRepository, UserProfileRepository>();
        services.AddSingleton<IFaceEmbeddingRepository, FaceEmbeddingRepository>();
        services.AddSingleton<ISecurityEventRepository, SecurityEventRepository>();
        services.AddSingleton<IApplicationSettingRepository, ApplicationSettingRepository>();

        return services;
    }

    /// <summary>
    /// Create the database file and apply pending migrations.
    /// Safe to call at startup; logs (never throws) if the store is unavailable.
    /// </summary>
    public static async Task EnsureSecurityDatabaseAsync(this IServiceProvider serviceProvider, CancellationToken cancellationToken = default)
    {
        var factory = serviceProvider.GetRequiredService<IDbContextFactory<SecurityDbContext>>();
        var logger = serviceProvider.GetService<ILogger<SecurityDbContext>>();

        try
        {
            await using var db = await factory.CreateDbContextAsync(cancellationToken);

            // EnsureCreated() and Migrate() must never run together: EnsureCreated
            // builds the schema straight from the current model, and Migrate then
            // re-issues the same CREATE TABLE statements from the migration and
            // fails with "table already exists". Pick exactly one strategy.
            var hasMigrations = db.Database.GetMigrations().Any();
            var created = false;

            if (hasMigrations)
            {
                var pending = (await db.Database.GetPendingMigrationsAsync(cancellationToken)).ToList();
                if (pending.Count > 0)
                {
                    await db.Database.MigrateAsync(cancellationToken);
                    created = true;
                }
            }
            else
            {
                // No migrations shipped (source-built variant) — fall back to
                // creating the schema directly from the model.
                created = await db.Database.EnsureCreatedAsync(cancellationToken);
            }

            logger?.LogInformation("Database ready at {Path} (created={Created})",
                DatabasePathResolver.OverridePath ?? "data/security.db", created);
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "Failed to initialize the local database");
        }
    }
}
