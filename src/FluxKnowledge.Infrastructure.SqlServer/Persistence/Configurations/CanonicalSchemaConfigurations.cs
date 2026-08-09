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
        builder.Property(entity => entity.UpdatedAtUtc).HasColumnType("datetimeoffset(7)");
        SchemaConfiguration.ConfigureRowVersion(builder.Property(entity => entity.RowVersion));
        builder.HasIndex(entity => entity.BatchId).IsUnique();
        builder.HasIndex(entity => new { entity.State, entity.UpdatedAtUtc });
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
        builder.Property(entity => entity.RequiredCapability).HasMaxLength(256).UseCollation(SchemaConfiguration.SchedulerFenceCollation);
        builder.Property(entity => entity.Reason).HasMaxLength(1024);
        builder.Property(entity => entity.LastAttemptAtUtc).HasColumnType("datetimeoffset(7)");
        builder.Property(entity => entity.AttemptEvidenceJson).HasColumnType("nvarchar(max)");
        builder.Property(entity => entity.CreatedAtUtc).HasColumnType("datetimeoffset(7)");
        builder.Property(entity => entity.UpdatedAtUtc).HasColumnType("datetimeoffset(7)");
        SchemaConfiguration.ConfigureRowVersion(builder.Property(entity => entity.RowVersion));
        builder.HasIndex(entity => new { entity.SourceRevisionId, entity.ActivityKind, entity.ProcessorVersion, entity.InputFingerprint }).IsUnique();
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
