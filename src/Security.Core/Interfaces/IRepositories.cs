using Security.Core.Entities;

namespace Security.Core.Interfaces;

public interface IUserProfileRepository
{
    Task<UserProfile?> GetActiveAsync(CancellationToken cancellationToken = default);

    Task<UserProfile?> GetByIdAsync(int id, CancellationToken cancellationToken = default);

    Task<UserProfile> AddAsync(UserProfile profile, CancellationToken cancellationToken = default);

    Task UpdateAsync(UserProfile profile, CancellationToken cancellationToken = default);

    Task DeleteAsync(int id, CancellationToken cancellationToken = default);

    Task<bool> HasActiveProfileAsync(CancellationToken cancellationToken = default);
}

public interface IFaceEmbeddingRepository
{
    Task<FaceEmbedding?> GetByProfileIdAsync(int profileId, CancellationToken cancellationToken = default);

    Task<FaceEmbedding> AddAsync(FaceEmbedding embedding, CancellationToken cancellationToken = default);

    Task UpdateAsync(FaceEmbedding embedding, CancellationToken cancellationToken = default);

    Task DeleteByProfileIdAsync(int profileId, CancellationToken cancellationToken = default);
}

public interface ISecurityEventRepository
{
    Task AddAsync(SecurityEvent securityEvent, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SecurityEvent>> GetRecentAsync(int count, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SecurityEvent>> QueryAsync(
        SecurityEventTypeFilter filter,
        int limit,
        CancellationToken cancellationToken = default);

    Task<int> CountAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Delete events strictly older than <paramref name="cutoffUtc"/> and
    /// return the removed rows (so retention can also clean up their
    /// snapshot files). Retention uses this — never a full table clear —
    /// so live events are untouched.
    /// </summary>
    Task<IReadOnlyList<SecurityEvent>> DeleteOlderThanAsync(DateTime cutoffUtc, CancellationToken cancellationToken = default);

    Task ClearAsync(CancellationToken cancellationToken = default);
}

/// <summary>Plain filter object so repositories do not depend on UI types.</summary>
public sealed class SecurityEventTypeFilter
{
    public Core.Enums.SecurityEventType? EventType { get; init; }

    public DateTime? FromUtc { get; init; }

    public DateTime? ToUtc { get; init; }
}

public interface IApplicationSettingRepository
{
    Task<string?> GetAsync(string key, CancellationToken cancellationToken = default);

    Task SetAsync(string key, string value, CancellationToken cancellationToken = default);

    Task<IReadOnlyDictionary<string, string>> GetAllAsync(CancellationToken cancellationToken = default);
}
