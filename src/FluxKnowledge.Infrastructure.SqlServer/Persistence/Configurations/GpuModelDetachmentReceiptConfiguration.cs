using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence.Configurations;

public sealed class GpuModelDetachmentReceiptConfiguration : IEntityTypeConfiguration<GpuModelDetachmentReceiptEntity>
{
    public void Configure(EntityTypeBuilder<GpuModelDetachmentReceiptEntity> builder)
    {
        builder.ToTable("GpuModelDetachmentReceipts", table =>
        {
            table.HasCheckConstraint("CK_GpuModelDetachmentReceipts_Identity", """
                [DispatchId]<>'00000000-0000-0000-0000-000000000000' AND [BatchId]<>'00000000-0000-0000-0000-000000000000' AND
                [ExecutionInstanceId]<>'00000000-0000-0000-0000-000000000000' AND [Version]>0 AND [AdmissionGeneration]>0 AND
                DATEPART(TZOFFSET,[DetachedAtUtc])=0 AND [DetachedAtUtc]>'0001-01-01T00:00:00+00:00'
                """);
            table.HasCheckConstraint("CK_GpuModelDetachmentReceipts_ExecutorKey", SchemaConfiguration.NoTrailingWhitespaceCheckFor("ExecutorKey", false));
        });
        builder.HasKey(row => row.DispatchId);
        builder.Property(row => row.DispatchId).ValueGeneratedNever();
        builder.Property(row => row.ExecutorKey).HasMaxLength(256).UseCollation(SchemaConfiguration.SchedulerFenceCollation);
        builder.HasOne<GpuModelResidencyEntity>().WithMany().HasForeignKey(row => row.ResidencyId).OnDelete(DeleteBehavior.Restrict);
        // Historical dispatch and batch identifiers must survive ordinary source and job pruning.
        builder.HasIndex(row => new { row.ResidencyId, row.Version }).IsUnique();
        SchemaConfiguration.ConfigureImmutableAfterInsert(builder);
    }
}
