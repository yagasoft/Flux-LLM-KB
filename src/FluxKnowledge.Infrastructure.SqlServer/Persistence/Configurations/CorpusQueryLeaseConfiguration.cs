using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence.Configurations;

public sealed class CorpusQueryLeaseConfiguration : IEntityTypeConfiguration<CorpusQueryLeaseEntity>
{
    public void Configure(EntityTypeBuilder<CorpusQueryLeaseEntity> builder)
    {
        builder.ToTable("CorpusQueryLeases", table =>
        {
            table.HasCheckConstraint("CK_CorpusQueryLeases_Ownership",
                "[OwnerInstanceId] <> '00000000-0000-0000-0000-000000000000' AND [OwnerProcessId] > 0 AND [SqlSessionId] > 0");
            table.HasCheckConstraint("CK_CorpusQueryLeases_Stamp",
                "[CorpusEpoch] <> '00000000-0000-0000-0000-000000000000' AND [CorpusVersion] >= 0 AND [Dimensions] > 0");
            table.HasCheckConstraint("CK_CorpusQueryLeases_ModelFingerprint", SchemaConfiguration.NoTrailingWhitespaceCheckFor("ModelFingerprint", false));
            table.HasCheckConstraint("CK_CorpusQueryLeases_MachineFingerprint", SchemaConfiguration.Sha256CheckFor("OwnerMachineFingerprint"));
        });
        builder.HasKey(value => value.Id);
        builder.Property(value => value.ModelFingerprint).HasMaxLength(256).UseCollation("Latin1_General_100_BIN2");
        builder.Property(value => value.OwnerMachineFingerprint).HasMaxLength(64).IsFixedLength().UseCollation("Latin1_General_100_BIN2");
        builder.HasIndex(value => value.GenerationId);
        // A shared SQL lock plus this process-incarnation record fences cleanup. Lost
        // sessions retain the record until explicit release or proven process exit.
        // No FK prevents the exclusive maintenance owner from retiring a proven dead lease.
    }
}
