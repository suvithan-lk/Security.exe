using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Security.Core.Entities;

namespace Security.Data.Configurations;

public class UserProfileConfiguration : IEntityTypeConfiguration<UserProfile>
{
    public void Configure(EntityTypeBuilder<UserProfile> builder)
    {
        builder.ToTable("UserProfiles");

        builder.HasKey(x => x.Id);

        builder.Property(x => x.DisplayName)
            .HasMaxLength(64)
            .IsRequired();

        builder.Property(x => x.IsActive)
            .IsRequired();

        builder.HasIndex(x => x.IsActive);

        builder.HasOne(x => x.FaceEmbedding)
            .WithOne(x => x.UserProfile)
            .HasForeignKey<FaceEmbedding>(x => x.UserProfileId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

public class FaceEmbeddingConfiguration : IEntityTypeConfiguration<FaceEmbedding>
{
    public void Configure(EntityTypeBuilder<FaceEmbedding> builder)
    {
        builder.ToTable("FaceEmbeddings");

        builder.HasKey(x => x.Id);

        // Encrypted payload — stored as text, never logged.
        builder.Property(x => x.EmbeddingData)
            .IsRequired();

        builder.Property(x => x.ModelVersion)
            .HasMaxLength(64)
            .IsRequired();

        builder.Property(x => x.SampleCount)
            .IsRequired();

        builder.HasIndex(x => x.UserProfileId).IsUnique();
    }
}

public class SecurityEventConfiguration : IEntityTypeConfiguration<SecurityEvent>
{
    public void Configure(EntityTypeBuilder<SecurityEvent> builder)
    {
        builder.ToTable("SecurityEvents");

        builder.HasKey(x => x.Id);

        builder.Property(x => x.EventType)
            .HasConversion<string>()
            .HasMaxLength(48);

        builder.Property(x => x.Result)
            .HasConversion<string>()
            .HasMaxLength(24);

        builder.Property(x => x.Description)
            .HasMaxLength(512)
            .IsRequired();

        // Phase 3: nullable additions — pre-Phase-3 rows carry NULL rather
        // than a guessed value. Both are metadata only (a state name and a
        // local relative file path); no credentials, no embeddings.
        builder.Property(x => x.SessionState)
            .HasConversion<string>()
            .HasMaxLength(24);

        builder.Property(x => x.SnapshotPath)
            .HasMaxLength(260);

        builder.Property(x => x.Timestamp);

        builder.HasIndex(x => x.Timestamp);
        builder.HasIndex(x => x.EventType);
    }
}

public class ApplicationSettingConfiguration : IEntityTypeConfiguration<ApplicationSetting>
{
    public void Configure(EntityTypeBuilder<ApplicationSetting> builder)
    {
        builder.ToTable("ApplicationSettings");

        builder.HasKey(x => x.Id);

        builder.Property(x => x.Key)
            .HasMaxLength(128)
            .IsRequired();

        builder.Property(x => x.Value)
            .HasMaxLength(2048)
            .IsRequired();

        builder.HasIndex(x => x.Key).IsUnique();
    }
}
