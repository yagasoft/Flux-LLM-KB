using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence.Configurations;

public sealed class CanonicalCodeDisclosureProofConfiguration : IEntityTypeConfiguration<CanonicalCodeDisclosureProofEntity>
{
    public void Configure(EntityTypeBuilder<CanonicalCodeDisclosureProofEntity> builder)
    {
        builder.ToTable("CanonicalCodeDisclosureProofs", table =>
        {
            table.HasCheckConstraint("CK_CodeDisclosureProof_CanonicalHash", SchemaConfiguration.Sha256CheckFor("CanonicalHash"));
            table.HasCheckConstraint("CK_CodeDisclosureProof_Checksum", SchemaConfiguration.Sha256CheckFor("Checksum"));
            table.HasCheckConstraint("CK_CodeDisclosureProof_State", "[State] BETWEEN 0 AND 2");
            table.HasCheckConstraint("CK_CodeDisclosureProof_LengthCount", "[CanonicalLength] >= 0 AND [SpanCount] >= 0 AND ([State] = 0 OR [SpanCount] = 0)");
        });
        builder.HasKey(entity => new { entity.ArtifactId, entity.Fingerprint });
        builder.Property(entity => entity.Fingerprint).HasMaxLength(128).IsUnicode(false)
            .UseCollation(SchemaConfiguration.SchedulerFenceCollation);
        SchemaConfiguration.ConfigureHash(builder.Property(entity => entity.CanonicalHash));
        SchemaConfiguration.ConfigureHash(builder.Property(entity => entity.Checksum));
        builder.HasOne<ArtifactEntity>().WithMany().HasForeignKey(entity => entity.ArtifactId).OnDelete(DeleteBehavior.Cascade);
        SchemaConfiguration.ConfigureImmutableAfterInsert(builder);
    }
}

public sealed class CanonicalCodeDisclosureSpanConfiguration : IEntityTypeConfiguration<CanonicalCodeDisclosureSpanEntity>
{
    public void Configure(EntityTypeBuilder<CanonicalCodeDisclosureSpanEntity> builder)
    {
        builder.ToTable("CanonicalCodeDisclosureSpans", table =>
        {
            table.HasCheckConstraint("CK_CodeDisclosureSpan_Checksum", SchemaConfiguration.Sha256CheckFor("Checksum"));
            table.HasCheckConstraint("CK_CodeDisclosureSpan_Range", "[Start] >= 0 AND [End] > [Start] AND ([Kind] = 1 OR ([Kind] = 0 AND [End] = [Start] + 1))");
        });
        builder.HasKey(entity => new { entity.ArtifactId, entity.Fingerprint, entity.Start, entity.End, entity.Kind });
        builder.Property(entity => entity.Fingerprint).HasMaxLength(128).IsUnicode(false)
            .UseCollation(SchemaConfiguration.SchedulerFenceCollation);
        SchemaConfiguration.ConfigureHash(builder.Property(entity => entity.Checksum));
        builder.HasOne<CanonicalCodeDisclosureProofEntity>().WithMany()
            .HasForeignKey(entity => new { entity.ArtifactId, entity.Fingerprint }).OnDelete(DeleteBehavior.Cascade);
        SchemaConfiguration.ConfigureImmutableAfterInsert(builder);
    }
}
