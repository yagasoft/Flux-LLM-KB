using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence.Configurations;

public sealed class IndexRecoveryAuditConfiguration : IEntityTypeConfiguration<IndexRecoveryAuditEntity>
{
    public void Configure(EntityTypeBuilder<IndexRecoveryAuditEntity> builder)
    {
        builder.ToTable("IndexRecoveryAudit", table => table.HasCheckConstraint("CK_IndexRecoveryAudit_Singleton", "[Id]=1"));
        builder.HasKey(value => value.Id);
        builder.Property(value => value.Id).ValueGeneratedNever();
        builder.Property(value => value.RecoveryRoot).HasMaxLength(2048).IsRequired();
        builder.Property(value => value.IndexRoot).HasMaxLength(2048).IsRequired();
        builder.Property(value => value.RootFingerprint).HasMaxLength(256).IsRequired();
        builder.Property(value => value.InventoryChecksum).HasMaxLength(64).IsRequired();
        builder.Property(value => value.FailurePath).HasMaxLength(2048);
        builder.Property(value => value.SealedAtUtc).HasColumnType("datetimeoffset(7)");
        SchemaConfiguration.ConfigureRowVersion(builder.Property(value => value.RowVersion));
        builder.HasData(new IndexRecoveryAuditEntity { Id = 1 });
    }
}

public sealed class IndexRecoveryPinConfiguration : IEntityTypeConfiguration<IndexRecoveryPinEntity>
{
    public void Configure(EntityTypeBuilder<IndexRecoveryPinEntity> builder)
    {
        builder.ToTable("IndexRecoveryPins");
        builder.HasKey(value => value.Id);
        SchemaConfiguration.ConfigureHash(builder.Property(value => value.Id));
        builder.Property(value => value.Packet).HasMaxLength(2048).IsRequired();
        builder.Property(value => value.IndexPath).HasMaxLength(2048);
        builder.HasIndex(value => value.GenerationId);
    }
}

public sealed class IndexRecoveryWriterConfiguration : IEntityTypeConfiguration<IndexRecoveryWriterEntity>
{
    public void Configure(EntityTypeBuilder<IndexRecoveryWriterEntity> builder)
    {
        builder.ToTable("IndexRecoveryWriters");
        builder.HasKey(value => value.Id);
        builder.Property(value => value.Id).ValueGeneratedNever();
        builder.Property(value => value.RecoveryRoot).HasMaxLength(2048).IsRequired();
        SchemaConfiguration.ConfigureHash(builder.Property(value => value.OwnerMachineFingerprint));
        builder.Property(value => value.OwnerStartedAtUtc).HasColumnType("datetimeoffset(7)");
        builder.Property(value => value.CreatedAtUtc).HasColumnType("datetimeoffset(7)");
        builder.Property(value => value.OwnerExitSufficient).HasDefaultValue(false);
    }
}
