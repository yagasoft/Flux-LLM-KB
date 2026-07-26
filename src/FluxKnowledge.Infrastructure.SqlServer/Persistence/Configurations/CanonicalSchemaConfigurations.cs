using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence.Configurations;

internal static class SchemaConfiguration
{
    public const string Sha256Check =
        "LEN([ContentHash]) = 64 AND [ContentHash] COLLATE Latin1_General_100_BIN2 NOT LIKE '%[^0-9a-f]%'";

    public static void ConfigureHash(PropertyBuilder<string> property) =>
        property.HasMaxLength(64).IsUnicode(false).IsFixedLength().IsRequired();

    public static void ConfigureRowVersion(PropertyBuilder<byte[]> property) =>
        property.IsRowVersion().IsConcurrencyToken();
}

public sealed class SourceIdentityConfiguration : IEntityTypeConfiguration<SourceIdentityEntity>
{
    public void Configure(EntityTypeBuilder<SourceIdentityEntity> builder)
    {
        builder.ToTable("SourceIdentities");
        builder.HasKey(entity => entity.Id);
        builder.Property(entity => entity.Id).ValueGeneratedNever();
        builder.Property(entity => entity.SourceKind).HasMaxLength(64).IsRequired();
        builder.Property(entity => entity.StableKey).HasMaxLength(768).IsRequired();
        builder.Property(entity => entity.CreatedAtUtc).HasColumnType("datetimeoffset(7)");
        builder.HasIndex(entity => new { entity.SourceKind, entity.StableKey }).IsUnique();
    }
}

