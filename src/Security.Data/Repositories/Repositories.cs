using Microsoft.EntityFrameworkCore;
using Security.Core.Entities;
using Security.Core.Interfaces;
using Security.Data.Database;

namespace Security.Data.Repositories;

/// <summary>
/// Repository implementations. Each method creates its own scope-safe context
/// usage via the injected factory, keeping unit-of-work boundaries simple.
/// </summary>
public class RepositoryBase
{
    private readonly IDbContextFactory<SecurityDbContext> _factory;

    protected RepositoryBase(IDbContextFactory<SecurityDbContext> factory)
        => _factory = factory;

    protected async Task<T> UseContextAsync<T>(
        Func<SecurityDbContext, Task<T>> action,
        CancellationToken cancellationToken)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        return await action(db);
    }

    protected async Task UseContextAsync(
        Func<SecurityDbContext, Task> action,
        CancellationToken cancellationToken)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        await action(db);
    }
}

public class UserProfileRepository : RepositoryBase, IUserProfileRepository
{
    public UserProfileRepository(IDbContextFactory<SecurityDbContext> factory)
        : base(factory)
    {
    }

    public Task<UserProfile?> GetActiveAsync(CancellationToken cancellationToken = default)
        => UseContextAsync(db => db.UserProfiles
            .Include(p => p.FaceEmbedding)
            .Where(p => p.IsActive)
            .OrderByDescending(p => p.UpdatedAt)
            .FirstOrDefaultAsync(cancellationToken), cancellationToken);

    public Task<UserProfile?> GetByIdAsync(int id, CancellationToken cancellationToken = default)
        => UseContextAsync(db => db.UserProfiles
            .Include(p => p.FaceEmbedding)
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == id, cancellationToken), cancellationToken);

    public async Task<UserProfile> AddAsync(UserProfile profile, CancellationToken cancellationToken = default)
    {
        await UseContextAsync(async db =>
        {
            db.UserProfiles.Add(profile);
            await db.SaveChangesAsync(cancellationToken);
        }, cancellationToken);

        return profile;
    }

    public async Task UpdateAsync(UserProfile profile, CancellationToken cancellationToken = default)
    {
        profile.UpdatedAt = DateTime.UtcNow;
        await UseContextAsync(async db =>
        {
            db.UserProfiles.Update(profile);
            await db.SaveChangesAsync(cancellationToken);
        }, cancellationToken);
    }

    public async Task DeleteAsync(int id, CancellationToken cancellationToken = default)
    {
        await UseContextAsync(async db =>
        {
            var profile = await db.UserProfiles.FirstOrDefaultAsync(p => p.Id == id, cancellationToken);
            if (profile is null)
                return;

            db.UserProfiles.Remove(profile);
            await db.SaveChangesAsync(cancellationToken);
        }, cancellationToken);
    }

    public Task<bool> HasActiveProfileAsync(CancellationToken cancellationToken = default)
        => UseContextAsync(db => db.UserProfiles.AnyAsync(p => p.IsActive, cancellationToken), cancellationToken);
}

public class FaceEmbeddingRepository : RepositoryBase, IFaceEmbeddingRepository
{
    public FaceEmbeddingRepository(IDbContextFactory<SecurityDbContext> factory)
        : base(factory)
    {
    }

    public Task<FaceEmbedding?> GetByProfileIdAsync(int profileId, CancellationToken cancellationToken = default)
        => UseContextAsync(db => db.FaceEmbeddings
            .AsNoTracking()
            .FirstOrDefaultAsync(e => e.UserProfileId == profileId, cancellationToken), cancellationToken);

    public async Task<FaceEmbedding> AddAsync(FaceEmbedding embedding, CancellationToken cancellationToken = default)
    {
        await UseContextAsync(async db =>
        {
            db.FaceEmbeddings.Add(embedding);
            await db.SaveChangesAsync(cancellationToken);
        }, cancellationToken);

        return embedding;
    }

    public async Task UpdateAsync(FaceEmbedding embedding, CancellationToken cancellationToken = default)
    {
        await UseContextAsync(async db =>
        {
            var existing = await db.FaceEmbeddings
                .FirstOrDefaultAsync(e => e.UserProfileId == embedding.UserProfileId, cancellationToken);

            if (existing is null)
            {
                db.FaceEmbeddings.Add(embedding);
            }
            else
            {
                existing.EmbeddingData = embedding.EmbeddingData;
                existing.ModelVersion = embedding.ModelVersion;
                existing.SampleCount = embedding.SampleCount;
                existing.CreatedAt = DateTime.UtcNow;
            }

            await db.SaveChangesAsync(cancellationToken);
        }, cancellationToken);
    }

