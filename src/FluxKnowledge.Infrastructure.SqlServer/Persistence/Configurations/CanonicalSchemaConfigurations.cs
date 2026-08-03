using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
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