public sealed class PipelineRecordConfiguration : IEntityTypeConfiguration<PipelineRecordEntity>
{
    public void Configure(EntityTypeBuilder<PipelineRecordEntity> builder)
    {
        builder.ToTable(
            "PipelineRecords",
            table => table.HasCheckConstraint("CK_PipelineRecords_ContentHash", SchemaConfiguration.Sha256Check));
        builder.HasKey(entity => entity.Id);
        builder.Property(entity => entity.Id).ValueGeneratedNever();
        SchemaConfiguration.ConfigureHash(builder.Property(entity => entity.ContentHash));
        builder.Property(entity => entity.RegisteredAtUtc).HasColumnType("datetimeoffset(7)");
        SchemaConfiguration.ConfigureRowVersion(builder.Property(entity => entity.RowVersion));
        builder.HasIndex(entity => new { entity.SourceIdentityId, entity.Revision }).IsUnique();
        builder.HasOne(entity => entity.SourceIdentity)
            .WithMany()
            .HasForeignKey(entity => entity.SourceIdentityId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<PipelineRecordEntity>()
            .WithMany()
            .HasForeignKey(entity => entity.RootLineageRecordId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(entity => entity.ParentRevisionRecord)
            .WithMany()
            .HasForeignKey(entity => entity.ParentRevisionRecordId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class JobConfiguration : IEntityTypeConfiguration<JobEntity>
{
    public void Configure(EntityTypeBuilder<JobEntity> builder)
    {
        builder.ToTable("Jobs");
        builder.HasKey(entity => entity.Id);
        builder.Property(entity => entity.Id).ValueGeneratedNever();
        builder.Property(entity => entity.Operation).HasMaxLength(128).IsRequired();
        builder.Property(entity => entity.DueAtUtc).HasColumnType("datetimeoffset(7)");
        builder.Property(entity => entity.LeaseExpiresAtUtc).HasColumnType("datetimeoffset(7)");
        builder.Property(entity => entity.LeaseOwner).HasMaxLength(256);
        builder.Property(entity => entity.Reason).HasMaxLength(512);
        builder.Property(entity => entity.ErrorDetails).HasMaxLength(4000);
        SchemaConfiguration.ConfigureRowVersion(builder.Property(entity => entity.RowVersion));
        builder.HasIndex(entity => new { entity.PublicState, entity.DueAtUtc });
        builder.HasOne(entity => entity.PipelineRecord)
            .WithMany()
            .HasForeignKey(entity => entity.PipelineRecordId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class JobAttemptConfiguration : IEntityTypeConfiguration<JobAttemptEntity>
{
    public void Configure(EntityTypeBuilder<JobAttemptEntity> builder)
    {
        builder.ToTable("JobAttempts");
        builder.HasKey(entity => entity.Id);
        builder.Property(entity => entity.Id).UseIdentityColumn();
        builder.Property(entity => entity.LeaseOwner).HasMaxLength(256).IsRequired();
        builder.Property(entity => entity.StartedAtUtc).HasColumnType("datetimeoffset(7)");
        builder.Property(entity => entity.CompletedAtUtc).HasColumnType("datetimeoffset(7)");
        builder.Property(entity => entity.Outcome).HasMaxLength(128);
        builder.Property(entity => entity.ErrorDetails).HasMaxLength(4000);
        builder.HasIndex(entity => new { entity.JobId, entity.AttemptNumber }).IsUnique();
        builder.HasOne(entity => entity.Job)
            .WithMany()
            .HasForeignKey(entity => entity.JobId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class OutboxMessageConfiguration : IEntityTypeConfiguration<OutboxMessageEntity>
{
    public void Configure(EntityTypeBuilder<OutboxMessageEntity> builder)
    {
        builder.ToTable("OutboxMessages");
        builder.HasKey(entity => entity.Id);
        builder.Property(entity => entity.Id).ValueGeneratedNever();
        builder.Property(entity => entity.Operation).HasMaxLength(128).IsRequired();
        builder.Property(entity => entity.IdempotencyKey).HasMaxLength(512).IsRequired();
        builder.Property(entity => entity.LeaseOwner).HasMaxLength(256);
        builder.Property(entity => entity.DueAtUtc).HasColumnType("datetimeoffset(7)");
        builder.Property(entity => entity.CreatedAtUtc).HasColumnType("datetimeoffset(7)");
        builder.Property(entity => entity.DispatchedAtUtc).HasColumnType("datetimeoffset(7)");
        builder.Property(entity => entity.LeaseExpiresAtUtc).HasColumnType("datetimeoffset(7)");
        SchemaConfiguration.ConfigureRowVersion(builder.Property(entity => entity.RowVersion));
        builder.HasIndex(entity => entity.IdempotencyKey).IsUnique();
        builder.HasIndex(entity => new { entity.DispatchedAtUtc, entity.DueAtUtc });
        builder.HasOne(entity => entity.PipelineRecord)
            .WithMany()
            .HasForeignKey(entity => entity.PipelineRecordId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class ArtifactConfiguration : IEntityTypeConfiguration<ArtifactEntity>
{
    public void Configure(EntityTypeBuilder<ArtifactEntity> builder)
    {
        builder.ToTable(
            "Artifacts",
            table => table.HasCheckConstraint("CK_Artifacts_ContentHash", SchemaConfiguration.Sha256Check));
        builder.HasKey(entity => entity.Id).HasName("PK_Artifacts");
        builder.Property(entity => entity.Id).ValueGeneratedNever();
        SchemaConfiguration.ConfigureHash(builder.Property(entity => entity.ContentHash));
        builder.Property(entity => entity.ContentType).HasMaxLength(256).IsRequired();
        builder.Property(entity => entity.SearchText).HasColumnType("nvarchar(max)").IsRequired();
        builder.Property(entity => entity.CreatedAtUtc).HasColumnType("datetimeoffset(7)");
        builder.HasIndex(entity => new { entity.PipelineRecordId, entity.SourceRevision, entity.Stage }).IsUnique();
        builder.HasOne(entity => entity.PipelineRecord)
            .WithMany()
            .HasForeignKey(entity => entity.PipelineRecordId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class TextChunkConfiguration : IEntityTypeConfiguration<TextChunkEntity>
{
    public void Configure(EntityTypeBuilder<TextChunkEntity> builder)
    {
        builder.ToTable(
            "TextChunks",
            table => table.HasCheckConstraint("CK_TextChunks_ContentHash", SchemaConfiguration.Sha256Check));
        builder.HasKey(entity => entity.Id);
        builder.Property(entity => entity.Id).UseIdentityColumn();
        SchemaConfiguration.ConfigureHash(builder.Property(entity => entity.ContentHash));
        builder.Property(entity => entity.Content).HasColumnType("nvarchar(max)").IsRequired();
        builder.HasIndex(entity => new { entity.ArtifactId, entity.Ordinal }).IsUnique();
        builder.HasOne(entity => entity.Artifact)
            .WithMany()
            .HasForeignKey(entity => entity.ArtifactId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class IndexGenerationConfiguration : IEntityTypeConfiguration<IndexGenerationEntity>
{
    public void Configure(EntityTypeBuilder<IndexGenerationEntity> builder)
    {
        builder.ToTable("IndexGenerations");
        builder.HasKey(entity => entity.Id);
        builder.Property(entity => entity.Id).ValueGeneratedNever();
        builder.Property(entity => entity.ModelFingerprint).HasMaxLength(256).IsRequired();
        builder.Property(entity => entity.IndexPath).HasMaxLength(2048).IsRequired();
        builder.Property(entity => entity.MetadataChecksum).HasMaxLength(64).IsUnicode(false).IsFixedLength().IsRequired();
        builder.Property(entity => entity.CreatedAtUtc).HasColumnType("datetimeoffset(7)");
        builder.Property(entity => entity.ValidatedAtUtc).HasColumnType("datetimeoffset(7)");
        SchemaConfiguration.ConfigureRowVersion(builder.Property(entity => entity.RowVersion));
    }
}

public sealed class VectorConfiguration : IEntityTypeConfiguration<VectorEntity>
{
    public void Configure(EntityTypeBuilder<VectorEntity> builder)
    {
        builder.ToTable(
            "Vectors",
            table => table.HasCheckConstraint("CK_Vectors_ContentHash", SchemaConfiguration.Sha256Check));
        builder.HasKey(entity => entity.VectorId);
        builder.Property(entity => entity.VectorId).UseIdentityColumn();
        builder.Property(entity => entity.ModelFingerprint).HasMaxLength(256).IsRequired();
        builder.Property(entity => entity.Values).HasColumnType("varbinary(max)").IsRequired();
        SchemaConfiguration.ConfigureHash(builder.Property(entity => entity.ContentHash));
        builder.Property(entity => entity.CreatedAtUtc).HasColumnType("datetimeoffset(7)");
        SchemaConfiguration.ConfigureRowVersion(builder.Property(entity => entity.RowVersion));
        builder.HasIndex(entity => new
        {
            entity.TextChunkId,
            entity.ModelFingerprint,
            entity.SourceRevision,
            entity.IndexGenerationId
        }).IsUnique();
        builder.HasOne(entity => entity.TextChunk)
            .WithMany()
            .HasForeignKey(entity => entity.TextChunkId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(entity => entity.IndexGeneration)
            .WithMany()
            .HasForeignKey(entity => entity.IndexGenerationId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class IndexStateConfiguration : IEntityTypeConfiguration<IndexStateEntity>
{
    public void Configure(EntityTypeBuilder<IndexStateEntity> builder)
    {
        builder.ToTable(
            "IndexState",
            table => table.HasCheckConstraint("CK_IndexState_Singleton", "[Id] = 1"));
        builder.HasKey(entity => entity.Id);
        builder.Property(entity => entity.Id).ValueGeneratedNever();
        builder.Property(entity => entity.UpdatedAtUtc).HasColumnType("datetimeoffset(7)");
        SchemaConfiguration.ConfigureRowVersion(builder.Property(entity => entity.RowVersion));
        builder.HasOne(entity => entity.ActiveIndexGeneration)
            .WithMany()
            .HasForeignKey(entity => entity.ActiveIndexGenerationId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasData(new IndexStateEntity { Id = 1, UpdatedAtUtc = DateTimeOffset.UnixEpoch });
    }
}

public sealed class AuditEventConfiguration : IEntityTypeConfiguration<AuditEventEntity>
{
    public void Configure(EntityTypeBuilder<AuditEventEntity> builder)
    {
        builder.ToTable("AuditEvents");
        builder.HasKey(entity => entity.Id);
        builder.Property(entity => entity.Id).UseIdentityColumn();
        builder.Property(entity => entity.EventType).HasMaxLength(256).IsRequired();
        builder.Property(entity => entity.Actor).HasMaxLength(256).IsRequired();
        builder.Property(entity => entity.DetailsJson).HasColumnType("nvarchar(max)").IsRequired();
        builder.Property(entity => entity.OccurredAtUtc).HasColumnType("datetimeoffset(7)");
        builder.HasIndex(entity => new { entity.PipelineRecordId, entity.OccurredAtUtc });
        builder.HasOne(entity => entity.PipelineRecord)
            .WithMany()
            .HasForeignKey(entity => entity.PipelineRecordId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class GpuMiniTaskConfiguration : IEntityTypeConfiguration<GpuMiniTaskEntity>
{
    public void Configure(EntityTypeBuilder<GpuMiniTaskEntity> builder)
    {
        builder.ToTable("GpuMiniTasks");
        builder.HasKey(entity => entity.Id);
        builder.Property(entity => entity.Id).ValueGeneratedNever();
        builder.Property(entity => entity.ModelRuntimeKey).HasMaxLength(256).IsRequired();
        builder.Property(entity => entity.SettingsFingerprint).HasMaxLength(256).IsRequired();
        builder.Property(entity => entity.IdempotencyKey).HasMaxLength(512).IsRequired();
        builder.Property(entity => entity.CreatedAtUtc).HasColumnType("datetimeoffset(7)");
        SchemaConfiguration.ConfigureRowVersion(builder.Property(entity => entity.RowVersion));
        builder.HasIndex(entity => entity.IdempotencyKey).IsUnique();
        builder.HasIndex(entity => new { entity.State, entity.PriorityLane, entity.CreatedAtUtc });
        builder.HasOne(entity => entity.ParentJob)
            .WithMany()
            .HasForeignKey(entity => entity.ParentJobId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