    public async Task DeleteByProfileIdAsync(int profileId, CancellationToken cancellationToken = default)
    {
        await UseContextAsync(async db =>
        {
            var items = await db.FaceEmbeddings
                .Where(e => e.UserProfileId == profileId)
                .ToListAsync(cancellationToken);

            if (items.Count > 0)
            {
                db.FaceEmbeddings.RemoveRange(items);
                await db.SaveChangesAsync(cancellationToken);
            }
        }, cancellationToken);
    }
}

public class SecurityEventRepository : RepositoryBase, ISecurityEventRepository
{
    public SecurityEventRepository(IDbContextFactory<SecurityDbContext> factory)
        : base(factory)
    {
    }

    public async Task AddAsync(SecurityEvent securityEvent, CancellationToken cancellationToken = default)
    {
        await UseContextAsync(async db =>
        {
            db.SecurityEvents.Add(securityEvent);
            await db.SaveChangesAsync(cancellationToken);
        }, cancellationToken);
    }

    public Task<IReadOnlyList<SecurityEvent>> GetRecentAsync(int count, CancellationToken cancellationToken = default)
        => UseContextAsync<IReadOnlyList<SecurityEvent>>(async db =>
            await db.SecurityEvents
                .AsNoTracking()
                .OrderByDescending(e => e.Timestamp)
                .ThenByDescending(e => e.Id)
                .Take(Math.Max(1, count))
                .ToListAsync(cancellationToken), cancellationToken);

    public Task<IReadOnlyList<SecurityEvent>> QueryAsync(
        SecurityEventTypeFilter filter,
        int limit,
        CancellationToken cancellationToken = default)
        => UseContextAsync<IReadOnlyList<SecurityEvent>>(async db =>
        {
            var query = db.SecurityEvents.AsNoTracking().AsQueryable();

            if (filter.EventType is not null)
                query = query.Where(e => e.EventType == filter.EventType.Value);

            if (filter.FromUtc is not null)
                query = query.Where(e => e.Timestamp >= filter.FromUtc.Value);

            if (filter.ToUtc is not null)
                query = query.Where(e => e.Timestamp <= filter.ToUtc.Value);

            return await query
                .OrderByDescending(e => e.Timestamp)
                .ThenByDescending(e => e.Id)
                .Take(Math.Max(1, limit))
                .ToListAsync(cancellationToken);
        }, cancellationToken);

    public Task<int> CountAsync(CancellationToken cancellationToken = default)
        => UseContextAsync(db => db.SecurityEvents.CountAsync(cancellationToken), cancellationToken);

    public async Task<IReadOnlyList<SecurityEvent>> DeleteOlderThanAsync(
        DateTime cutoffUtc,
        CancellationToken cancellationToken = default)
    {
        return await UseContextAsync<IReadOnlyList<SecurityEvent>>(async db =>
        {
            var expired = await db.SecurityEvents
                .Where(e => e.Timestamp < cutoffUtc)
                .ToListAsync(cancellationToken);

            if (expired.Count == 0)
                return (IReadOnlyList<SecurityEvent>)Array.Empty<SecurityEvent>();

            db.SecurityEvents.RemoveRange(expired);
            await db.SaveChangesAsync(cancellationToken);
            return expired;
        }, cancellationToken);
    }

    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        await UseContextAsync(async db =>
        {
            var all = await db.SecurityEvents.ToListAsync(cancellationToken);
            db.SecurityEvents.RemoveRange(all);
            await db.SaveChangesAsync(cancellationToken);
        }, cancellationToken);
    }
}

public class ApplicationSettingRepository : RepositoryBase, IApplicationSettingRepository
{
    public ApplicationSettingRepository(IDbContextFactory<SecurityDbContext> factory)
        : base(factory)
    {
    }

    public Task<string?> GetAsync(string key, CancellationToken cancellationToken = default)
        => UseContextAsync(db => db.ApplicationSettings
            .AsNoTracking()
            .Where(s => s.Key == key)
            .Select(s => s.Value)
            .FirstOrDefaultAsync(cancellationToken), cancellationToken);

    public async Task SetAsync(string key, string value, CancellationToken cancellationToken = default)
    {
        await UseContextAsync(async db =>
        {
            var existing = await db.ApplicationSettings
                .FirstOrDefaultAsync(s => s.Key == key, cancellationToken);

            if (existing is null)
            {
                db.ApplicationSettings.Add(new ApplicationSetting
                {
                    Key = key,
                    Value = value,
                    UpdatedAt = DateTime.UtcNow,
                });
            }
            else
            {
                existing.Value = value;
                existing.UpdatedAt = DateTime.UtcNow;
            }

            await db.SaveChangesAsync(cancellationToken);
        }, cancellationToken);
    }

    public async Task<IReadOnlyDictionary<string, string>> GetAllAsync(CancellationToken cancellationToken = default)
        => await UseContextAsync<IReadOnlyDictionary<string, string>>(async db =>
        {
            var list = await db.ApplicationSettings
                .AsNoTracking()
                .ToListAsync(cancellationToken);

            return list.ToDictionary(s => s.Key, s => s.Value);
        }, cancellationToken);
}
