using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence.Configurations;

public sealed class SourceVolumeJournalConfiguration : IEntityTypeConfiguration<SourceVolumeJournalCheckpointEntity>
{
    public void Configure(EntityTypeBuilder<SourceVolumeJournalCheckpointEntity> b)
    {
        b.ToTable("SourceVolumeJournalCheckpoints"); b.HasKey(x => x.VolumeIdentity);
        b.Property(x => x.VolumeIdentity).HasMaxLength(64); b.Property(x => x.JournalId).HasMaxLength(16);
        b.Property(x => x.LeaseOwner).HasMaxLength(128); b.Property(x => x.LastBatchFingerprint).HasMaxLength(64);
        b.Property(x => x.LastFailureCode).HasMaxLength(64);
        b.Property(x => x.RowVersion).IsRowVersion();
    }
}
public sealed class SourceRootScanStateConfiguration : IEntityTypeConfiguration<SourceRootScanStateEntity>
{
    public void Configure(EntityTypeBuilder<SourceRootScanStateEntity> b)
    {
        b.ToTable("SourceRootScanStates"); b.HasKey(x => x.SourceRootId);
        b.HasOne<SourceRootConfigurationEntity>().WithOne().HasForeignKey<SourceRootScanStateEntity>(x => x.SourceRootId).OnDelete(DeleteBehavior.Cascade);
        b.Property(x => x.VolumeIdentity).HasMaxLength(64); b.Property(x => x.PolicyFingerprint).HasMaxLength(64);
        b.Property(x => x.Mode).HasMaxLength(32); b.Property(x => x.Reason).HasMaxLength(128);
        b.Property(x => x.SealProfileId).HasMaxLength(64).IsUnicode(false);
        b.Property(x => x.RowVersion).IsRowVersion(); b.HasIndex(x => x.VolumeIdentity);
    }
}
public sealed class SourceInventoryConfiguration : IEntityTypeConfiguration<SourceInventoryEntryEntity>
{
    public void Configure(EntityTypeBuilder<SourceInventoryEntryEntity> b)
    {
        b.ToTable("SourceInventoryEntries"); b.HasKey(x => x.Id);
        b.HasOne<SourceRootConfigurationEntity>().WithMany().HasForeignKey(x => x.SourceRootId).OnDelete(DeleteBehavior.Cascade);
        b.Property(x => x.RelativePath).HasMaxLength(32767); b.Property(x => x.PathFingerprint).HasMaxLength(64).IsUnicode(false);
        b.Property(x => x.FileId).HasMaxLength(32).IsUnicode(false); b.Property(x => x.ParentId).HasMaxLength(32).IsUnicode(false);
        b.Property(x => x.SealProfileId).HasMaxLength(64).IsUnicode(false);
        b.Property(x => x.SealFailureCode).HasMaxLength(64).IsUnicode(false);
        b.HasIndex(x => new { x.IsSealed, x.NextForcedCheckAtUtc, x.SourceRootId });
        b.HasIndex(x => new { x.SourceRootId, x.PathFingerprint }).IsUnique();
        b.HasIndex(x => new { x.SourceRootId, x.FileId }); b.Property(x => x.RowVersion).IsRowVersion();
    }
}
public sealed class SourcePendingChangeConfiguration : IEntityTypeConfiguration<SourcePendingChangeEntity>
{
    public void Configure(EntityTypeBuilder<SourcePendingChangeEntity> b)
    {
        b.ToTable("SourcePendingChanges"); b.HasKey(x => x.Id);
        b.HasOne<SourceRootConfigurationEntity>().WithMany().HasForeignKey(x => x.SourceRootId).OnDelete(DeleteBehavior.Cascade);
        b.Property(x => x.RelativePath).HasMaxLength(32767); b.Property(x => x.PathFingerprint).HasMaxLength(64).IsUnicode(false);
        b.HasIndex(x => new { x.SourceRootId, x.PathFingerprint }).IsUnique(); b.Property(x => x.RowVersion).IsRowVersion();
    }
}
public sealed class SourceVerificationRunConfiguration : IEntityTypeConfiguration<SourceVerificationRunEntity>
{
    public void Configure(EntityTypeBuilder<SourceVerificationRunEntity> b)
    {
        b.ToTable("SourceVerificationRuns"); b.HasKey(x => x.Id);
        b.HasOne<SourceRootConfigurationEntity>().WithMany().HasForeignKey(x => x.SourceRootId).OnDelete(DeleteBehavior.Cascade);
        b.Property(x => x.State).HasMaxLength(32); b.Property(x => x.RowVersion).IsRowVersion();
        b.Property(x => x.Reason).HasMaxLength(128);
        b.HasIndex(x => new { x.SourceRootId, x.CreatedAtUtc });
        b.HasIndex(x => x.SourceRootId).IsUnique().HasFilter("[Active] = 1");
        b.HasIndex(x => new { x.Active, x.NextBatchAtUtc });
    }
}
public sealed class SourceVerificationItemConfiguration : IEntityTypeConfiguration<SourceVerificationItemEntity>
{
    public void Configure(EntityTypeBuilder<SourceVerificationItemEntity> b)
    {
        b.ToTable("SourceVerificationItems"); b.HasKey(x => x.Id);
        b.HasOne<SourceVerificationRunEntity>().WithMany().HasForeignKey(x => x.RunId).OnDelete(DeleteBehavior.Cascade);
        b.Property(x => x.State).HasMaxLength(32); b.Property(x => x.Reason).HasMaxLength(128);
        b.Property(x => x.ReceiptSha256).HasMaxLength(64).IsUnicode(false);
        b.HasIndex(x => new { x.RunId, x.State, x.AttemptedAtUtc, x.Id });
        b.HasIndex(x => new { x.RunId, x.ReceiptSha256, x.ReceiptByteLength });
        b.HasIndex(x => new { x.RunId, x.InventoryEntryId }).IsUnique(); b.Property(x => x.RowVersion).IsRowVersion();
    }
}
