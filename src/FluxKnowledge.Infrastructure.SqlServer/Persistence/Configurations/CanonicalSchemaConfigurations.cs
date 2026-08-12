using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence.Configurations;

internal static class SchemaConfiguration
{
    public const string SchedulerFenceCollation = "Latin1_General_100_BIN2";
    private const string TrailingWhitespaceCodePoints =
        "9, 10, 11, 12, 13, 32, 133, 160, 5760, 8192, 8193, 8194, 8195, 8196, 8197, 8198, 8199, 8200, 8201, 8202, 8232, 8233, 8239, 8287, 12288";

    public const string Sha256Check =
        "LEN([ContentHash]) = 64 AND [ContentHash] COLLATE Latin1_General_100_BIN2 NOT LIKE '%[^0-9a-f]%'";

    public static string Sha256CheckFor(string columnName) =>
        $"LEN([{columnName}]) = 64 AND [{columnName}] COLLATE Latin1_General_100_BIN2 NOT LIKE '%[^0-9a-f]%'";

    public static string NoTrailingWhitespaceCheckFor(string columnName, bool nullable)
    {
        var canonical =
            $"DATALENGTH([{columnName}]) > 0 AND UNICODE(RIGHT([{columnName}], 1)) NOT IN ({TrailingWhitespaceCodePoints})";
        return nullable ? $"[{columnName}] IS NULL OR ({canonical})" : canonical;
    }

    public static void ConfigureHash(PropertyBuilder<string> property) =>
        property.HasMaxLength(64).IsUnicode(false).IsFixedLength().IsRequired();

    public static void ConfigureRowVersion(PropertyBuilder<byte[]> property) =>
        property.IsRowVersion().IsConcurrencyToken();

    public static void ConfigureImmutableAfterInsert<TProperty>(PropertyBuilder<TProperty> property) =>
        property.Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Throw);

    public static void ConfigureImmutableAfterInsert<TEntity>(EntityTypeBuilder<TEntity> builder)
        where TEntity : class
    {
        foreach (var property in builder.Metadata.GetProperties())
        {
            property.SetAfterSaveBehavior(PropertySaveBehavior.Throw);
        }
    }
}

