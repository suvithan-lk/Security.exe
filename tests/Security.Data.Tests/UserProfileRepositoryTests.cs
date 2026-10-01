using Security.Core.Entities;
using Security.Data.Repositories;
using Xunit;

namespace Security.Data.Tests;

public class UserProfileRepositoryTests : IDisposable
{
    private readonly TestDatabase _db = new();
    private readonly UserProfileRepository _profiles;

    public UserProfileRepositoryTests()
        => _profiles = new UserProfileRepository(_db.Factory);

    public void Dispose() => _db.Dispose();

    private static UserProfile NewProfile(string name = "Default User") => new()
    {
        DisplayName = name,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow,
        IsActive = true,
    };

    [Fact]
    public async Task Add_assigns_an_id_and_persists()
    {
        var added = await _profiles.AddAsync(NewProfile("Ada"));

        Assert.True(added.Id > 0);

        var loaded = await _profiles.GetByIdAsync(added.Id);
        Assert.NotNull(loaded);
        Assert.Equal("Ada", loaded!.DisplayName);
    }

    [Fact]
    public async Task HasActiveProfile_is_false_until_one_exists()
    {
        Assert.False(await _profiles.HasActiveProfileAsync());

        await _profiles.AddAsync(NewProfile());
        Assert.True(await _profiles.HasActiveProfileAsync());
    }

    [Fact]
    public async Task GetActive_returns_the_most_recently_updated_active_profile()
    {
        var older = await _profiles.AddAsync(NewProfile("Older"));
        await Task.Delay(20);
        var newer = await _profiles.AddAsync(NewProfile("Newer"));

        var active = await _profiles.GetActiveAsync();

        Assert.NotNull(active);
        Assert.Equal(newer.Id, active!.Id);
        Assert.NotEqual(older.Id, active.Id);
    }

    [Fact]
    public async Task Inactive_profiles_are_ignored_by_GetActive()
    {
        var profile = await _profiles.AddAsync(NewProfile());
        profile.IsActive = false;
        await _profiles.UpdateAsync(profile);

        Assert.Null(await _profiles.GetActiveAsync());
        Assert.False(await _profiles.HasActiveProfileAsync());
    }

    [Fact]
    public async Task Update_touches_UpdatedAt_and_saves_new_values()
    {
        var profile = await _profiles.AddAsync(NewProfile());
        var original = profile.UpdatedAt;
        await Task.Delay(20);

        profile.DisplayName = "Renamed";
        await _profiles.UpdateAsync(profile);

        var loaded = await _profiles.GetByIdAsync(profile.Id);
        Assert.Equal("Renamed", loaded!.DisplayName);
        Assert.True(loaded.UpdatedAt >= original);
    }

    [Fact]
    public async Task Delete_removes_the_profile()
    {
        var profile = await _profiles.AddAsync(NewProfile());

        await _profiles.DeleteAsync(profile.Id);

        Assert.Null(await _profiles.GetByIdAsync(profile.Id));
        Assert.False(await _profiles.HasActiveProfileAsync());
    }

    [Fact]
    public async Task Deleting_a_missing_profile_is_a_no_op()
    {
        await _profiles.DeleteAsync(999_999);
        Assert.Null(await _profiles.GetByIdAsync(999_999));
    }

    [Fact]
    public async Task GetActive_includes_the_embedded_template()
    {
        var profile = await _profiles.AddAsync(NewProfile());
        var embeddings = new FaceEmbeddingRepository(_db.Factory);

        await embeddings.AddAsync(new FaceEmbedding
        {
            UserProfileId = profile.Id,
            EmbeddingData = "protected-payload",
            ModelVersion = "sface-2021dec",
            SampleCount = 20,
            CreatedAt = DateTime.UtcNow,
        });

        var active = await _profiles.GetActiveAsync();

        Assert.NotNull(active?.FaceEmbedding);
        Assert.Equal("protected-payload", active!.FaceEmbedding!.EmbeddingData);
        Assert.Equal(20, active.FaceEmbedding.SampleCount);
    }
}

public class FaceEmbeddingRepositoryTests : IDisposable
{
    private readonly TestDatabase _db = new();
    private readonly UserProfileRepository _profiles;
    private readonly FaceEmbeddingRepository _embeddings;

    public FaceEmbeddingRepositoryTests()
    {
        _profiles = new UserProfileRepository(_db.Factory);
        _embeddings = new FaceEmbeddingRepository(_db.Factory);
    }

    public void Dispose() => _db.Dispose();

    private async Task<int> NewProfileIdAsync()
        => (await _profiles.AddAsync(new UserProfile
        {
            DisplayName = "Default User",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            IsActive = true,
        })).Id;

    private static FaceEmbedding NewEmbedding(int profileId, string payload = "cipher", int samples = 20) => new()
    {
        UserProfileId = profileId,
        EmbeddingData = payload,
        ModelVersion = "sface-2021dec",
        SampleCount = samples,
        CreatedAt = DateTime.UtcNow,
    };

    [Fact]
    public async Task Add_then_get_round_trips()
    {
        var profileId = await NewProfileIdAsync();
        await _embeddings.AddAsync(NewEmbedding(profileId, "encrypted-blob", 12));

        var loaded = await _embeddings.GetByProfileIdAsync(profileId);

        Assert.NotNull(loaded);
        Assert.Equal("encrypted-blob", loaded!.EmbeddingData);
        Assert.Equal(12, loaded.SampleCount);
        Assert.Equal("sface-2021dec", loaded.ModelVersion);
    }

    [Fact]
    public async Task Get_returns_null_when_no_template_exists()
    {
        var profileId = await NewProfileIdAsync();
        Assert.Null(await _embeddings.GetByProfileIdAsync(profileId));
    }

    [Fact]
    public async Task Update_replaces_the_existing_template_in_place()
    {
        var profileId = await NewProfileIdAsync();
        var original = await _embeddings.AddAsync(NewEmbedding(profileId, "first", 20));

        var replacement = NewEmbedding(profileId, "second", 14);
        await _embeddings.UpdateAsync(replacement);

        var loaded = await _embeddings.GetByProfileIdAsync(profileId);
        Assert.Equal(original.Id, loaded!.Id);   // no duplicate row
        Assert.Equal("second", loaded.EmbeddingData);
        Assert.Equal(14, loaded.SampleCount);
    }

    [Fact]
    public async Task Delete_removes_only_the_requested_profile_template()
    {
        var keepId = await NewProfileIdAsync();
        var dropId = await NewProfileIdAsync();
        await _embeddings.AddAsync(NewEmbedding(keepId));
        await _embeddings.AddAsync(NewEmbedding(dropId));

        await _embeddings.DeleteByProfileIdAsync(dropId);

        Assert.Null(await _embeddings.GetByProfileIdAsync(dropId));
        Assert.NotNull(await _embeddings.GetByProfileIdAsync(keepId));
    }

    [Fact]
    public async Task Delete_for_unknown_profile_is_a_no_op()
    {
        await _embeddings.DeleteByProfileIdAsync(123456);
    }
}
