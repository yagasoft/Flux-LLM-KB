using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence.Configurations;

public sealed class TextChunkWindowEligibilityConfiguration : IEntityTypeConfiguration<TextChunkWindowEligibilityEntity>
{
    public void Configure(EntityTypeBuilder<TextChunkWindowEligibilityEntity> builder)
    {
        builder.ToTable("TextChunkWindowEligibilities");
        builder.HasKey(value => value.TextChunkId);
        builder.Property(value => value.TextChunkId).ValueGeneratedNever();
        SchemaConfiguration.ConfigureHash(builder.Property(value => value.CanonicalContentHash));
        SchemaConfiguration.ConfigureHash(builder.Property(value => value.ChunkContentHash));
        builder.Property(value => value.RuleVersion).HasMaxLength(128).UseCollation(SchemaConfiguration.SchedulerFenceCollation).IsRequired();
        builder.Property(value => value.CalculatedAtUtc).HasColumnType("datetimeoffset(7)");
        builder.HasOne(value => value.TextChunk).WithOne(value => value.WindowEligibility)
            .HasForeignKey<TextChunkWindowEligibilityEntity>(value => value.TextChunkId).OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class TextChunkWindowBackfillStateConfiguration : IEntityTypeConfiguration<TextChunkWindowBackfillStateEntity>
{
    public void Configure(EntityTypeBuilder<TextChunkWindowBackfillStateEntity> builder)
    {
        builder.ToTable("TextChunkWindowBackfillStates", table =>
        {
            table.HasCheckConstraint("CK_TextChunkWindowBackfillStates_Id", "[Id] = 1");
            table.HasCheckConstraint("CK_TextChunkWindowBackfillStates_Cursor", "[LastChunkId] >= 0 AND [EndChunkId] >= 0 AND ([RevisitFromChunkId] IS NULL OR [RevisitFromChunkId] > 0)");
        });
        builder.HasKey(value => value.Id);
        builder.Property(value => value.Id).ValueGeneratedNever();
        builder.Property(value => value.RuleVersion).HasMaxLength(128).UseCollation(SchemaConfiguration.SchedulerFenceCollation).IsRequired();
        builder.Property(value => value.UpdatedAtUtc).HasColumnType("datetimeoffset(7)");
        SchemaConfiguration.ConfigureRowVersion(builder.Property(value => value.RowVersion));
    }
}
