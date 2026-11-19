using FluxKnowledge.Application.Gpu;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence.Configurations;

public sealed class MediaTranscriptionGpuRequestConfiguration : IEntityTypeConfiguration<MediaTranscriptionGpuRequestEntity>
{
    public void Configure(EntityTypeBuilder<MediaTranscriptionGpuRequestEntity> builder)
    {
        builder.ToTable("MediaTranscriptionGpuRequests", table =>
        {
            table.HasCheckConstraint("CK_MediaTranscriptionGpuRequests_ContentSha256", SchemaConfiguration.Sha256CheckFor("ContentSha256"));
            table.HasCheckConstraint("CK_MediaTranscriptionGpuRequests_Phase", "[ExecutionPhase] BETWEEN 0 AND 4");
            table.HasCheckConstraint("CK_MediaTranscriptionGpuRequests_Execution",
                """
                ([ExecutionPhase]=0 AND [ExecutionInstanceId] IS NULL) OR
                ([ExecutionPhase]>0 AND [ExecutionInstanceId] IS NOT NULL AND [ExecutionBatchId] IS NOT NULL AND
                 [ExecutionDispatchId] IS NOT NULL AND [ExecutionAdmissionGeneration] IS NOT NULL AND [ExecutionAdmissionGeneration]>0 AND
                 [SupervisorInstanceId] IS NOT NULL AND [SupervisorProcessId] IS NOT NULL AND [SupervisorProcessId]>0 AND
                 [SupervisorProcessStartedAtUtc] IS NOT NULL AND [MachineFingerprint] IS NOT NULL AND
                 [ClaimedAtUtc] IS NOT NULL AND [DeadlineAtUtc] IS NOT NULL AND [DeadlineAtUtc]>[ClaimedAtUtc])
                """);
            table.HasCheckConstraint("CK_MediaTranscriptionGpuRequests_Native",
                """
                ([NativeProcessId] IS NULL AND [NativeProcessStartedAtUtc] IS NULL AND [ExecutionPhase]<>2) OR
                ([NativeProcessId] IS NOT NULL AND [NativeProcessId]>0 AND [NativeProcessStartedAtUtc] IS NOT NULL AND [ExecutionPhase]>=2)
                """);
            table.HasCheckConstraint("CK_MediaTranscriptionGpuRequests_Cleanup",
                """
                ([ExecutionPhase]<3 AND [CleanupConfirmedAtUtc] IS NULL AND [CleanupKind] IS NULL) OR
                ([ExecutionPhase]>=3 AND [CleanupConfirmedAtUtc] IS NOT NULL AND [CleanupKind] IS NOT NULL AND
                 ([CleanupKind]=0 AND [NativeProcessId] IS NULL OR [CleanupKind]=1 AND [NativeProcessId] IS NOT NULL))
                """);
            table.HasCheckConstraint("CK_MediaTranscriptionGpuRequests_Result",
                $"""
                 ([ResultJson] IS NULL AND [ResultDigest] IS NULL AND [ExecutionPhase]<4) OR
                 ([ExecutionPhase]>=3 AND [ResultJson] IS NOT NULL AND DATALENGTH([ResultJson]) BETWEEN 1 AND {MediaTranscriptionGpuContracts.MaximumResultBytes} AND
                  [ResultDigest] IS NOT NULL AND DATALENGTH([ResultDigest])=32)
                 """);
            table.HasCheckConstraint("CK_MediaTranscriptionGpuRequests_Requeue", "[RequeuedAtUtc] IS NULL OR [ExecutionPhase]=4");
            table.HasCheckConstraint("CK_MediaTranscriptionGpuRequests_ModelRuntimeKey", SchemaConfiguration.NoTrailingWhitespaceCheckFor("ModelRuntimeKey", false));
            table.HasCheckConstraint("CK_MediaTranscriptionGpuRequests_SettingsFingerprint", SchemaConfiguration.NoTrailingWhitespaceCheckFor("SettingsFingerprint", false));
        });
        builder.HasKey(entity => entity.MiniTaskId);
        builder.Property(entity => entity.MiniTaskId).ValueGeneratedNever();
        SchemaConfiguration.ConfigureHash(builder.Property(entity => entity.ContentSha256));
        builder.Property(entity => entity.ContentSha256).UseCollation(SchemaConfiguration.SchedulerFenceCollation);
        builder.Property(entity => entity.ModelRuntimeKey).HasMaxLength(256).UseCollation(SchemaConfiguration.SchedulerFenceCollation);
        builder.Property(entity => entity.SettingsFingerprint).HasMaxLength(256).UseCollation(SchemaConfiguration.SchedulerFenceCollation);
        builder.Property(entity => entity.MachineFingerprint).HasColumnType("char(64)").IsUnicode(false).HasMaxLength(64);
        builder.Property(entity => entity.ResultJson).HasColumnType("nvarchar(max)");
        builder.Property(entity => entity.ResultDigest).HasColumnType("varbinary(32)");
        SchemaConfiguration.ConfigureRowVersion(builder.Property(entity => entity.RowVersion));
        builder.HasIndex(entity => new { entity.PipelineRecordId, entity.SourceRevision });
        builder.HasIndex(entity => new { entity.ParentJobId, entity.RetainedSourceRevisionId }).IsUnique();
        builder.HasIndex(entity => new { entity.ExecutionPhase, entity.RequeuedAtUtc, entity.CreatedAtUtc });
    }
}
