using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence.Configurations;

public sealed class CorpusRebuildOperationConfiguration : IEntityTypeConfiguration<CorpusRebuildOperationEntity>
{
    public void Configure(EntityTypeBuilder<CorpusRebuildOperationEntity> builder)
    {
        builder.ToTable("CorpusRebuildOperations", table => table.HasCheckConstraint(
            "CK_CorpusRebuildOperations_ManifestJson", "ISJSON([ManifestJson]) = 1"));
        builder.HasKey(value => value.Id);
        builder.Property(value => value.Id).ValueGeneratedNever();
        builder.HasIndex(value => value.TargetEpoch).IsUnique();
        SchemaConfiguration.ConfigureHash(builder.Property(value => value.ManifestHash));
        builder.Property(value => value.ManifestJson).IsRequired();
        builder.Property(value => value.CreatedAtUtc).HasColumnType("datetimeoffset(7)");
        builder.Property(value => value.CompletedAtUtc).HasColumnType("datetimeoffset(7)");
        SchemaConfiguration.ConfigureRowVersion(builder.Property(value => value.RowVersion));
    }
}

public sealed class CorpusRebuildWorkItemConfiguration : IEntityTypeConfiguration<CorpusRebuildWorkItemEntity>
{
    public void Configure(EntityTypeBuilder<CorpusRebuildWorkItemEntity> builder)
    {
        builder.ToTable("CorpusRebuildWorkItems", table => table.HasCheckConstraint("CK_CorpusRebuildWorkItems_State", "[State] IN (0, 1, 2)"));
        builder.HasKey(value => new { value.OperationId, value.PipelineRecordId });
        builder.HasIndex(value => new { value.OperationId, value.State });
        builder.HasIndex(value => value.EmbeddingJobId).IsUnique();
        builder.HasIndex(value => value.DispatchMessageId).IsUnique();
        builder.HasOne<CorpusRebuildOperationEntity>().WithMany().HasForeignKey(value => value.OperationId).OnDelete(DeleteBehavior.Restrict);
        // Receipts outlive source deletion. Identifiers are captured history, not live source FKs.
        builder.Property(value => value.PreparedAtUtc).HasColumnType("datetimeoffset(7)");
        builder.Property(value => value.CompletedAtUtc).HasColumnType("datetimeoffset(7)");
        SchemaConfiguration.ConfigureRowVersion(builder.Property(value => value.RowVersion));
    }
}
