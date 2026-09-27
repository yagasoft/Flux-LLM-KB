using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence.Configurations;

public sealed class EmbeddingGpuRequestConfiguration : IEntityTypeConfiguration<EmbeddingGpuRequestEntity>
{
    public void Configure(EntityTypeBuilder<EmbeddingGpuRequestEntity> builder)
    {
        builder.ToTable("EmbeddingGpuRequests", table =>
        {
            table.HasCheckConstraint("CK_EmbeddingGpuRequests_Inputs", "ISJSON([InputsJson]) = 1 AND DATALENGTH([InputsJson]) BETWEEN 2 AND 4096");
            table.HasCheckConstraint("CK_EmbeddingGpuRequests_InputDigest", SchemaConfiguration.Sha256CheckFor("InputDigest"));
            table.HasCheckConstraint("CK_EmbeddingGpuRequests_State", "[State] BETWEEN 0 AND 2");
            table.HasCheckConstraint("CK_EmbeddingGpuRequests_Profile", "[Dimensions] BETWEEN 1 AND 4096 AND DATALENGTH([ModelFingerprint]) > 0");
            table.HasCheckConstraint("CK_EmbeddingGpuRequests_Result", "[ResultDigest] IS NULL OR DATALENGTH([ResultDigest]) = 32");
            table.HasCheckConstraint("CK_EmbeddingGpuRequests_Cleanup", "([NativeCleanupConfirmed] = 0 AND [CleanupConfirmedAtUtc] IS NULL) OR ([NativeCleanupConfirmed] = 1 AND [CleanupConfirmedAtUtc] IS NOT NULL)");
            table.HasCheckConstraint("CK_EmbeddingGpuRequests_Owner", "([ExecutorInstanceId] IS NULL AND [ClaimOperationId] IS NULL AND [OwnerProcessId] IS NULL AND [OwnerStartedAtUtc] IS NULL AND [OwnerMachineFingerprint] IS NULL AND [DispatchId] IS NULL) OR ([ExecutorInstanceId] IS NOT NULL AND [ExecutorInstanceId] <> '00000000-0000-0000-0000-000000000000' AND [ClaimOperationId] IS NOT NULL AND [ClaimOperationId] <> '00000000-0000-0000-0000-000000000000' AND [OwnerProcessId] IS NOT NULL AND [OwnerProcessId] > 0 AND [OwnerStartedAtUtc] IS NOT NULL AND [DispatchId] IS NOT NULL AND [OwnerMachineFingerprint] IS NOT NULL AND " + SchemaConfiguration.Sha256CheckFor("OwnerMachineFingerprint") + ")");
            table.HasCheckConstraint("CK_EmbeddingGpuRequests_Completion", "[State] = 0 OR ([State] = 1 AND [ResultDigest] IS NOT NULL AND [ExecutorInstanceId] IS NOT NULL) OR ([State] = 2 AND [NativeCleanupConfirmed] = 1)");
        });
        builder.HasKey(value => value.MiniTaskId);
        builder.Property(value => value.MiniTaskId).ValueGeneratedNever();
        builder.Property(value => value.InputsJson).HasMaxLength(2048).IsRequired();
        builder.Property(value => value.InputDigest).HasMaxLength(64).IsRequired().UseCollation(SchemaConfiguration.SchedulerFenceCollation);
        builder.Property(value => value.ModelFingerprint).HasMaxLength(256).IsRequired().UseCollation(SchemaConfiguration.SchedulerFenceCollation);
        builder.Property(value => value.OwnerMachineFingerprint).HasMaxLength(64).UseCollation(SchemaConfiguration.SchedulerFenceCollation);
        builder.Property(value => value.OwnerStartedAtUtc).HasColumnType("datetimeoffset(7)");
        builder.Property(value => value.CleanupConfirmedAtUtc).HasColumnType("datetimeoffset(7)");
        builder.Property(value => value.CreatedAtUtc).HasColumnType("datetimeoffset(7)");
        builder.Property(value => value.UpdatedAtUtc).HasColumnType("datetimeoffset(7)");
        SchemaConfiguration.ConfigureRowVersion(builder.Property(value => value.RowVersion));
        builder.HasIndex(value => new { value.ParentJobId, value.State });
        builder.HasIndex(value => new { value.ParentJobId, value.GenerationId, value.InputDigest }).IsUnique().HasFilter("[State] < 2");
        builder.HasIndex(value => new { value.PipelineRecordId, value.SourceRevision, value.State });
    }
}