public sealed class OutlookCaptureProfileConfiguration : IEntityTypeConfiguration<OutlookCaptureProfileEntity>
{
    public void Configure(EntityTypeBuilder<OutlookCaptureProfileEntity> builder)
    {
        builder.ToTable("OutlookCaptureProfiles"); builder.HasKey(x => x.Id); builder.Property(x => x.Id).ValueGeneratedNever();
        builder.Property(x => x.DisplayName).HasMaxLength(256).IsRequired();
        builder.Property(x => x.SpoolRoot).HasMaxLength(2048).IsRequired().UseCollation(SchemaConfiguration.SchedulerFenceCollation);
        builder.Property(x => x.CreatedAtUtc).HasColumnType("datetimeoffset(7)"); builder.Property(x => x.UpdatedAtUtc).HasColumnType("datetimeoffset(7)");
        SchemaConfiguration.ConfigureImmutableAfterInsert(builder.Property(x => x.SourceRootId));
        SchemaConfiguration.ConfigureRowVersion(builder.Property(x => x.RowVersion));
        builder.HasIndex(x => x.SourceRootId).IsUnique();
        builder.HasOne<SourceRootConfigurationEntity>().WithMany().HasForeignKey(x => x.SourceRootId).OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class OutlookCaptureFolderConfiguration : IEntityTypeConfiguration<OutlookCaptureFolderEntity>
{
    public void Configure(EntityTypeBuilder<OutlookCaptureFolderEntity> builder)
    {
        builder.ToTable("OutlookCaptureFolders"); builder.HasKey(x => x.Id); builder.Property(x => x.Id).ValueGeneratedNever();
        builder.Property(x => x.StoreId).HasColumnType("nvarchar(max)").IsRequired().UseCollation(SchemaConfiguration.SchedulerFenceCollation);
        builder.Property(x => x.FolderEntryId).HasColumnType("nvarchar(max)").IsRequired().UseCollation(SchemaConfiguration.SchedulerFenceCollation);
        builder.Property(x => x.CanonicalIdentityFingerprint).HasColumnType("char(64)").IsUnicode(false).IsFixedLength().HasMaxLength(64)
            .HasComputedColumnSql("CONVERT(char(64), HASHBYTES('SHA2_256', CONCAT(CONVERT(nvarchar(20), DATALENGTH([StoreId])), N':', [StoreId], CONVERT(nvarchar(20), DATALENGTH([FolderEntryId])), N':', [FolderEntryId])), 2)", stored: true)
            .UseCollation(SchemaConfiguration.SchedulerFenceCollation);
        builder.Property(x => x.DisplayName).HasMaxLength(256).IsRequired(); builder.Property(x => x.CursorUtc).HasColumnType("datetimeoffset(7)");
        builder.Property(x => x.CursorFingerprint).HasColumnType("char(64)").IsUnicode(false).IsFixedLength().HasMaxLength(64);
        SchemaConfiguration.ConfigureRowVersion(builder.Property(x => x.RowVersion));
        builder.HasIndex(x => new { x.ProfileId, x.CanonicalIdentityFingerprint }).IsUnique();
        builder.HasOne<OutlookCaptureProfileEntity>().WithMany().HasForeignKey(x => x.ProfileId).OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class OutlookCaptureOperationConfiguration : IEntityTypeConfiguration<OutlookCaptureOperationEntity>
{
    public void Configure(EntityTypeBuilder<OutlookCaptureOperationEntity> builder)
    {
        builder.ToTable("OutlookCaptureOperations"); builder.HasKey(x => x.Id); builder.Property(x => x.Id).ValueGeneratedNever();
        builder.Property(x => x.Kind).HasMaxLength(64).IsRequired(); builder.Property(x => x.RequestFingerprint).HasColumnType("char(64)").IsUnicode(false).IsFixedLength().HasMaxLength(64).IsRequired();
        builder.Property(x => x.CompletedAtUtc).HasColumnType("datetimeoffset(7)"); builder.HasIndex(x => x.OperationId).IsUnique();
    }
}

public sealed class OutlookCaptureExportConfiguration : IEntityTypeConfiguration<OutlookCaptureExportEntity>
{
    public void Configure(EntityTypeBuilder<OutlookCaptureExportEntity> builder)
    {
        builder.ToTable("OutlookCaptureExports", table => table.HasCheckConstraint(
            "CK_OutlookCaptureExports_IdentityRequiredUnlessBlocked",
            $"([State] = {(int)FluxKnowledge.Domain.Outlook.OutlookExportState.Blocked} AND [ProfileId] IS NULL AND [FolderId] IS NULL) OR " +
            "([ProfileId] IS NOT NULL AND [FolderId] IS NOT NULL)"));
        builder.HasKey(x => x.Id); builder.Property(x => x.Id).ValueGeneratedNever();
        builder.Property(x => x.EntryId).HasColumnType("nvarchar(max)").IsRequired().UseCollation(SchemaConfiguration.SchedulerFenceCollation);
        builder.Property(x => x.EntryIdFingerprint).HasColumnType("char(64)").IsUnicode(false).IsFixedLength().HasMaxLength(64)
            .HasComputedColumnSql("CONVERT(char(64), HASHBYTES('SHA2_256', [EntryId]), 2)", stored: true)
            .UseCollation(SchemaConfiguration.SchedulerFenceCollation);
        builder.Property(x => x.SourceFingerprint).HasColumnType("char(64)").IsUnicode(false).IsFixedLength().HasMaxLength(64).IsRequired();
        builder.Property(x => x.ManifestHash).HasColumnType("char(64)").IsUnicode(false).IsFixedLength().HasMaxLength(64);
        builder.Property(x => x.RelativeSpoolPath).HasMaxLength(2048).UseCollation(SchemaConfiguration.SchedulerFenceCollation);
        builder.Property(x => x.BlockedReasonCode).HasMaxLength(64).IsUnicode(false).UseCollation(SchemaConfiguration.SchedulerFenceCollation);
        SchemaConfiguration.ConfigureRowVersion(builder.Property(x => x.RowVersion));
        SchemaConfiguration.ConfigureImmutableAfterInsert(builder.Property(x => x.CatchUpId));
        SchemaConfiguration.ConfigureImmutableAfterInsert(builder.Property(x => x.FencingToken));
        builder.HasIndex(x => new { x.FolderId, x.EntryIdFingerprint })
            .HasFilter($"[State] <> {(int)FluxKnowledge.Domain.Outlook.OutlookExportState.Blocked}")
            .IsUnique();
        builder.HasOne<OutlookCaptureProfileEntity>().WithMany().HasForeignKey(x => x.ProfileId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<OutlookCaptureFolderEntity>().WithMany().HasForeignKey(x => x.FolderId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<OutlookCatchUpEntity>().WithMany().HasForeignKey(x => x.CatchUpId).OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class OutlookBrowseRequestConfiguration : IEntityTypeConfiguration<OutlookBrowseRequestEntity>
{
    public void Configure(EntityTypeBuilder<OutlookBrowseRequestEntity> builder)
    {
        builder.ToTable("OutlookBrowseRequests"); builder.HasKey(x => x.Id); builder.Property(x => x.Id).ValueGeneratedNever();
        builder.Property(x => x.ExpiresAtUtc).HasColumnType("datetimeoffset(7)"); builder.Property(x => x.LeaseExpiresAtUtc).HasColumnType("datetimeoffset(7)");
        builder.Property(x => x.LeaseOwner).HasMaxLength(768).UseCollation(SchemaConfiguration.SchedulerFenceCollation); builder.Property(x => x.TargetPath).HasMaxLength(512).UseCollation(SchemaConfiguration.SchedulerFenceCollation); builder.Property(x => x.TargetPathFingerprint).HasMaxLength(64).UseCollation(SchemaConfiguration.SchedulerFenceCollation); SchemaConfiguration.ConfigureRowVersion(builder.Property(x => x.RowVersion));
        builder.HasIndex(x => new { x.State, x.ExpiresAtUtc });
        builder.HasOne<OutlookCaptureProfileEntity>().WithMany().HasForeignKey(x => x.ProfileId).OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class OutlookBrowseResultConfiguration : IEntityTypeConfiguration<OutlookBrowseResultEntity>
{
    public void Configure(EntityTypeBuilder<OutlookBrowseResultEntity> builder)
    {
        builder.ToTable("OutlookBrowseResults"); builder.HasKey(x => x.Id); builder.Property(x => x.Id).ValueGeneratedNever(); builder.Property(x => x.DisplayName).HasMaxLength(256).IsRequired();
        builder.HasIndex(x => new { x.BrowseRequestId, x.FolderId }).IsUnique();
        builder.HasOne<OutlookBrowseRequestEntity>().WithMany().HasForeignKey(x => x.BrowseRequestId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<OutlookCaptureFolderEntity>().WithMany().HasForeignKey(x => x.FolderId).OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class OutlookCatchUpConfiguration : IEntityTypeConfiguration<OutlookCatchUpEntity>
{
    public void Configure(EntityTypeBuilder<OutlookCatchUpEntity> builder)
    {
        builder.ToTable("OutlookCatchUps"); builder.HasKey(x => x.Id); builder.Property(x => x.Id).ValueGeneratedNever();
        builder.Property(x => x.CoalescingKey).HasMaxLength(256).IsRequired().UseCollation(SchemaConfiguration.SchedulerFenceCollation); builder.Property(x => x.Reason).HasMaxLength(1024);
        builder.Property(x => x.NotBeforeUtc).HasColumnType("datetimeoffset(7)"); builder.Property(x => x.LeaseExpiresAtUtc).HasColumnType("datetimeoffset(7)"); builder.Property(x => x.LastHeartbeatAtUtc).HasColumnType("datetimeoffset(7)");
        builder.Property(x => x.LeaseOwner).HasMaxLength(768).UseCollation(SchemaConfiguration.SchedulerFenceCollation); SchemaConfiguration.ConfigureRowVersion(builder.Property(x => x.RowVersion));
        builder.HasIndex(x => new { x.ProfileId, x.CoalescingKey }).HasFilter("[State] IN (0, 1)").IsUnique(); builder.HasIndex(x => new { x.State, x.NotBeforeUtc, x.LeaseExpiresAtUtc });
        builder.HasOne<OutlookCaptureProfileEntity>().WithMany().HasForeignKey(x => x.ProfileId).OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class DeferredCapabilityConfiguration : IEntityTypeConfiguration<DeferredCapabilityEntity>
{
    public void Configure(EntityTypeBuilder<DeferredCapabilityEntity> builder)
    {
        builder.ToTable("DeferredCapabilities"); builder.HasKey(x => x.Id); builder.Property(x => x.Id).ValueGeneratedNever();
        builder.Property(x => x.ArtifactFingerprint).HasColumnType("char(64)").IsUnicode(false).IsFixedLength().HasMaxLength(64).IsRequired()
            .UseCollation(SchemaConfiguration.SchedulerFenceCollation);
        builder.Property(x => x.RequiredCapability).HasMaxLength(256).IsRequired().UseCollation(SchemaConfiguration.SchedulerFenceCollation);
        builder.Property(x => x.Provenance).HasMaxLength(256).IsRequired().UseCollation(SchemaConfiguration.SchedulerFenceCollation);
        builder.Property(x => x.ClaimedProcessorVersion).HasMaxLength(256).UseCollation(SchemaConfiguration.SchedulerFenceCollation); builder.Property(x => x.CreatedAtUtc).HasColumnType("datetimeoffset(7)"); builder.Property(x => x.ClaimedAtUtc).HasColumnType("datetimeoffset(7)"); SchemaConfiguration.ConfigureRowVersion(builder.Property(x => x.RowVersion));
        builder.HasIndex(x => new { x.SourceRevisionId, x.ArtifactFingerprint, x.RequiredCapability }).IsUnique();
        builder.HasOne<SourceRevisionEntity>().WithMany().HasForeignKey(x => x.SourceRevisionId).OnDelete(DeleteBehavior.Restrict);
    }
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
        builder.HasAlternateKey(entity => new { entity.Id, entity.Revision });
        builder.HasIndex(entity => new { entity.SourceIdentityId, entity.Revision }).IsUnique();
        builder.HasIndex(entity => entity.SourceRevisionId).IsUnique().HasFilter("[SourceRevisionId] IS NOT NULL");
        SchemaConfiguration.ConfigureImmutableAfterInsert(builder.Property(entity => entity.SourceRevisionId));
        builder.HasOne(entity => entity.SourceIdentity)
            .WithMany()
            .HasForeignKey(entity => entity.SourceIdentityId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(entity => entity.SourceRevision)
            .WithMany()
            .HasForeignKey(entity => entity.SourceRevisionId)
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
        builder.ToTable(
            "Jobs",
            table =>
            {
                table.HasCheckConstraint(
                    "CK_Jobs_Operation_NoTrailingWhitespace",
                    SchemaConfiguration.NoTrailingWhitespaceCheckFor("Operation", nullable: false));
                table.HasCheckConstraint(
                    "CK_Jobs_LeaseOwner_NoTrailingWhitespace",
                    SchemaConfiguration.NoTrailingWhitespaceCheckFor("LeaseOwner", nullable: true));
            });
        builder.HasKey(entity => entity.Id);
        builder.Property(entity => entity.Id).ValueGeneratedNever();
        builder.Property(entity => entity.Operation)
            .HasMaxLength(128)
            .IsRequired()
            .UseCollation(SchemaConfiguration.SchedulerFenceCollation);
        builder.Property(entity => entity.DueAtUtc).HasColumnType("datetimeoffset(7)");
        builder.Property(entity => entity.LeaseExpiresAtUtc).HasColumnType("datetimeoffset(7)");
        builder.Property(entity => entity.LeaseOwner)
            .HasMaxLength(256)
            .UseCollation(SchemaConfiguration.SchedulerFenceCollation);
        builder.Property(entity => entity.Reason).HasMaxLength(512);
        builder.Property(entity => entity.ErrorDetails).HasMaxLength(4000);
        SchemaConfiguration.ConfigureRowVersion(builder.Property(entity => entity.RowVersion));
        builder.HasAlternateKey(entity => new { entity.Id, entity.SourceRevision });
        builder.HasIndex(entity => new { entity.PublicState, entity.DueAtUtc });
        builder.HasOne(entity => entity.PipelineRecord)
            .WithMany()
            .HasForeignKey(entity => new { entity.PipelineRecordId, entity.SourceRevision })
            .HasPrincipalKey(entity => new { entity.Id, entity.Revision })
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
        SchemaConfiguration.ConfigureRowVersion(builder.Property(entity => entity.RowVersion));
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
            .HasForeignKey(entity => new { entity.PipelineRecordId, entity.SourceRevision })
            .HasPrincipalKey(entity => new { entity.Id, entity.Revision })
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
        builder.HasAlternateKey(entity => new { entity.Id, entity.SourceRevision });
        builder.HasIndex(entity => new { entity.PipelineRecordId, entity.SourceRevision, entity.Stage }).IsUnique();
        builder.HasOne(entity => entity.PipelineRecord)
            .WithMany()
            .HasForeignKey(entity => new { entity.PipelineRecordId, entity.SourceRevision })
            .HasPrincipalKey(entity => new { entity.Id, entity.Revision })
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
        builder.HasAlternateKey(entity => new { entity.Id, entity.SourceRevision });
        builder.HasIndex(entity => new { entity.ArtifactId, entity.Ordinal }).IsUnique();
        builder.HasOne(entity => entity.Artifact)
            .WithMany()
            .HasForeignKey(entity => new { entity.ArtifactId, entity.SourceRevision })
            .HasPrincipalKey(entity => new { entity.Id, entity.SourceRevision })
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
            table =>
            {
                table.HasCheckConstraint(
                    "CK_Vectors_TextChunkContentHash",
                    SchemaConfiguration.Sha256CheckFor(nameof(VectorEntity.TextChunkContentHash)));
                table.HasCheckConstraint(
                    "CK_Vectors_PayloadChecksum",
                    SchemaConfiguration.Sha256CheckFor(nameof(VectorEntity.PayloadChecksum)));
            });
        builder.HasKey(entity => entity.VectorId);
        builder.Property(entity => entity.VectorId).UseIdentityColumn();
        builder.Property(entity => entity.ModelFingerprint).HasMaxLength(256).IsRequired();
        builder.Property(entity => entity.Values).HasColumnType("varbinary(max)").IsRequired();
        SchemaConfiguration.ConfigureHash(builder.Property(entity => entity.TextChunkContentHash));
        SchemaConfiguration.ConfigureHash(builder.Property(entity => entity.PayloadChecksum));
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
            .HasForeignKey(entity => new { entity.TextChunkId, entity.SourceRevision })
            .HasPrincipalKey(entity => new { entity.Id, entity.SourceRevision })
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

public sealed class IndexGenerationVectorConfiguration : IEntityTypeConfiguration<IndexGenerationVectorEntity>
{
    public void Configure(EntityTypeBuilder<IndexGenerationVectorEntity> builder)
    {
        builder.ToTable("IndexGenerationVectors");
        builder.HasKey(entity => new { entity.GenerationId, entity.VectorId });
        builder.HasIndex(entity => entity.VectorId);
        builder.HasOne(entity => entity.Generation)
            .WithMany()
            .HasForeignKey(entity => entity.GenerationId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(entity => entity.Vector)
            .WithMany()
            .HasForeignKey(entity => entity.VectorId)
            .OnDelete(DeleteBehavior.Restrict);
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
        builder.Property(entity => entity.CorrelationId).HasMaxLength(256).UseCollation(SchemaConfiguration.SchedulerFenceCollation);
        builder.Property(entity => entity.EventFamily).HasMaxLength(128);
        builder.Property(entity => entity.Severity).HasMaxLength(64);
        builder.HasIndex(entity => new { entity.PipelineRecordId, entity.OccurredAtUtc });
        builder.HasIndex(entity => new { entity.OccurredAtUtc, entity.Id }).IsDescending();
        builder.HasIndex(entity => new { entity.SourceRootId, entity.OccurredAtUtc }).IsDescending(false, true);
        builder.HasIndex(entity => new { entity.SourceRevisionId, entity.OccurredAtUtc }).IsDescending(false, true);
        builder.HasIndex(entity => entity.CorrelationId);
        builder.HasOne(entity => entity.PipelineRecord)
            .WithMany()
            .HasForeignKey(entity => entity.PipelineRecordId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(entity => entity.SourceRoot)
            .WithMany()
            .HasForeignKey(entity => entity.SourceRootId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(entity => entity.SourceScanRequest)
            .WithMany()
            .HasForeignKey(entity => entity.SourceScanRequestId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(entity => entity.SourceRevision)
            .WithMany()
            .HasForeignKey(entity => entity.SourceRevisionId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(entity => entity.SourceActivity)
            .WithMany()
            .HasForeignKey(entity => entity.SourceActivityId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class GpuMiniTaskConfiguration : IEntityTypeConfiguration<GpuMiniTaskEntity>
{
    public void Configure(EntityTypeBuilder<GpuMiniTaskEntity> builder)
    {
        builder.ToTable(
            "GpuMiniTasks",
            table =>
            {
                table.HasCheckConstraint(
                    "CK_GpuMiniTasks_ModelRuntimeKey_NoTrailingWhitespace",
                    SchemaConfiguration.NoTrailingWhitespaceCheckFor("ModelRuntimeKey", nullable: false));
                table.HasCheckConstraint(
                    "CK_GpuMiniTasks_SettingsFingerprint_NoTrailingWhitespace",
                    SchemaConfiguration.NoTrailingWhitespaceCheckFor("SettingsFingerprint", nullable: false));
                table.HasCheckConstraint(
                    "CK_GpuMiniTasks_IdempotencyKey_NoTrailingWhitespace",
                    SchemaConfiguration.NoTrailingWhitespaceCheckFor("IdempotencyKey", nullable: false));
                table.HasCheckConstraint(
                    "CK_GpuMiniTasks_HandoffLeaseOwner_NoTrailingWhitespace",
                    SchemaConfiguration.NoTrailingWhitespaceCheckFor("HandoffLeaseOwner", nullable: true));
            });
        builder.HasKey(entity => entity.Id);
        builder.Property(entity => entity.Id).ValueGeneratedNever();
        builder.Property(entity => entity.ModelRuntimeKey)
            .HasMaxLength(256)
            .IsRequired()
            .UseCollation(SchemaConfiguration.SchedulerFenceCollation);
        builder.Property(entity => entity.SettingsFingerprint)
            .HasMaxLength(256)
            .IsRequired()
            .UseCollation(SchemaConfiguration.SchedulerFenceCollation);
        builder.Property(entity => entity.IdempotencyKey)
            .HasMaxLength(512)
            .IsRequired()
            .UseCollation(SchemaConfiguration.SchedulerFenceCollation);
        builder.Property(entity => entity.HandoffLeaseOwner)
            .HasMaxLength(256)
            .UseCollation(SchemaConfiguration.SchedulerFenceCollation);
        builder.Property(entity => entity.ExecutionState).HasColumnName("State");
        builder.Property(entity => entity.CreatedSequence)
            .ValueGeneratedOnAdd()
            .HasDefaultValueSql("NEXT VALUE FOR [GpuMiniTaskCreatedSequence]");
        builder.Property(entity => entity.DeferredUntilUtc).HasColumnType("datetimeoffset(7)");
        builder.Property(entity => entity.CreatedAtUtc).HasColumnType("datetimeoffset(7)");
        SchemaConfiguration.ConfigureRowVersion(builder.Property(entity => entity.RowVersion));
        builder.HasIndex(entity => entity.IdempotencyKey).IsUnique();
        builder.HasIndex(entity => new { entity.ExecutionState, entity.PriorityLane, entity.CreatedSequence, entity.Id });
        builder.HasIndex(entity => new { entity.ExecutionState, entity.DeferredUntilUtc });
        builder.HasOne(entity => entity.ParentJob)
            .WithMany()
            .HasForeignKey(entity => new { entity.ParentJobId, entity.SourceRevision })
            .HasPrincipalKey(entity => new { entity.Id, entity.SourceRevision })
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(entity => entity.Batch)
            .WithMany()
            .HasForeignKey(entity => entity.BatchId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class GpuBatchConfiguration : IEntityTypeConfiguration<GpuBatchEntity>
{
    public void Configure(EntityTypeBuilder<GpuBatchEntity> builder)
    {
        builder.ToTable(
            "GpuBatches",
            table =>
            {
                table.HasCheckConstraint(
                    "CK_GpuBatches_CapacitySlotKey_NoTrailingWhitespace",
                    SchemaConfiguration.NoTrailingWhitespaceCheckFor("CapacitySlotKey", nullable: false));
                table.HasCheckConstraint(
                    "CK_GpuBatches_ModelRuntimeKey_NoTrailingWhitespace",
                    SchemaConfiguration.NoTrailingWhitespaceCheckFor("ModelRuntimeKey", nullable: false));
                table.HasCheckConstraint(
                    "CK_GpuBatches_SettingsFingerprint_NoTrailingWhitespace",
                    SchemaConfiguration.NoTrailingWhitespaceCheckFor("SettingsFingerprint", nullable: false));
                table.HasCheckConstraint(
                    "CK_GpuBatches_OwnerKey_NoTrailingWhitespace",
                    SchemaConfiguration.NoTrailingWhitespaceCheckFor("OwnerKey", nullable: false));
            });
        builder.HasKey(entity => entity.Id);
        builder.Property(entity => entity.Id).ValueGeneratedNever();
        builder.Property(entity => entity.CapacitySlotKey)
            .HasMaxLength(256)
            .IsRequired()
            .UseCollation(SchemaConfiguration.SchedulerFenceCollation);
        builder.Property(entity => entity.ModelRuntimeKey)
            .HasMaxLength(256)
            .IsRequired()
            .UseCollation(SchemaConfiguration.SchedulerFenceCollation);
        builder.Property(entity => entity.SettingsFingerprint)
            .HasMaxLength(256)
            .IsRequired()
            .UseCollation(SchemaConfiguration.SchedulerFenceCollation);
        builder.Property(entity => entity.OwnerKey)
            .HasMaxLength(256)
            .IsRequired()
            .UseCollation(SchemaConfiguration.SchedulerFenceCollation);
        builder.Property(entity => entity.LastHeartbeatAtUtc).HasColumnType("datetimeoffset(7)");
        builder.Property(entity => entity.CreatedAtUtc).HasColumnType("datetimeoffset(7)");
        builder.Property(entity => entity.UpdatedAtUtc).HasColumnType("datetimeoffset(7)");
        SchemaConfiguration.ConfigureRowVersion(builder.Property(entity => entity.RowVersion));
        builder.HasIndex(entity => entity.CapacitySlotKey);
        builder.HasOne(entity => entity.CapacitySlot)
            .WithMany()
            .HasForeignKey(entity => entity.CapacitySlotKey)
            .HasPrincipalKey(entity => entity.SlotKey)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class GpuCapacitySlotConfiguration : IEntityTypeConfiguration<GpuCapacitySlotEntity>
{
    public void Configure(EntityTypeBuilder<GpuCapacitySlotEntity> builder)
    {
        builder.ToTable(
            "GpuCapacitySlots",
            table =>
            {
                table.HasCheckConstraint(
                    "CK_GpuCapacitySlots_SlotKey_NoTrailingWhitespace",
                    SchemaConfiguration.NoTrailingWhitespaceCheckFor("SlotKey", nullable: false));
                table.HasCheckConstraint(
                    "CK_GpuCapacitySlots_OwnerKey_NoTrailingWhitespace",
                    SchemaConfiguration.NoTrailingWhitespaceCheckFor("OwnerKey", nullable: true));
            });
        builder.HasKey(entity => entity.SlotKey);
        builder.Property(entity => entity.SlotKey)
            .HasMaxLength(256)
            .ValueGeneratedNever()
            .UseCollation(SchemaConfiguration.SchedulerFenceCollation);
        builder.Property(entity => entity.OwnerKey)
            .HasMaxLength(256)
            .UseCollation(SchemaConfiguration.SchedulerFenceCollation);
        builder.Property(entity => entity.LastHeartbeatAtUtc).HasColumnType("datetimeoffset(7)");
        builder.Property(entity => entity.UpdatedAtUtc).HasColumnType("datetimeoffset(7)");
        SchemaConfiguration.ConfigureRowVersion(builder.Property(entity => entity.RowVersion));
        builder.HasIndex(entity => new { entity.State, entity.SlotKey });
        builder.HasOne(entity => entity.ActiveBatch)
            .WithMany()
            .HasForeignKey(entity => entity.ActiveBatchId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class GpuSchedulerStateConfiguration : IEntityTypeConfiguration<GpuSchedulerStateEntity>
{
    public void Configure(EntityTypeBuilder<GpuSchedulerStateEntity> builder)
    {
        builder.ToTable(
            "GpuSchedulerState",
            table =>
            {
                table.HasCheckConstraint("CK_GpuSchedulerState_Singleton", "[Id] = 1");
                table.HasCheckConstraint(
                    "CK_GpuSchedulerState_InFlightWake",
                    "([InFlightWakeOperationId] IS NULL AND [InFlightWakeGeneration] IS NULL AND [InFlightWakeReasons] = 0 AND [InFlightNextDeferredAtUtc] IS NULL AND [InFlightEffectiveAdmissionReasons] IS NULL) OR ([InFlightWakeOperationId] IS NOT NULL AND [InFlightWakeGeneration] IS NOT NULL AND [InFlightEffectiveAdmissionReasons] IS NOT NULL)");
            });
        builder.HasKey(entity => entity.Id);
        builder.Property(entity => entity.Id).ValueGeneratedNever();
        builder.Property(entity => entity.NextDeferredAtUtc).HasColumnType("datetimeoffset(7)");
        builder.Property(entity => entity.InFlightNextDeferredAtUtc).HasColumnType("datetimeoffset(7)");
        builder.Property(entity => entity.InFlightEffectiveAdmissionReasons).HasColumnType("int");
        builder.Property(entity => entity.UpdatedAtUtc).HasColumnType("datetimeoffset(7)");
        SchemaConfiguration.ConfigureRowVersion(builder.Property(entity => entity.RowVersion));
    }
}

public sealed class GpuSchedulerOperationReceiptConfiguration : IEntityTypeConfiguration<GpuSchedulerOperationReceiptEntity>
{
    public void Configure(EntityTypeBuilder<GpuSchedulerOperationReceiptEntity> builder)
    {
        builder.ToTable(
            "GpuSchedulerOperationReceipts",
            table =>
            {
                table.HasCheckConstraint(
                    "CK_GpuSchedulerOperationReceipts_OperationKind_NoTrailingWhitespace",
                    SchemaConfiguration.NoTrailingWhitespaceCheckFor("OperationKind", nullable: false));
                table.HasCheckConstraint(
                    "CK_GpuSchedulerOperationReceipts_RequestFingerprint_NoTrailingWhitespace",
                    SchemaConfiguration.NoTrailingWhitespaceCheckFor("RequestFingerprint", nullable: true));
                table.HasCheckConstraint(
                    "CK_GpuSchedulerOperationReceipts_CapacitySlotKey_NoTrailingWhitespace",
                    SchemaConfiguration.NoTrailingWhitespaceCheckFor("CapacitySlotKey", nullable: true));
                table.HasCheckConstraint(
                    "CK_GpuSchedulerOperationReceipts_OwnerKey_NoTrailingWhitespace",
                    SchemaConfiguration.NoTrailingWhitespaceCheckFor("OwnerKey", nullable: true));
            });
        builder.HasKey(entity => entity.OperationId);
        builder.Property(entity => entity.OperationId).ValueGeneratedNever();
        builder.Property(entity => entity.OperationKind)
            .HasMaxLength(64)
            .IsRequired()
            .UseCollation(SchemaConfiguration.SchedulerFenceCollation);
        builder.Property(entity => entity.RequestFingerprint)
            .HasMaxLength(64)
            .UseCollation(SchemaConfiguration.SchedulerFenceCollation);
        builder.Property(entity => entity.CapacitySlotKey)
            .HasMaxLength(256)
            .UseCollation(SchemaConfiguration.SchedulerFenceCollation);
        builder.Property(entity => entity.OwnerKey)
            .HasMaxLength(256)
            .UseCollation(SchemaConfiguration.SchedulerFenceCollation);
        builder.Property(entity => entity.DeferredUntilUtc).HasColumnType("datetimeoffset(7)");
        builder.Property(entity => entity.NextDeferredAtUtc).HasColumnType("datetimeoffset(7)");
        builder.Property(entity => entity.EffectiveAdmissionReasons).HasColumnType("int");
        builder.Property(entity => entity.CreatedAtUtc).HasColumnType("datetimeoffset(7)");
        builder.HasIndex(entity => new { entity.OperationKind, entity.BatchId, entity.CapacitySlotKey, entity.AdmissionGeneration });
    }
}

public sealed class GpuExecutorDispatchConfiguration : IEntityTypeConfiguration<GpuExecutorDispatchEntity>
{
    public void Configure(EntityTypeBuilder<GpuExecutorDispatchEntity> builder)
    {
        builder.ToTable(
            "GpuExecutorDispatches",
            table =>
            {
                table.HasCheckConstraint(
                    "CK_GpuExecutorDispatches_CapacitySlotKey_NoTrailingWhitespace",
                    SchemaConfiguration.NoTrailingWhitespaceCheckFor("CapacitySlotKey", nullable: false));
                table.HasCheckConstraint(
                    "CK_GpuExecutorDispatches_OwnerKey_NoTrailingWhitespace",
                    SchemaConfiguration.NoTrailingWhitespaceCheckFor("OwnerKey", nullable: false));
                table.HasCheckConstraint(
                    "CK_GpuExecutorDispatches_ExecutorKey_NoTrailingWhitespace",
                    SchemaConfiguration.NoTrailingWhitespaceCheckFor("ExecutorKey", nullable: false));
            });
        builder.HasKey(entity => entity.DispatchId);
        builder.Property(entity => entity.DispatchId).ValueGeneratedNever();
        builder.Property(entity => entity.CapacitySlotKey).HasMaxLength(256).IsRequired().UseCollation(SchemaConfiguration.SchedulerFenceCollation);
        builder.Property(entity => entity.OwnerKey).HasMaxLength(256).IsRequired().UseCollation(SchemaConfiguration.SchedulerFenceCollation);
        builder.Property(entity => entity.ExecutorKey).HasMaxLength(256).IsRequired().UseCollation(SchemaConfiguration.SchedulerFenceCollation);
        builder.Property(entity => entity.AcknowledgedAtUtc).HasColumnType("datetimeoffset(7)");
        builder.Property(entity => entity.NativeWorkerBindRequestFingerprint).HasColumnType("char(64)").IsUnicode(false).IsFixedLength().HasMaxLength(64).UseCollation(SchemaConfiguration.SchedulerFenceCollation);
        builder.Property(entity => entity.NativeWorkerClearRequestFingerprint).HasColumnType("char(64)").IsUnicode(false).IsFixedLength().HasMaxLength(64).UseCollation(SchemaConfiguration.SchedulerFenceCollation);
        builder.Property(entity => entity.UpdatedAtUtc).HasColumnType("datetimeoffset(7)");
        SchemaConfiguration.ConfigureRowVersion(builder.Property(entity => entity.RowVersion));
        builder.HasIndex(entity => entity.BatchId).IsUnique();
        builder.HasIndex(entity => new { entity.State, entity.UpdatedAtUtc });
        builder.HasIndex(entity => entity.NativeWorkerBindOperationId).IsUnique().HasFilter("[NativeWorkerBindOperationId] IS NOT NULL");
        builder.HasIndex(entity => entity.NativeWorkerClearOperationId).IsUnique().HasFilter("[NativeWorkerClearOperationId] IS NOT NULL");
        builder.HasOne(entity => entity.Batch).WithMany().HasForeignKey(entity => entity.BatchId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(entity => entity.CapacitySlot).WithMany().HasForeignKey(entity => entity.CapacitySlotKey).HasPrincipalKey(entity => entity.SlotKey).OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class GpuExecutorResultReceiptConfiguration : IEntityTypeConfiguration<GpuExecutorResultReceiptEntity>
{
    public void Configure(EntityTypeBuilder<GpuExecutorResultReceiptEntity> builder)
    {
        builder.ToTable(
            "GpuExecutorResultReceipts",
            table =>
            {
                table.HasCheckConstraint(
                    "CK_GpuExecutorResultReceipts_ExecutorKey_NoTrailingWhitespace",
                    SchemaConfiguration.NoTrailingWhitespaceCheckFor("ExecutorKey", nullable: false));
                table.HasCheckConstraint(
                    "CK_GpuExecutorResultReceipts_RequestFingerprint_NoTrailingWhitespace",
                    SchemaConfiguration.NoTrailingWhitespaceCheckFor("RequestFingerprint", nullable: false));
            });
        builder.HasKey(entity => entity.OperationId);
        builder.Property(entity => entity.OperationId).ValueGeneratedNever();
        builder.Property(entity => entity.ExecutorKey).HasMaxLength(256).IsRequired().UseCollation(SchemaConfiguration.SchedulerFenceCollation);
        builder.Property(entity => entity.OpaqueResultDigest).HasColumnType("varbinary(32)");
        builder.Property(entity => entity.RequestFingerprint).HasMaxLength(64).IsRequired().UseCollation(SchemaConfiguration.SchedulerFenceCollation);
        builder.Property(entity => entity.CreatedAtUtc).HasColumnType("datetimeoffset(7)");
        builder.HasIndex(entity => new { entity.DispatchId, entity.MiniTaskId }).IsUnique();
        builder.HasIndex(entity => new { entity.BatchId, entity.MiniTaskId, entity.AdmissionGeneration });
        builder.HasOne(entity => entity.Dispatch).WithMany().HasForeignKey(entity => entity.DispatchId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(entity => entity.Batch).WithMany().HasForeignKey(entity => entity.BatchId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(entity => entity.MiniTask).WithMany().HasForeignKey(entity => entity.MiniTaskId).OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class GpuExecutorEvidenceConfiguration : IEntityTypeConfiguration<GpuExecutorEvidenceEntity>
{
    public void Configure(EntityTypeBuilder<GpuExecutorEvidenceEntity> builder)
    {
        builder.ToTable(
            "GpuExecutorEvidence",
            table =>
            {
                table.HasCheckConstraint(
                    "CK_GpuExecutorEvidence_CapacitySlotKey_NoTrailingWhitespace",
                    SchemaConfiguration.NoTrailingWhitespaceCheckFor("CapacitySlotKey", nullable: false));
                table.HasCheckConstraint(
                    "CK_GpuExecutorEvidence_ExecutorKey_NoTrailingWhitespace",
                    SchemaConfiguration.NoTrailingWhitespaceCheckFor("ExecutorKey", nullable: false));
                table.HasCheckConstraint(
                    "CK_GpuExecutorEvidence_VerifierKey_NoTrailingWhitespace",
                    SchemaConfiguration.NoTrailingWhitespaceCheckFor("VerifierKey", nullable: false));
                table.HasCheckConstraint(
                    "CK_GpuExecutorEvidence_RequestFingerprint_NoTrailingWhitespace",
                    SchemaConfiguration.NoTrailingWhitespaceCheckFor("RequestFingerprint", nullable: false));
            });
        builder.HasKey(entity => entity.OperationId);
        builder.Property(entity => entity.OperationId).ValueGeneratedNever();
        builder.Property(entity => entity.CapacitySlotKey).HasMaxLength(256).IsRequired().UseCollation(SchemaConfiguration.SchedulerFenceCollation);
        builder.Property(entity => entity.ExecutorKey).HasMaxLength(256).IsRequired().UseCollation(SchemaConfiguration.SchedulerFenceCollation);
        builder.Property(entity => entity.VerifierKey).HasMaxLength(256).IsRequired().UseCollation(SchemaConfiguration.SchedulerFenceCollation);
        builder.Property(entity => entity.RequestFingerprint).HasMaxLength(64).IsRequired().UseCollation(SchemaConfiguration.SchedulerFenceCollation);
        builder.Property(entity => entity.ObservedAtUtc).HasColumnType("datetimeoffset(7)");
        builder.Property(entity => entity.CreatedAtUtc).HasColumnType("datetimeoffset(7)");
        builder.HasIndex(entity => new { entity.DispatchId, entity.EvidenceClass, entity.OperationId });
        builder.HasOne(entity => entity.Dispatch).WithMany().HasForeignKey(entity => entity.DispatchId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(entity => entity.Batch).WithMany().HasForeignKey(entity => entity.BatchId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(entity => entity.CapacitySlot).WithMany().HasForeignKey(entity => entity.CapacitySlotKey).HasPrincipalKey(entity => entity.SlotKey).OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class NativeWorkerInstanceConfiguration : IEntityTypeConfiguration<NativeWorkerInstanceEntity>
{
    public void Configure(EntityTypeBuilder<NativeWorkerInstanceEntity> builder)
    {
        builder.ToTable(
            "NativeWorkerInstances",
            table =>
            {
                table.HasCheckConstraint("CK_NativeWorkerInstances_ExecutableFingerprint_Sha256", SchemaConfiguration.Sha256CheckFor("ExecutableFingerprint"));
                table.HasCheckConstraint("CK_NativeWorkerInstances_ExecutorKey_NoTrailingWhitespace", SchemaConfiguration.NoTrailingWhitespaceCheckFor("ExecutorKey", nullable: false));
                table.HasCheckConstraint("CK_NativeWorkerInstances_ExecutableFingerprint_NoTrailingWhitespace", SchemaConfiguration.NoTrailingWhitespaceCheckFor("ExecutableFingerprint", nullable: false));
                table.HasCheckConstraint("CK_NativeWorkerInstances_ProtocolVersion_NoTrailingWhitespace", SchemaConfiguration.NoTrailingWhitespaceCheckFor("ProtocolVersion", nullable: false));
                table.HasCheckConstraint("CK_NativeWorkerInstances_ProcessId_Positive", "[ProcessId] IS NULL OR [ProcessId] > 0");
                table.HasCheckConstraint("CK_NativeWorkerInstances_ProcessAttestation_Complete", "([ProcessId] IS NULL AND [ProcessStartedAtUtc] IS NULL) OR ([ProcessId] IS NOT NULL AND [ProcessStartedAtUtc] IS NOT NULL)");
                table.HasCheckConstraint("CK_NativeWorkerInstances_State_Closed", "[State] >= 0 AND [State] <= 13");
            });
        builder.HasKey(entity => entity.InstanceId);
        builder.Property(entity => entity.InstanceId).ValueGeneratedNever();
        builder.Property(entity => entity.ExecutorKey).HasMaxLength(256).IsRequired().UseCollation(SchemaConfiguration.SchedulerFenceCollation);
        builder.Property(entity => entity.ProcessStartedAtUtc).HasColumnType("datetimeoffset(7)");
        SchemaConfiguration.ConfigureHash(builder.Property(entity => entity.ExecutableFingerprint));
        builder.Property(entity => entity.ExecutableFingerprint).UseCollation(SchemaConfiguration.SchedulerFenceCollation);
        builder.Property(entity => entity.ProtocolVersion).HasMaxLength(32).IsRequired().UseCollation(SchemaConfiguration.SchedulerFenceCollation);
        builder.Property(entity => entity.LaunchedAtUtc).HasColumnType("datetimeoffset(7)");
        builder.Property(entity => entity.ConnectedAtUtc).HasColumnType("datetimeoffset(7)");
        builder.Property(entity => entity.LastHeartbeatAtUtc).HasColumnType("datetimeoffset(7)");
        builder.Property(entity => entity.ExitedAtUtc).HasColumnType("datetimeoffset(7)");
        SchemaConfiguration.ConfigureRowVersion(builder.Property(entity => entity.RowVersion));
        SchemaConfiguration.ConfigureImmutableAfterInsert(builder.Property(entity => entity.ExecutorKey));
        SchemaConfiguration.ConfigureImmutableAfterInsert(builder.Property(entity => entity.ExecutableFingerprint));
        SchemaConfiguration.ConfigureImmutableAfterInsert(builder.Property(entity => entity.ProtocolVersion));
        builder.HasIndex(entity => entity.ActiveDispatchId).IsUnique().HasFilter("[ActiveDispatchId] IS NOT NULL");
        builder.HasOne(entity => entity.ActiveDispatch).WithMany().HasForeignKey(entity => entity.ActiveDispatchId).OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class NativeWorkerLifecycleEvidenceConfiguration : IEntityTypeConfiguration<NativeWorkerLifecycleEvidenceEntity>
{
    public void Configure(EntityTypeBuilder<NativeWorkerLifecycleEvidenceEntity> builder)
    {
        builder.ToTable(
            "NativeWorkerLifecycleEvidence",
            table =>
            {
                table.HasCheckConstraint("CK_NativeWorkerLifecycleEvidence_RequestFingerprint_Sha256", SchemaConfiguration.Sha256CheckFor("RequestFingerprint"));
                table.HasCheckConstraint("CK_NativeWorkerLifecycleEvidence_RequestFingerprint_NoTrailingWhitespace", SchemaConfiguration.NoTrailingWhitespaceCheckFor("RequestFingerprint", nullable: false));
                table.HasCheckConstraint("CK_NativeWorkerLifecycleEvidence_LifecycleClass_Closed", "[LifecycleClass] >= 0 AND [LifecycleClass] <= 13");
                table.HasCheckConstraint("CK_NativeWorkerLifecycleEvidence_OutcomeCode_Bounded", "[OutcomeCode] IS NULL OR ([OutcomeCode] >= -32768 AND [OutcomeCode] <= 65535)");
            });
        builder.HasKey(entity => entity.OperationId);
        builder.Property(entity => entity.OperationId).ValueGeneratedNever();
        builder.Property(entity => entity.ObservedAtUtc).HasColumnType("datetimeoffset(7)");
        builder.Property(entity => entity.RequestFingerprint).HasMaxLength(64).IsUnicode(false).IsFixedLength().IsRequired().UseCollation(SchemaConfiguration.SchedulerFenceCollation);
        builder.Property(entity => entity.CreatedAtUtc).HasColumnType("datetimeoffset(7)");
        builder.HasIndex(entity => new { entity.InstanceId, entity.ObservedAtUtc, entity.OperationId });
        builder.HasOne(entity => entity.Instance).WithMany().HasForeignKey(entity => entity.InstanceId).OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class SourceRootConfigurationConfiguration : IEntityTypeConfiguration<SourceRootConfigurationEntity>
{
    public void Configure(EntityTypeBuilder<SourceRootConfigurationEntity> builder)
    {
        builder.ToTable("SourceRootConfigurations");
        builder.HasKey(entity => entity.Id);
        builder.Property(entity => entity.Id).ValueGeneratedNever();
        builder.Property(entity => entity.CanonicalPath).HasMaxLength(2048).IsRequired().UseCollation(SchemaConfiguration.SchedulerFenceCollation);
        builder.Property(entity => entity.CanonicalPathFingerprint)
            .HasMaxLength(64)
            .IsUnicode(false)
            .IsFixedLength()
            .HasComputedColumnSql("CONVERT(char(64), HASHBYTES('SHA2_256', [CanonicalPath]), 2)", stored: true)
            .UseCollation(SchemaConfiguration.SchedulerFenceCollation);
        builder.Property(entity => entity.DisplayName).HasMaxLength(256).IsRequired();
        builder.Property(entity => entity.IncludePatternsJson).HasColumnType("nvarchar(max)").IsRequired();
        builder.Property(entity => entity.ExcludePatternsJson).HasColumnType("nvarchar(max)").IsRequired();
        builder.Property(entity => entity.AllowedClassificationsJson).HasColumnType("nvarchar(max)").IsRequired();
        builder.Property(entity => entity.LastScanStartedAtUtc).HasColumnType("datetimeoffset(7)");
        builder.Property(entity => entity.LastScanCompletedAtUtc).HasColumnType("datetimeoffset(7)");
        builder.Property(entity => entity.LastScanEvidenceJson).HasColumnType("nvarchar(max)");
        builder.Property(entity => entity.PermissionEvidenceJson).HasColumnType("nvarchar(max)");
        builder.Property(entity => entity.HealthEvidenceJson).HasColumnType("nvarchar(max)");
        builder.Property(entity => entity.CreatedAtUtc).HasColumnType("datetimeoffset(7)");
        builder.Property(entity => entity.UpdatedAtUtc).HasColumnType("datetimeoffset(7)");
        SchemaConfiguration.ConfigureRowVersion(builder.Property(entity => entity.RowVersion));
        builder.HasIndex(entity => entity.CanonicalPathFingerprint).IsUnique();
    }
}

public sealed class SourceRootWatchStateConfiguration : IEntityTypeConfiguration<SourceRootWatchStateEntity>
{
    public void Configure(EntityTypeBuilder<SourceRootWatchStateEntity> builder)
    {
        builder.ToTable("SourceRootWatchStates");
        builder.HasKey(entity => entity.SourceRootId);
        builder.Property(entity => entity.FirstSignalAtUtc).HasColumnType("datetimeoffset(7)");
        builder.Property(entity => entity.LastSignalAtUtc).HasColumnType("datetimeoffset(7)");
        builder.Property(entity => entity.DueAtUtc).HasColumnType("datetimeoffset(7)");
        builder.Property(entity => entity.LeaseOwner).HasMaxLength(256).UseCollation(SchemaConfiguration.SchedulerFenceCollation);
        builder.Property(entity => entity.LeaseExpiresAtUtc).HasColumnType("datetimeoffset(7)");
        SchemaConfiguration.ConfigureRowVersion(builder.Property(entity => entity.RowVersion));
        builder.HasIndex(entity => new { entity.DueAtUtc, entity.LeaseExpiresAtUtc });
        builder.HasOne(entity => entity.SourceRoot)
            .WithOne()
            .HasForeignKey<SourceRootWatchStateEntity>(entity => entity.SourceRootId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class SourceScanRequestConfiguration : IEntityTypeConfiguration<SourceScanRequestEntity>
{
    public void Configure(EntityTypeBuilder<SourceScanRequestEntity> builder)
    {
        builder.ToTable("SourceScanRequests");
        builder.HasKey(entity => entity.Id);
        builder.Property(entity => entity.Id).ValueGeneratedNever();
        builder.Property(entity => entity.RequestedBy).HasMaxLength(256).IsRequired().UseCollation(SchemaConfiguration.SchedulerFenceCollation);
        builder.Property(entity => entity.RequestedAtUtc).HasColumnType("datetimeoffset(7)");
        builder.Property(entity => entity.ReleasedAtUtc).HasColumnType("datetimeoffset(7)");
        builder.Property(entity => entity.AuditEvidenceJson).HasColumnType("nvarchar(max)");
        SchemaConfiguration.ConfigureRowVersion(builder.Property(entity => entity.RowVersion));
        builder.HasIndex(entity => new { entity.SourceRootId, entity.RequestedAtUtc });
        builder.HasIndex(entity => new { entity.IsReleased, entity.State });
        builder.HasOne(entity => entity.SourceRoot).WithMany().HasForeignKey(entity => entity.SourceRootId).OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class SourceScanJobConfiguration : IEntityTypeConfiguration<SourceScanJobEntity>
{
    public void Configure(EntityTypeBuilder<SourceScanJobEntity> builder)
    {
        builder.ToTable("SourceScanJobs");
        builder.HasKey(entity => entity.Id);
        builder.Property(entity => entity.Id).ValueGeneratedNever();
        builder.Property(entity => entity.DueAtUtc).HasColumnType("datetimeoffset(7)");
        builder.Property(entity => entity.LeaseOwner).HasMaxLength(256).UseCollation(SchemaConfiguration.SchedulerFenceCollation);
        builder.Property(entity => entity.LeaseExpiresAtUtc).HasColumnType("datetimeoffset(7)");
        builder.Property(entity => entity.Reason).HasMaxLength(1024);
        builder.Property(entity => entity.ErrorDetails).HasMaxLength(4000);
        builder.Property(entity => entity.CreatedAtUtc).HasColumnType("datetimeoffset(7)");
        builder.Property(entity => entity.UpdatedAtUtc).HasColumnType("datetimeoffset(7)");
        SchemaConfiguration.ConfigureRowVersion(builder.Property(entity => entity.RowVersion));
        builder.HasIndex(entity => entity.SourceScanRequestId).IsUnique();
        builder.HasIndex(entity => new { entity.State, entity.DueAtUtc });
        builder.HasOne(entity => entity.SourceScanRequest).WithMany().HasForeignKey(entity => entity.SourceScanRequestId).OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class SourceScanOutboxConfiguration : IEntityTypeConfiguration<SourceScanOutboxEntity>
{
    public void Configure(EntityTypeBuilder<SourceScanOutboxEntity> builder)
    {
        builder.ToTable("SourceScanOutbox");
        builder.HasKey(entity => entity.Id);
        builder.Property(entity => entity.Id).ValueGeneratedNever();
        builder.Property(entity => entity.Operation).HasMaxLength(128).IsRequired().UseCollation(SchemaConfiguration.SchedulerFenceCollation);
        builder.Property(entity => entity.IdempotencyKey).HasMaxLength(512).IsRequired().UseCollation(SchemaConfiguration.SchedulerFenceCollation);
        builder.Property(entity => entity.DueAtUtc).HasColumnType("datetimeoffset(7)");
        builder.Property(entity => entity.CreatedAtUtc).HasColumnType("datetimeoffset(7)");
        builder.Property(entity => entity.DispatchedAtUtc).HasColumnType("datetimeoffset(7)");
        builder.Property(entity => entity.LeaseOwner).HasMaxLength(256).UseCollation(SchemaConfiguration.SchedulerFenceCollation);
        builder.Property(entity => entity.LeaseExpiresAtUtc).HasColumnType("datetimeoffset(7)");
        SchemaConfiguration.ConfigureRowVersion(builder.Property(entity => entity.RowVersion));
        builder.HasIndex(entity => entity.SourceScanRequestId).IsUnique();
        builder.HasIndex(entity => entity.IdempotencyKey).IsUnique();
        builder.HasIndex(entity => new { entity.DispatchedAtUtc, entity.DueAtUtc });
        builder.HasOne(entity => entity.SourceScanRequest).WithMany().HasForeignKey(entity => entity.SourceScanRequestId).OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class SourceRevisionConfiguration : IEntityTypeConfiguration<SourceRevisionEntity>
{
    public void Configure(EntityTypeBuilder<SourceRevisionEntity> builder)
    {
        builder.ToTable("SourceRevisions", table => table.HasCheckConstraint("CK_SourceRevisions_ContentSha256", SchemaConfiguration.Sha256CheckFor("ContentSha256")));
        builder.HasKey(entity => entity.Id);
        builder.Property(entity => entity.Id).ValueGeneratedNever();
        builder.Property(entity => entity.StableSourceIdentity).HasMaxLength(768).IsRequired().UseCollation(SchemaConfiguration.SchedulerFenceCollation);
        SchemaConfiguration.ConfigureHash(builder.Property(entity => entity.ContentSha256));
        builder.Property(entity => entity.CanonicalPath).HasMaxLength(2048).IsRequired().UseCollation(SchemaConfiguration.SchedulerFenceCollation);
        builder.Property(entity => entity.CanonicalPathFingerprint)
            .HasMaxLength(64)
            .IsUnicode(false)
            .IsFixedLength()
            .HasComputedColumnSql("CONVERT(char(64), HASHBYTES('SHA2_256', [CanonicalPath]), 2)", stored: true)
            .UseCollation(SchemaConfiguration.SchedulerFenceCollation);
        builder.Property(entity => entity.Classification).HasMaxLength(256).IsRequired();
        builder.Property(entity => entity.Extension).HasMaxLength(32).IsRequired();
        builder.Property(entity => entity.OriginKind).HasDefaultValue(0);
        builder.Property(entity => entity.FileCreatedAtUtc).HasColumnType("datetimeoffset(7)");
        builder.Property(entity => entity.FileLastWriteAtUtc).HasColumnType("datetimeoffset(7)");
        builder.Property(entity => entity.DiscoveredAtUtc).HasColumnType("datetimeoffset(7)");
        builder.Property(entity => entity.DiscoveryEvidenceJson).HasColumnType("nvarchar(max)");
        builder.Property(entity => entity.SuppressedAtUtc).HasColumnType("datetimeoffset(7)");
        builder.Property(entity => entity.RetainUntilUtc).HasColumnType("datetimeoffset(7)");
        builder.Property(entity => entity.RetentionEvidenceJson).HasColumnType("nvarchar(max)");
        SchemaConfiguration.ConfigureRowVersion(builder.Property(entity => entity.RowVersion));
        SchemaConfiguration.ConfigureImmutableAfterInsert(builder.Property(entity => entity.SourceRootId));
        SchemaConfiguration.ConfigureImmutableAfterInsert(builder.Property(entity => entity.StableSourceIdentity));
        SchemaConfiguration.ConfigureImmutableAfterInsert(builder.Property(entity => entity.Revision));
        SchemaConfiguration.ConfigureImmutableAfterInsert(builder.Property(entity => entity.ContentSha256));
        SchemaConfiguration.ConfigureImmutableAfterInsert(builder.Property(entity => entity.CanonicalPath));
        SchemaConfiguration.ConfigureImmutableAfterInsert(builder.Property(entity => entity.ParentSourceRevisionId));
        SchemaConfiguration.ConfigureImmutableAfterInsert(builder.Property(entity => entity.Classification));
        SchemaConfiguration.ConfigureImmutableAfterInsert(builder.Property(entity => entity.Extension));
        SchemaConfiguration.ConfigureImmutableAfterInsert(builder.Property(entity => entity.ByteLength));
        SchemaConfiguration.ConfigureImmutableAfterInsert(builder.Property(entity => entity.FileCreatedAtUtc));
        SchemaConfiguration.ConfigureImmutableAfterInsert(builder.Property(entity => entity.FileLastWriteAtUtc));
        SchemaConfiguration.ConfigureImmutableAfterInsert(builder.Property(entity => entity.DiscoveredAtUtc));
        SchemaConfiguration.ConfigureImmutableAfterInsert(builder.Property(entity => entity.DiscoveryEvidenceJson));
        builder.HasIndex(entity => new { entity.SourceRootId, entity.StableSourceIdentity, entity.Revision }).IsUnique();
        builder.HasIndex(entity => new { entity.SourceRootId, entity.CanonicalPathFingerprint, entity.ContentSha256 }).IsUnique();
        builder.HasOne(entity => entity.SourceRoot).WithMany().HasForeignKey(entity => entity.SourceRootId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(entity => entity.ParentSourceRevision).WithMany().HasForeignKey(entity => entity.ParentSourceRevisionId).OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class SourceProcessorBranchConfiguration : IEntityTypeConfiguration<SourceProcessorBranchEntity>
{
    public void Configure(EntityTypeBuilder<SourceProcessorBranchEntity> builder)
    {
        builder.ToTable("SourceProcessorBranches"); builder.HasKey(value => value.Id); builder.Property(value => value.Id).ValueGeneratedNever();
        SchemaConfiguration.ConfigureHash(builder.Property(value => value.InputSha256));
        builder.Property(value => value.ProcessorVersion).HasMaxLength(256).IsRequired().UseCollation(SchemaConfiguration.SchedulerFenceCollation);
        builder.Property(value => value.ProcessorFingerprint).HasMaxLength(256).IsRequired().UseCollation(SchemaConfiguration.SchedulerFenceCollation);
        builder.Property(value => value.LeaseOwner).HasMaxLength(768).UseCollation(SchemaConfiguration.SchedulerFenceCollation);
        builder.Property(value => value.CompletionReceiptFingerprint).HasMaxLength(64).IsUnicode(false).UseCollation(SchemaConfiguration.SchedulerFenceCollation);
        builder.Property(value => value.LeaseExpiresAtUtc).HasColumnType("datetimeoffset(7)"); builder.Property(value => value.CreatedAtUtc).HasColumnType("datetimeoffset(7)"); builder.Property(value => value.UpdatedAtUtc).HasColumnType("datetimeoffset(7)");
        SchemaConfiguration.ConfigureRowVersion(builder.Property(value => value.RowVersion));
        builder.HasIndex(value => value.SourceActivityId).IsUnique(); builder.HasIndex(value => new { value.State, value.LeaseExpiresAtUtc });
        builder.HasOne<SourceActivityEntity>().WithMany().HasForeignKey(value => value.SourceActivityId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<SourceRevisionEntity>().WithMany().HasForeignKey(value => value.SourceRevisionId).OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class SourceProcessorAttemptConfiguration : IEntityTypeConfiguration<SourceProcessorAttemptEntity>
{
    public void Configure(EntityTypeBuilder<SourceProcessorAttemptEntity> builder)
    {
        builder.ToTable("SourceProcessorAttempts"); builder.HasKey(value => value.Id); builder.Property(value => value.Id).ValueGeneratedNever();
        builder.Property(value => value.OutcomeCode).HasMaxLength(128).UseCollation(SchemaConfiguration.SchedulerFenceCollation); builder.Property(value => value.EvidenceJson).HasMaxLength(1024);
        builder.Property(value => value.StartedAtUtc).HasColumnType("datetimeoffset(7)"); builder.Property(value => value.FinishedAtUtc).HasColumnType("datetimeoffset(7)");
        builder.HasIndex(value => new { value.BranchId, value.LeaseGeneration }).IsUnique();
        builder.HasAlternateKey(value => new { value.BranchId, value.Id });
        builder.HasOne<SourceProcessorBranchEntity>().WithMany().HasForeignKey(value => value.BranchId).OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class SourceProcessorCodeDocumentConfiguration : IEntityTypeConfiguration<SourceProcessorCodeDocumentEntity>
{
    public void Configure(EntityTypeBuilder<SourceProcessorCodeDocumentEntity> builder)
    {
        builder.ToTable("SourceProcessorCodeDocuments", table =>
        {
            table.HasCheckConstraint("CK_SourceProcessorCodeDocuments_Counts", "[DecodedCharacterCount] >= 0 AND [LineCount] >= 1 AND [LineCount] <= [DecodedCharacterCount] + 1 AND [SymbolCount] >= 0 AND [ReferenceCount] >= 0 AND [DiagnosticsCount] BETWEEN 0 AND 256 AND [WithheldSymbolCount] >= 0 AND [WithheldReferenceCount] >= 0 AND [WithheldDiagnosticCount] BETWEEN 0 AND 256 AND [ReceiptDiagnosticCodeCount] BETWEEN 0 AND 256 AND [DiagnosticsCount] = [ReceiptDiagnosticCodeCount]");
            table.HasTrigger("TR_SourceProcessorCodeDocuments_Immutable");
            table.HasTrigger("TR_SourceProcessorCodeDocuments_InsertFence");
        });
        builder.HasKey(value => value.SourceProcessorBranchId);
        builder.HasAlternateKey(value => new
        {
            value.SourceProcessorBranchId,
            value.SourceRevisionId,
            value.RetainedArtifactSha256,
            value.DescriptorFingerprint,
            value.ParserFingerprint,
            value.HandlerImplementationId,
            value.WithheldSymbolCount,
            value.WithheldReferenceCount,
            value.WithheldDiagnosticCount,
            value.ReceiptDiagnosticCodeCount,
            value.DocumentFingerprint,
            value.CompletionFingerprint
        });
        ConfigureCodeIdentity(builder);
        builder.Property(value => value.HandlerImplementationId).HasMaxLength(256).IsRequired().UseCollation(SchemaConfiguration.SchedulerFenceCollation);
        builder.Property(value => value.DocumentFingerprint).HasMaxLength(64).IsUnicode(false).IsRequired();
        builder.Property(value => value.CompletionFingerprint).HasMaxLength(64).IsUnicode(false).IsRequired();
        builder.HasOne<SourceProcessorBranchEntity>().WithMany().HasForeignKey(value => value.SourceProcessorBranchId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<SourceRevisionEntity>().WithMany().HasForeignKey(value => value.SourceRevisionId).OnDelete(DeleteBehavior.Restrict);
        SchemaConfiguration.ConfigureImmutableAfterInsert(builder);
    }

    internal static void ConfigureCodeIdentity<TEntity>(EntityTypeBuilder<TEntity> builder) where TEntity : class
    {
        SchemaConfiguration.ConfigureHash(builder.Property<string>("RetainedArtifactSha256"));
        SchemaConfiguration.ConfigureHash(builder.Property<string>("DescriptorFingerprint"));
        SchemaConfiguration.ConfigureHash(builder.Property<string>("ParserFingerprint"));
    }
}

public sealed class SourceProcessorCodeSymbolConfiguration : IEntityTypeConfiguration<SourceProcessorCodeSymbolEntity>
{
    public void Configure(EntityTypeBuilder<SourceProcessorCodeSymbolEntity> builder)
    {
        builder.ToTable("SourceProcessorCodeSymbols", table =>
        {
            table.HasCheckConstraint("CK_SourceProcessorCodeSymbols_Bounds", "[Ordinal] >= 0 AND [DeclarationKindCode] BETWEEN 1 AND 20 AND [SpanStartUtf16] >= 0 AND [SpanLengthUtf16] >= 0 AND [LexicalParentOrdinal] >= -1 AND [LexicalParentOrdinal] < [Ordinal]");
            table.HasTrigger("TR_SourceProcessorCodeSymbols_Immutable");
            table.HasTrigger("TR_SourceProcessorCodeSymbols_InsertFence");
        });
        builder.HasKey(value => new { value.DocumentId, value.Ordinal });
        builder.Property(value => value.LocalName).HasMaxLength(1024).IsRequired(); builder.Property(value => value.QualifiedName).HasMaxLength(4096).IsRequired(); builder.Property(value => value.RenderedSignature).HasMaxLength(4096).IsRequired(); builder.Property(value => value.Modifiers).HasMaxLength(512).IsRequired();
        builder.Property(value => value.SymbolFingerprint).HasMaxLength(64).IsUnicode(false).IsRequired();
        builder.HasIndex(value => new { value.DocumentId, value.SymbolFingerprint }).IsUnique();
        builder.HasOne<SourceProcessorCodeDocumentEntity>().WithMany().HasForeignKey(value => value.DocumentId).OnDelete(DeleteBehavior.Restrict);
        SchemaConfiguration.ConfigureImmutableAfterInsert(builder);
    }
}

public sealed class SourceProcessorCodeReferenceConfiguration : IEntityTypeConfiguration<SourceProcessorCodeReferenceEntity>
{
    public void Configure(EntityTypeBuilder<SourceProcessorCodeReferenceEntity> builder)
    {
        builder.ToTable("SourceProcessorCodeReferences", table =>
        {
            table.HasCheckConstraint("CK_SourceProcessorCodeReferences_Bounds", "[Ordinal] >= 0 AND [RelationshipKindCode] BETWEEN 1 AND 7 AND ([SourceSymbolOrdinal] IS NULL OR [SourceSymbolOrdinal] >= 0) AND [SpanStartUtf16] >= 0 AND [SpanLengthUtf16] >= 0");
            table.HasTrigger("TR_SourceProcessorCodeReferences_Immutable");
            table.HasTrigger("TR_SourceProcessorCodeReferences_InsertFence");
        });
        builder.HasKey(value => new { value.DocumentId, value.Ordinal }); builder.Property(value => value.TargetDisplay).HasMaxLength(4096).IsRequired(); builder.Property(value => value.ReferenceFingerprint).HasMaxLength(64).IsUnicode(false).IsRequired();
        builder.HasIndex(value => new { value.DocumentId, value.ReferenceFingerprint }).IsUnique(); builder.HasOne<SourceProcessorCodeDocumentEntity>().WithMany().HasForeignKey(value => value.DocumentId).OnDelete(DeleteBehavior.Restrict);
        SchemaConfiguration.ConfigureImmutableAfterInsert(builder);
    }
}

public sealed class SourceProcessorCodeDiagnosticConfiguration : IEntityTypeConfiguration<SourceProcessorCodeDiagnosticEntity>
{
    public void Configure(EntityTypeBuilder<SourceProcessorCodeDiagnosticEntity> builder)
    {
        builder.ToTable("SourceProcessorCodeDiagnostics", table =>
        {
            table.HasCheckConstraint("CK_SourceProcessorCodeDiagnostics_Representation", "[Ordinal] BETWEEN 0 AND 255 AND [Severity] BETWEEN 0 AND 3 AND [SpanStartUtf16] >= 0 AND [SpanLengthUtf16] >= 0 AND (([Representation] = N'scanned' AND [ScannedMessage] IS NOT NULL AND [WithheldReason] IS NULL) OR ([Representation] = N'withheld' AND [ScannedMessage] IS NULL AND [WithheldReason] = N'secret-content-withheld'))");
            table.HasTrigger("TR_SourceProcessorCodeDiagnostics_Immutable");
            table.HasTrigger("TR_SourceProcessorCodeDiagnostics_InsertFence");
        });
        builder.HasKey(value => new { value.DocumentId, value.Ordinal }); builder.Property(value => value.DiagnosticId).HasMaxLength(64).IsRequired(); builder.Property(value => value.Representation).HasMaxLength(16).IsRequired(); builder.Property(value => value.ScannedMessage).HasMaxLength(1024); builder.Property(value => value.WithheldReason).HasMaxLength(64); builder.Property(value => value.DiagnosticFingerprint).HasMaxLength(64).IsUnicode(false).IsRequired();
        builder.HasIndex(value => new { value.DocumentId, value.DiagnosticFingerprint }).IsUnique(); builder.HasIndex(value => new { value.DocumentId, value.Severity, value.Ordinal }); builder.HasOne<SourceProcessorCodeDocumentEntity>().WithMany().HasForeignKey(value => value.DocumentId).OnDelete(DeleteBehavior.Restrict);
        SchemaConfiguration.ConfigureImmutableAfterInsert(builder);
    }
}

public sealed class SourceProcessorCodeCompletionReceiptConfiguration : IEntityTypeConfiguration<SourceProcessorCodeCompletionReceiptEntity>
{
    public void Configure(EntityTypeBuilder<SourceProcessorCodeCompletionReceiptEntity> builder)
    {
        builder.ToTable("SourceProcessorCodeCompletionReceipts", table =>
        {
            table.HasCheckConstraint("CK_SourceProcessorCodeCompletionReceipts_Outcome", "(([OutcomeCode] = N'csharp-code-syntax-invalid' AND [DocumentId] IS NULL AND [DocumentFingerprint] IS NULL AND [BlockedDiagnosticsCount] BETWEEN 0 AND 256 AND [WithheldSymbolCount] = 0 AND [WithheldReferenceCount] = 0) OR ([OutcomeCode] = N'success' AND [DocumentId] IS NOT NULL AND [DocumentFingerprint] IS NOT NULL AND [BlockedDiagnosticsCount] = 0)) AND [ActivityKind] = 5 AND [WithheldSymbolCount] >= 0 AND [WithheldReferenceCount] >= 0 AND [WithheldDiagnosticCount] BETWEEN 0 AND 256 AND [ReceiptDiagnosticCodeCount] BETWEEN 0 AND 256 AND [ReceiptDiagnosticCodesWire] LIKE CONVERT(varchar(3), [ReceiptDiagnosticCodeCount]) + ';%'");
            table.HasCheckConstraint("CK_SourceProcessorCodeCompletionReceipts_DocumentBranchEquality", "[DocumentId] IS NULL OR [DocumentId] = [SourceProcessorBranchId]");
            table.HasTrigger("TR_SourceProcessorCodeCompletionReceipts_Immutable");
            table.HasTrigger("TR_SourceProcessorCodeCompletionReceipts_OutcomeFence");
        });
        builder.HasKey(value => value.SourceProcessorBranchId); SourceProcessorCodeDocumentConfiguration.ConfigureCodeIdentity(builder);
        builder.Property(value => value.ProcessorVersion).HasMaxLength(256).IsRequired(); builder.Property(value => value.HandlerImplementationId).HasMaxLength(256).IsRequired().UseCollation(SchemaConfiguration.SchedulerFenceCollation); builder.Property(value => value.OutcomeCode).HasMaxLength(128).IsRequired(); builder.Property(value => value.DocumentFingerprint).HasMaxLength(64).IsUnicode(false); builder.Property(value => value.CompletionFingerprint).HasMaxLength(64).IsUnicode(false).IsRequired(); builder.Property(value => value.ReceiptDiagnosticCodesWire).HasMaxLength(8192).IsRequired(); builder.Property(value => value.CreatedAtUtc).HasColumnType("datetimeoffset(7)");
        builder.HasOne<SourceProcessorBranchEntity>().WithMany().HasForeignKey(value => value.SourceProcessorBranchId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<SourceRevisionEntity>().WithMany().HasForeignKey(value => value.SourceRevisionId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<SourceProcessorCodeDocumentEntity>().WithMany()
            .HasForeignKey(value => new
            {
                value.DocumentId,
                value.SourceRevisionId,
                value.RetainedArtifactSha256,
                value.DescriptorFingerprint,
                value.ParserFingerprint,
                value.HandlerImplementationId,
                value.WithheldSymbolCount,
                value.WithheldReferenceCount,
                value.WithheldDiagnosticCount,
                value.ReceiptDiagnosticCodeCount,
                value.DocumentFingerprint,
                value.CompletionFingerprint
            })
            .HasPrincipalKey(value => new
            {
                value.SourceProcessorBranchId,
                value.SourceRevisionId,
                value.RetainedArtifactSha256,
                value.DescriptorFingerprint,
                value.ParserFingerprint,
                value.HandlerImplementationId,
                value.WithheldSymbolCount,
                value.WithheldReferenceCount,
                value.WithheldDiagnosticCount,
                value.ReceiptDiagnosticCodeCount,
                value.DocumentFingerprint,
                value.CompletionFingerprint
            })
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_SourceProcessorCodeCompletionReceipts_SourceProcessorCodeDocuments_SuccessIdentity");
        builder.HasOne<SourceProcessorAttemptEntity>().WithMany().HasForeignKey(value => new { value.SourceProcessorBranchId, value.SourceProcessorAttemptId }).HasPrincipalKey(value => new { value.BranchId, value.Id }).OnDelete(DeleteBehavior.Restrict);
        SchemaConfiguration.ConfigureImmutableAfterInsert(builder);
    }
}

public sealed class SourceProcessorCodeBlockedDiagnosticConfiguration : IEntityTypeConfiguration<SourceProcessorCodeBlockedDiagnosticEntity>
{
    public void Configure(EntityTypeBuilder<SourceProcessorCodeBlockedDiagnosticEntity> builder)
    {
        builder.ToTable("SourceProcessorCodeBlockedDiagnostics", table =>
        {
            table.HasCheckConstraint("CK_SourceProcessorCodeBlockedDiagnostics_Representation", "[Ordinal] BETWEEN 0 AND 255 AND [Severity] BETWEEN 0 AND 3 AND [SpanStartUtf16] >= 0 AND [SpanLengthUtf16] >= 0 AND (([Representation] = N'scanned' AND [ScannedMessage] IS NOT NULL AND [WithheldReason] IS NULL) OR ([Representation] = N'withheld' AND [ScannedMessage] IS NULL AND [WithheldReason] = N'secret-content-withheld'))");
            table.HasTrigger("TR_SourceProcessorCodeBlockedDiagnostics_Immutable");
            table.HasTrigger("TR_SourceProcessorCodeBlockedDiagnostics_InsertFence");
        });
        builder.HasKey(value => new { value.SourceProcessorBranchId, value.SourceProcessorAttemptId, value.Ordinal }); builder.Property(value => value.DiagnosticId).HasMaxLength(64).IsRequired(); builder.Property(value => value.Representation).HasMaxLength(16).IsRequired(); builder.Property(value => value.ScannedMessage).HasMaxLength(1024); builder.Property(value => value.WithheldReason).HasMaxLength(64); builder.Property(value => value.BlockedDiagnosticFingerprint).HasMaxLength(64).IsUnicode(false).IsRequired();
        builder.HasIndex(value => new { value.SourceProcessorBranchId, value.SourceProcessorAttemptId, value.BlockedDiagnosticFingerprint }).IsUnique(); builder.HasIndex(value => new { value.SourceProcessorBranchId, value.SourceProcessorAttemptId, value.Ordinal }); builder.HasOne<SourceProcessorBranchEntity>().WithMany().HasForeignKey(value => value.SourceProcessorBranchId).OnDelete(DeleteBehavior.Restrict); builder.HasOne<SourceProcessorAttemptEntity>().WithMany().HasForeignKey(value => new { value.SourceProcessorBranchId, value.SourceProcessorAttemptId }).HasPrincipalKey(value => new { value.BranchId, value.Id }).OnDelete(DeleteBehavior.Restrict);
        SchemaConfiguration.ConfigureImmutableAfterInsert(builder);
    }
}

public sealed class OperatorActionHardDenialConfiguration : IEntityTypeConfiguration<OperatorActionHardDenialEntity>
{
    public void Configure(EntityTypeBuilder<OperatorActionHardDenialEntity> builder)
    {
        builder.ToTable("OperatorActionHardDenials");
        builder.HasKey(value => value.ReasonCode);
        builder.Property(value => value.ReasonCode).HasMaxLength(128).IsRequired()
            .UseCollation(SchemaConfiguration.SchedulerFenceCollation);
        SchemaConfiguration.ConfigureImmutableAfterInsert(builder.Property(value => value.ReasonCode));
        builder.HasData(OperatorActionHardDenialReasons.All.Select(reasonCode => new OperatorActionHardDenialEntity { ReasonCode = reasonCode }));
    }
}

public sealed class OperatorActionCapabilityPolicyConfiguration : IEntityTypeConfiguration<OperatorActionCapabilityPolicyEntity>
{
    public void Configure(EntityTypeBuilder<OperatorActionCapabilityPolicyEntity> builder)
    {
        builder.ToTable("OperatorActionCapabilityPolicies");
        builder.HasKey(value => new
        {
            value.PolicyId, value.PolicyRevision, value.DescriptorId, value.DescriptorFingerprint,
            value.DescriptorVersion, value.SafetyContractId, value.HandlerId, value.ActionKind, value.ReasonCode
        });
        builder.Property(value => value.DescriptorFingerprint).HasMaxLength(256).IsRequired()
            .UseCollation(SchemaConfiguration.SchedulerFenceCollation);
        builder.Property(value => value.DescriptorVersion).HasMaxLength(256).IsRequired()
            .UseCollation(SchemaConfiguration.SchedulerFenceCollation);
        builder.Property(value => value.SafetyContractId).HasMaxLength(256).IsRequired()
            .UseCollation(SchemaConfiguration.SchedulerFenceCollation);
        builder.Property(value => value.HandlerId).HasMaxLength(256).IsRequired()
            .UseCollation(SchemaConfiguration.SchedulerFenceCollation);
        builder.Property(value => value.ActionKind).HasMaxLength(64).IsRequired()
            .UseCollation(SchemaConfiguration.SchedulerFenceCollation);
        builder.Property(value => value.ReasonCode).HasMaxLength(128).IsRequired()
            .UseCollation(SchemaConfiguration.SchedulerFenceCollation);
        foreach (var property in builder.Metadata.GetProperties().Where(property => property.IsPrimaryKey()))
        {
            property.SetAfterSaveBehavior(PropertySaveBehavior.Throw);
        }
    }
}

public sealed class OperatorActionActionLedgerConfiguration : IEntityTypeConfiguration<OperatorActionActionLedgerEntity>
{
    public void Configure(EntityTypeBuilder<OperatorActionActionLedgerEntity> builder)
    {
        builder.ToTable("OperatorActionActionLedger");
        builder.HasKey(value => value.ActionId);
        SchemaConfiguration.ConfigureHash(builder.Property(value => value.ActionId));
        builder.Property(value => value.ActionId).UseCollation(SchemaConfiguration.SchedulerFenceCollation);
        builder.Property(value => value.DescriptorFingerprint).HasMaxLength(256).IsRequired().UseCollation(SchemaConfiguration.SchedulerFenceCollation);
        builder.Property(value => value.DescriptorVersion).HasMaxLength(256).IsRequired().UseCollation(SchemaConfiguration.SchedulerFenceCollation);
        builder.Property(value => value.SafetyContractId).HasMaxLength(256).IsRequired().UseCollation(SchemaConfiguration.SchedulerFenceCollation);
        builder.Property(value => value.HandlerId).HasMaxLength(256).IsRequired().UseCollation(SchemaConfiguration.SchedulerFenceCollation);
        builder.Property(value => value.ActionKind).HasMaxLength(64).IsRequired().UseCollation(SchemaConfiguration.SchedulerFenceCollation);
        builder.Property(value => value.ReasonCode).HasMaxLength(128).IsRequired().UseCollation(SchemaConfiguration.SchedulerFenceCollation);
        builder.Property(value => value.BlockedRowVersion).HasColumnType("binary(8)").IsRequired();
        builder.Property(value => value.CreatedAtUtc).HasColumnType("datetimeoffset(7)");
        SchemaConfiguration.ConfigureRowVersion(builder.Property(value => value.RowVersion));
        foreach (var property in new[]
                 {
                     nameof(OperatorActionActionLedgerEntity.ActionId), nameof(OperatorActionActionLedgerEntity.PolicyId),
                     nameof(OperatorActionActionLedgerEntity.PolicyRevision), nameof(OperatorActionActionLedgerEntity.DescriptorId),
                     nameof(OperatorActionActionLedgerEntity.DescriptorFingerprint), nameof(OperatorActionActionLedgerEntity.DescriptorVersion),
                     nameof(OperatorActionActionLedgerEntity.SafetyContractId), nameof(OperatorActionActionLedgerEntity.HandlerId),
                     nameof(OperatorActionActionLedgerEntity.ActionKind), nameof(OperatorActionActionLedgerEntity.ReasonCode),
                     nameof(OperatorActionActionLedgerEntity.SourceProcessorBranchId), nameof(OperatorActionActionLedgerEntity.BlockedRowVersion),
                     nameof(OperatorActionActionLedgerEntity.SourceProcessorForceRequestId), nameof(OperatorActionActionLedgerEntity.CreatedAtUtc)
                 })
        {
            builder.Property(property).Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Throw);
        }
        builder.HasIndex(value => value.SourceProcessorForceRequestId).IsUnique().HasFilter("[SourceProcessorForceRequestId] IS NOT NULL");
        builder.HasOne<OperatorActionCapabilityPolicyEntity>().WithMany()
            .HasForeignKey(value => new
            {
                value.PolicyId, value.PolicyRevision, value.DescriptorId, value.DescriptorFingerprint,
                value.DescriptorVersion, value.SafetyContractId, value.HandlerId, value.ActionKind, value.ReasonCode
            })
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<SourceProcessorBranchEntity>().WithMany().HasForeignKey(value => value.SourceProcessorBranchId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<SourceProcessorForceRequestEntity>().WithMany().HasForeignKey(value => value.SourceProcessorForceRequestId).OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class OperatorActionOperationLedgerConfiguration : IEntityTypeConfiguration<OperatorActionOperationLedgerEntity>
{
    public void Configure(EntityTypeBuilder<OperatorActionOperationLedgerEntity> builder)
    {
        builder.ToTable("OperatorActionOperationLedger"); builder.HasKey(value => value.OperationId); builder.Property(value => value.OperationId).ValueGeneratedNever();
        SchemaConfiguration.ConfigureHash(builder.Property(value => value.RequestFingerprint));
        builder.Property(value => value.RequestFingerprint).UseCollation(SchemaConfiguration.SchedulerFenceCollation);
        SchemaConfiguration.ConfigureHash(builder.Property(value => value.ActionId));
        builder.Property(value => value.ActionId).UseCollation(SchemaConfiguration.SchedulerFenceCollation);
        builder.Property(value => value.CreatedAtUtc).HasColumnType("datetimeoffset(7)");
        builder.Property(value => value.IgnoreState).HasColumnType("bit");
        SchemaConfiguration.ConfigureRowVersion(builder.Property(value => value.RowVersion));
        foreach (var property in new[] { nameof(OperatorActionOperationLedgerEntity.OperationId), nameof(OperatorActionOperationLedgerEntity.RequestFingerprint), nameof(OperatorActionOperationLedgerEntity.ActionId), nameof(OperatorActionOperationLedgerEntity.CreatedAtUtc), nameof(OperatorActionOperationLedgerEntity.IgnoreSequence), nameof(OperatorActionOperationLedgerEntity.IgnoreState) })
        {
            builder.Property(property).Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Throw);
        }
        builder.HasIndex(value => value.OperationId).IsUnique();
        builder.HasIndex(value => value.ActionId);
        builder.HasOne<OperatorActionActionLedgerEntity>().WithMany().HasForeignKey(value => value.ActionId).OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class SourceProcessorActionIgnoreHeadConfiguration : IEntityTypeConfiguration<SourceProcessorActionIgnoreHeadEntity>
{
    public void Configure(EntityTypeBuilder<SourceProcessorActionIgnoreHeadEntity> builder)
    {
        builder.ToTable("SourceProcessorActionIgnoreHeads"); builder.HasKey(value => value.ActionId);
        SchemaConfiguration.ConfigureHash(builder.Property(value => value.ActionId));
        builder.Property(value => value.ActionId).UseCollation(SchemaConfiguration.SchedulerFenceCollation);
        builder.Property(value => value.UpdatedAtUtc).HasColumnType("datetimeoffset(7)");
        SchemaConfiguration.ConfigureRowVersion(builder.Property(value => value.RowVersion));
        SchemaConfiguration.ConfigureImmutableAfterInsert(builder.Property(value => value.ActionId));
        builder.HasIndex(value => value.ActionId).IsUnique();
        builder.HasOne<OperatorActionActionLedgerEntity>().WithMany().HasForeignKey(value => value.ActionId).OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class SourceProcessorForceRequestConfiguration : IEntityTypeConfiguration<SourceProcessorForceRequestEntity>
{
    public void Configure(EntityTypeBuilder<SourceProcessorForceRequestEntity> builder)
    {
        builder.ToTable("SourceProcessorForceRequests", table =>
        {
            table.HasCheckConstraint("CK_SourceProcessorForceRequests_State", "[State] IN (0, 1, 2, 3, 4, 5, 6)");
            table.HasCheckConstraint("CK_SourceProcessorForceRequests_ActionId", SchemaConfiguration.Sha256CheckFor("ActionId"));
            table.HasCheckConstraint("CK_SourceProcessorForceRequests_RequestFingerprint", SchemaConfiguration.Sha256CheckFor("RequestFingerprint"));
            table.HasCheckConstraint("CK_SourceProcessorForceRequests_TerminalReceiptFingerprint", "[TerminalReceiptFingerprint] IS NULL OR (" + SchemaConfiguration.Sha256CheckFor("TerminalReceiptFingerprint") + ")");
            table.HasCheckConstraint("CK_SourceProcessorForceRequests_ExpectedInputSha256", SchemaConfiguration.Sha256CheckFor("ExpectedInputSha256"));
            table.HasCheckConstraint("CK_SourceProcessorForceRequests_Timestamps", "[RequestedAtUtc] <= [ClaimExpiresAtUtc] AND ([ClaimedAtUtc] IS NULL OR [ClaimedAtUtc] >= [RequestedAtUtc]) AND ([TerminalAtUtc] IS NULL OR [TerminalAtUtc] >= [RequestedAtUtc]) AND ([TerminalAtUtc] IS NULL OR [ClaimedAtUtc] IS NULL OR [TerminalAtUtc] >= [ClaimedAtUtc])");
            table.HasCheckConstraint("CK_SourceProcessorForceRequests_AttemptBinding", "([ForceAttemptBranchId] IS NULL AND [ForceAttemptLeaseGeneration] IS NULL) OR ([ForceAttemptBranchId] IS NOT NULL AND [ForceAttemptLeaseGeneration] IS NOT NULL AND [ForceAttemptBranchId] = [SourceProcessorBranchId])");
            table.HasCheckConstraint("CK_SourceProcessorForceRequests_OriginalOutcome", "[OriginalOutcomeCode] <> N''");
            table.HasCheckConstraint("CK_SourceProcessorForceRequests_StateShape", $"(" +
                "([State] = 0 AND [ClaimedAtUtc] IS NULL AND [TerminalAtUtc] IS NULL AND [ForceAttemptBranchId] IS NULL AND [TerminalReceiptFingerprint] IS NULL AND [TerminalReasonCode] IS NULL) OR " +
                "([State] = 1 AND [ClaimedAtUtc] IS NOT NULL AND [TerminalAtUtc] IS NULL AND [ForceAttemptBranchId] IS NOT NULL AND [TerminalReceiptFingerprint] IS NULL AND [TerminalReasonCode] IS NULL) OR " +
                $"([State] = 2 AND [ClaimedAtUtc] IS NOT NULL AND [TerminalAtUtc] IS NOT NULL AND [ForceAttemptBranchId] IS NOT NULL AND [TerminalReceiptFingerprint] IS NOT NULL AND [TerminalReasonCode] = N'completed') OR " +
                "([State] = 3 AND [ClaimedAtUtc] IS NOT NULL AND [TerminalAtUtc] IS NOT NULL AND [ForceAttemptBranchId] IS NOT NULL AND [TerminalReceiptFingerprint] IS NOT NULL AND [TerminalReasonCode] IS NOT NULL) OR " +
                "([State] = 4 AND [ClaimedAtUtc] IS NOT NULL AND [TerminalAtUtc] IS NOT NULL AND [ForceAttemptBranchId] IS NOT NULL AND [TerminalReceiptFingerprint] IS NOT NULL AND [TerminalReasonCode] = N'force-request-transient') OR " +
                "([State] = 5 AND [TerminalAtUtc] IS NOT NULL AND [TerminalReceiptFingerprint] IS NOT NULL AND [TerminalReasonCode] IN (N'force-request-cancelled', N'force-request-descriptor-disabled') AND (([ClaimedAtUtc] IS NULL AND [ForceAttemptBranchId] IS NULL) OR ([ClaimedAtUtc] IS NOT NULL AND [ForceAttemptBranchId] IS NOT NULL))) OR " +
                "([State] = 6 AND [TerminalAtUtc] IS NOT NULL AND [TerminalReceiptFingerprint] IS NOT NULL AND (([ClaimedAtUtc] IS NULL AND [ForceAttemptBranchId] IS NULL AND [TerminalReasonCode] = N'force-request-claim-expired') OR ([ClaimedAtUtc] IS NOT NULL AND [ForceAttemptBranchId] IS NOT NULL AND [TerminalReasonCode] = N'lease-expired-reconciled'))))");
        });
        builder.HasKey(value => value.Id); builder.Property(value => value.Id).ValueGeneratedNever();
        SchemaConfiguration.ConfigureHash(builder.Property(value => value.ActionId));
        builder.Property(value => value.ActionId).UseCollation(SchemaConfiguration.SchedulerFenceCollation);
        SchemaConfiguration.ConfigureHash(builder.Property(value => value.RequestFingerprint));
        builder.Property(value => value.RequestFingerprint).UseCollation(SchemaConfiguration.SchedulerFenceCollation);
        SchemaConfiguration.ConfigureHash(builder.Property(value => value.ExpectedInputSha256));
        builder.Property(value => value.ExpectedInputSha256).UseCollation(SchemaConfiguration.SchedulerFenceCollation);
        builder.Property(value => value.DescriptorFingerprint).HasMaxLength(256).IsRequired().UseCollation(SchemaConfiguration.SchedulerFenceCollation);
        builder.Property(value => value.DescriptorVersion).HasMaxLength(256).IsRequired().UseCollation(SchemaConfiguration.SchedulerFenceCollation);
        builder.Property(value => value.SafetyContractId).HasMaxLength(256).IsRequired().UseCollation(SchemaConfiguration.SchedulerFenceCollation);
        builder.Property(value => value.HandlerId).HasMaxLength(256).IsRequired().UseCollation(SchemaConfiguration.SchedulerFenceCollation);
        builder.Property(value => value.ActionKind).HasMaxLength(64).IsRequired().UseCollation(SchemaConfiguration.SchedulerFenceCollation);
        builder.Property(value => value.PolicyReasonCode).HasMaxLength(128).IsRequired().UseCollation(SchemaConfiguration.SchedulerFenceCollation);
        builder.Property(value => value.OriginalBlockedRowVersion).HasColumnType("binary(8)").IsRequired();
        builder.Property(value => value.OriginalOutcomeCode).HasMaxLength(128).IsRequired().UseCollation(SchemaConfiguration.SchedulerFenceCollation);
        builder.Property(value => value.RequestedAtUtc).HasColumnType("datetimeoffset(7)"); builder.Property(value => value.ClaimExpiresAtUtc).HasColumnType("datetimeoffset(7)");
        builder.Property(value => value.ClaimedAtUtc).HasColumnType("datetimeoffset(7)"); builder.Property(value => value.TerminalAtUtc).HasColumnType("datetimeoffset(7)");
        builder.Property(value => value.TerminalReceiptFingerprint).HasColumnType("char(64)").IsUnicode(false).IsFixedLength().HasMaxLength(64).UseCollation(SchemaConfiguration.SchedulerFenceCollation);
        builder.Property(value => value.TerminalReasonCode).HasMaxLength(128).UseCollation(SchemaConfiguration.SchedulerFenceCollation);
        SchemaConfiguration.ConfigureRowVersion(builder.Property(value => value.RowVersion));
        foreach (var property in new[] { nameof(SourceProcessorForceRequestEntity.ActionId), nameof(SourceProcessorForceRequestEntity.OperationId), nameof(SourceProcessorForceRequestEntity.RequestFingerprint),
                     nameof(SourceProcessorForceRequestEntity.PolicyId), nameof(SourceProcessorForceRequestEntity.PolicyRevision),
                     nameof(SourceProcessorForceRequestEntity.DescriptorVersion), nameof(SourceProcessorForceRequestEntity.SafetyContractId),
                     nameof(SourceProcessorForceRequestEntity.HandlerId), nameof(SourceProcessorForceRequestEntity.ActionKind), nameof(SourceProcessorForceRequestEntity.PolicyReasonCode),
                     nameof(SourceProcessorForceRequestEntity.SourceActivityId), nameof(SourceProcessorForceRequestEntity.SourceProcessorBranchId), nameof(SourceProcessorForceRequestEntity.SourceRevisionId),
                     nameof(SourceProcessorForceRequestEntity.DescriptorId), nameof(SourceProcessorForceRequestEntity.DescriptorFingerprint), nameof(SourceProcessorForceRequestEntity.ExpectedInputSha256),
                     nameof(SourceProcessorForceRequestEntity.OriginalBlockedLeaseGeneration), nameof(SourceProcessorForceRequestEntity.OriginalBlockedRowVersion), nameof(SourceProcessorForceRequestEntity.OriginalOutcomeCode),
                     nameof(SourceProcessorForceRequestEntity.RequestedAtUtc), nameof(SourceProcessorForceRequestEntity.ClaimExpiresAtUtc) })
        {
            builder.Property(property).Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Throw);
        }
        builder.HasIndex(value => value.ActionId).IsUnique(); builder.HasIndex(value => value.OperationId).IsUnique();
        builder.HasIndex(value => new { value.SourceProcessorBranchId, value.DescriptorId, value.DescriptorFingerprint, value.OriginalBlockedRowVersion })
            .HasFilter("[State] IN (0, 1)").IsUnique();
        builder.HasIndex(value => new { value.State, value.ClaimExpiresAtUtc });
        builder.HasOne<SourceActivityEntity>().WithMany().HasForeignKey(value => value.SourceActivityId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<SourceProcessorBranchEntity>().WithMany().HasForeignKey(value => value.SourceProcessorBranchId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<SourceRevisionEntity>().WithMany().HasForeignKey(value => value.SourceRevisionId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<OperatorActionCapabilityPolicyEntity>().WithMany()
            .HasForeignKey(value => new
            {
                value.PolicyId, value.PolicyRevision, value.DescriptorId, value.DescriptorFingerprint,
                value.DescriptorVersion, value.SafetyContractId, value.HandlerId, value.ActionKind,
                ReasonCode = value.PolicyReasonCode
            })
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<SourceProcessorAttemptEntity>().WithMany()
            .HasForeignKey(value => new { value.ForceAttemptBranchId, value.ForceAttemptLeaseGeneration })
            .HasPrincipalKey(value => new { value.BranchId, value.LeaseGeneration })
            .OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class SourceProcessorBranchMemberConfiguration : IEntityTypeConfiguration<SourceProcessorBranchMemberEntity>
{
    public void Configure(EntityTypeBuilder<SourceProcessorBranchMemberEntity> builder)
    {
        builder.ToTable("SourceProcessorBranchMembers"); builder.HasKey(value => value.Id); builder.Property(value => value.Id).ValueGeneratedNever();
        SchemaConfiguration.ConfigureHash(builder.Property(value => value.MemberFingerprint)); builder.Property(value => value.Disposition).HasMaxLength(64).IsRequired(); builder.Property(value => value.ReasonCode).HasMaxLength(128);
        builder.Property(value => value.CreatedAtUtc).HasColumnType("datetimeoffset(7)"); builder.HasIndex(value => new { value.BranchId, value.MemberFingerprint }).IsUnique();
        builder.HasOne<SourceProcessorBranchEntity>().WithMany().HasForeignKey(value => value.BranchId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<SourceRevisionEntity>().WithMany().HasForeignKey(value => value.ChildSourceRevisionId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<SourceActivityEntity>().WithMany().HasForeignKey(value => value.ChildSourceActivityId).OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class SourceActivityRelationConfiguration : IEntityTypeConfiguration<SourceActivityRelationEntity>
{
    public void Configure(EntityTypeBuilder<SourceActivityRelationEntity> builder)
    {
        builder.ToTable("SourceActivityRelations"); builder.HasKey(value => value.Id); builder.Property(value => value.Id).ValueGeneratedNever();
        builder.Property(value => value.RelationshipKind).HasMaxLength(128).IsRequired(); builder.Property(value => value.ReasonCode).HasMaxLength(128).IsRequired(); builder.Property(value => value.CreatedAtUtc).HasColumnType("datetimeoffset(7)");
        builder.HasIndex(value => value.PredecessorActivityId).IsUnique(); builder.HasIndex(value => value.SuccessorActivityId).IsUnique();
        builder.HasOne<SourceActivityEntity>().WithMany().HasForeignKey(value => value.PredecessorActivityId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<SourceActivityEntity>().WithMany().HasForeignKey(value => value.SuccessorActivityId).OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class SourceArtifactConfiguration : IEntityTypeConfiguration<SourceArtifactEntity>
{
    public void Configure(EntityTypeBuilder<SourceArtifactEntity> builder)
    {
        builder.ToTable("SourceArtifacts", table => table.HasCheckConstraint("CK_SourceArtifacts_ContentSha256", SchemaConfiguration.Sha256CheckFor("ContentSha256")));
        builder.HasKey(entity => entity.Id);
        builder.Property(entity => entity.Id).ValueGeneratedNever();
        SchemaConfiguration.ConfigureHash(builder.Property(entity => entity.ContentSha256));
        builder.Property(entity => entity.StoreRelativePath).HasMaxLength(2048).IsRequired().UseCollation(SchemaConfiguration.SchedulerFenceCollation);
        builder.Property(entity => entity.ChecksumVerifiedAtUtc).HasColumnType("datetimeoffset(7)");
        builder.Property(entity => entity.RetainUntilUtc).HasColumnType("datetimeoffset(7)");
        builder.Property(entity => entity.RetentionEvidenceJson).HasColumnType("nvarchar(max)");
        SchemaConfiguration.ConfigureRowVersion(builder.Property(entity => entity.RowVersion));
        SchemaConfiguration.ConfigureImmutableAfterInsert(builder.Property(entity => entity.SourceRevisionId));
        SchemaConfiguration.ConfigureImmutableAfterInsert(builder.Property(entity => entity.ContentSha256));
        SchemaConfiguration.ConfigureImmutableAfterInsert(builder.Property(entity => entity.StoreRelativePath));
        SchemaConfiguration.ConfigureImmutableAfterInsert(builder.Property(entity => entity.ByteLength));
        SchemaConfiguration.ConfigureImmutableAfterInsert(builder.Property(entity => entity.ChecksumVerifiedAtUtc));
        builder.HasIndex(entity => entity.SourceRevisionId).IsUnique();
        builder.HasIndex(entity => entity.ContentSha256);
        builder.HasOne(entity => entity.SourceRevision).WithMany().HasForeignKey(entity => entity.SourceRevisionId).OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class SourceActivityConfiguration : IEntityTypeConfiguration<SourceActivityEntity>
{
    public void Configure(EntityTypeBuilder<SourceActivityEntity> builder)
    {
        builder.ToTable("SourceActivities");
        builder.HasKey(entity => entity.Id);
        builder.Property(entity => entity.Id).ValueGeneratedNever();
        builder.Property(entity => entity.ProcessorVersion).HasMaxLength(256).IsRequired().UseCollation(SchemaConfiguration.SchedulerFenceCollation);
        builder.Property(entity => entity.InputFingerprint).HasMaxLength(256).IsRequired().UseCollation(SchemaConfiguration.SchedulerFenceCollation);
        builder.Property(entity => entity.DescriptorFingerprint).HasMaxLength(64).IsUnicode(false).IsRequired()
            .HasDefaultValue(SourceActivityEntity.LegacyDescriptorFingerprint).UseCollation(SchemaConfiguration.SchedulerFenceCollation);
        builder.Property(entity => entity.RequiredCapability).HasMaxLength(256).UseCollation(SchemaConfiguration.SchedulerFenceCollation);
        builder.Property(entity => entity.Reason).HasMaxLength(1024);
        builder.Property(entity => entity.LastAttemptAtUtc).HasColumnType("datetimeoffset(7)");
        builder.Property(entity => entity.AttemptEvidenceJson).HasColumnType("nvarchar(max)");
        builder.Property(entity => entity.CreatedAtUtc).HasColumnType("datetimeoffset(7)");
        builder.Property(entity => entity.UpdatedAtUtc).HasColumnType("datetimeoffset(7)");
        SchemaConfiguration.ConfigureRowVersion(builder.Property(entity => entity.RowVersion));
        builder.HasIndex(entity => new { entity.SourceRevisionId, entity.ActivityKind, entity.ProcessorVersion, entity.DescriptorFingerprint, entity.InputFingerprint }).IsUnique();
        builder.HasIndex(entity => new { entity.State, entity.ExecutionClass });
        builder.HasOne(entity => entity.SourceRevision).WithMany().HasForeignKey(entity => entity.SourceRevisionId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(entity => entity.ResultingPipelineRecord).WithMany()
            .HasForeignKey(entity => new { entity.ResultingPipelineRecordId, entity.ResultingPipelineRecordRevision })
            .HasPrincipalKey(entity => new { entity.Id, entity.Revision })
            .OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class SourceCapabilityConfiguration : IEntityTypeConfiguration<SourceCapabilityEntity>
{
    public void Configure(EntityTypeBuilder<SourceCapabilityEntity> builder)
    {
        builder.ToTable(
            "SourceCapabilities",
            table => table.HasCheckConstraint("CK_SourceCapabilities_NativeExecutorLater_NotRunnable", "[ExecutionClass] <> 2 OR [IsRunnable] = CONVERT(bit, 0)"));
        builder.HasKey(entity => entity.Id);
        builder.Property(entity => entity.Id).ValueGeneratedNever();
        builder.Property(entity => entity.ProcessorKind).HasMaxLength(256).IsRequired().UseCollation(SchemaConfiguration.SchedulerFenceCollation);
        builder.Property(entity => entity.ProcessorVersion).HasMaxLength(256).IsRequired().UseCollation(SchemaConfiguration.SchedulerFenceCollation);
        builder.Property(entity => entity.AcceptedClassificationsJson).HasColumnType("nvarchar(max)").IsRequired();
        builder.Property(entity => entity.OutputContract).HasMaxLength(512).IsRequired();
        builder.Property(entity => entity.ProcessorFingerprint).HasMaxLength(256).IsRequired().UseCollation(SchemaConfiguration.SchedulerFenceCollation);
        builder.Property(entity => entity.RegisteredBy).HasMaxLength(256).IsRequired().UseCollation(SchemaConfiguration.SchedulerFenceCollation);
        builder.Property(entity => entity.RegisteredAtUtc).HasColumnType("datetimeoffset(7)");
        builder.Property(entity => entity.RegistrationEvidenceJson).HasColumnType("nvarchar(max)");
        SchemaConfiguration.ConfigureRowVersion(builder.Property(entity => entity.RowVersion));
        builder.HasIndex(entity => new { entity.ProcessorKind, entity.ProcessorVersion, entity.ProcessorFingerprint }).IsUnique();
        builder.HasData(new SourceCapabilityEntity
        {
            Id = new Guid("9c56d5b2-c931-4c8b-ab66-fd0601e9c1df"),
            ProcessorKind = "text-metadata",
            ProcessorVersion = "phase-3a-v1",
            ExecutionClass = 0,
            AcceptedClassificationsJson = "[\"text/plain\"]",
            OutputContract = "pipeline:extract-utf8",
            ProcessorFingerprint = "phase-3a-inprocess-text-metadata-v1",
            IsRunnable = true,
            RegisteredBy = "system",
            RegisteredAtUtc = DateTimeOffset.Parse("2026-08-06T12:00:00+00:00")
        });
    }
}
