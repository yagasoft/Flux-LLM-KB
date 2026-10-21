using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence.Configurations;

public sealed class IndexSegmentConfiguration : IEntityTypeConfiguration<IndexSegmentEntity>
{
    public void Configure(EntityTypeBuilder<IndexSegmentEntity> builder)
    {
        builder.ToTable("IndexSegments", table =>
        {
            table.HasCheckConstraint("CK_IndexSegments_State", "[State] BETWEEN 0 AND 3");
            table.HasCheckConstraint("CK_IndexSegments_Profile", "[Dimensions] > 0 AND [VectorCount] > 0 AND [FileLength] > 0");
        });
        builder.HasKey(value => value.Id);
        SchemaConfiguration.ConfigureHash(builder.Property(value => value.Id));
        SchemaConfiguration.ConfigureHash(builder.Property(value => value.MembershipChecksum));
        SchemaConfiguration.ConfigureHash(builder.Property(value => value.FileChecksum));
        builder.Property(value => value.ModelFingerprint).HasMaxLength(256).IsRequired();
        builder.Property(value => value.NativeFormat).HasMaxLength(128).IsRequired();
        builder.Property(value => value.DirectoryPath).HasMaxLength(2048).IsRequired();
        builder.Property(value => value.CreatedAtUtc).HasColumnType("datetimeoffset(7)");
        builder.Property(value => value.SealedAtUtc).HasColumnType("datetimeoffset(7)");
        builder.Property(value => value.ReclaimedAtUtc).HasColumnType("datetimeoffset(7)");
        builder.HasOne<IndexPublicationCandidateEntity>().WithMany().HasForeignKey(value => value.ProducingCandidateId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(value => new { value.State, value.CreatedAtUtc });
        SchemaConfiguration.ConfigureRowVersion(builder.Property(value => value.RowVersion));
    }
}

public sealed class IndexSegmentVectorConfiguration : IEntityTypeConfiguration<IndexSegmentVectorEntity>
{
    public void Configure(EntityTypeBuilder<IndexSegmentVectorEntity> builder)
    {
        builder.ToTable("IndexSegmentVectors");
        builder.HasKey(value => new { value.SegmentId, value.VectorId });
        SchemaConfiguration.ConfigureHash(builder.Property(value => value.SegmentId));
        builder.HasIndex(value => value.VectorId);
        builder.HasOne<IndexSegmentEntity>().WithMany().HasForeignKey(value => value.SegmentId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<VectorEntity>().WithMany().HasForeignKey(value => value.VectorId).OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class IndexGenerationSegmentConfiguration : IEntityTypeConfiguration<IndexGenerationSegmentEntity>
{
    public void Configure(EntityTypeBuilder<IndexGenerationSegmentEntity> builder)
    {
        builder.ToTable("IndexGenerationSegments", table => table.HasCheckConstraint("CK_IndexGenerationSegments_Ordinal", "[Ordinal] >= 0"));
        builder.HasKey(value => new { value.GenerationId, value.SegmentId });
        SchemaConfiguration.ConfigureHash(builder.Property(value => value.SegmentId));
        builder.HasIndex(value => new { value.GenerationId, value.Ordinal }).IsUnique();
        builder.HasOne<IndexGenerationEntity>().WithMany().HasForeignKey(value => value.GenerationId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<IndexSegmentEntity>().WithMany().HasForeignKey(value => value.SegmentId).OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class CurrentIndexMemberConfiguration : IEntityTypeConfiguration<CurrentIndexMemberEntity>
{
    public void Configure(EntityTypeBuilder<CurrentIndexMemberEntity> builder)
    {
        builder.ToTable("CurrentIndexMembers");
        builder.HasKey(value => new { value.CorpusEpoch, value.VectorId });
        SchemaConfiguration.ConfigureHash(builder.Property(value => value.SegmentId));
        builder.HasIndex(value => new { value.CorpusEpoch, value.OwnerSourceRevisionId, value.VectorId });
        builder.HasIndex(value => new { value.CorpusEpoch, value.SourceIdentityId, value.VectorId });
        builder.HasIndex(value => new { value.CorpusEpoch, value.SourceRootId, value.VectorId });
        builder.HasOne<IndexSegmentVectorEntity>().WithMany().HasForeignKey(value => new { value.SegmentId, value.VectorId }).OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class IndexProjectionChangeConfiguration : IEntityTypeConfiguration<IndexProjectionChangeEntity>
{
    public void Configure(EntityTypeBuilder<IndexProjectionChangeEntity> builder)
    {
        builder.ToTable("IndexProjectionChanges", table => table.HasCheckConstraint("CK_IndexProjectionChanges_Scope", "[CorpusVersion] >= 0 AND [ScopeKind] BETWEEN 1 AND 4"));
        builder.HasKey(value => new { value.CorpusEpoch, value.CorpusVersion, value.ScopeKind, value.ScopeId });
        builder.Property(value => value.CreatedAtUtc).HasColumnType("datetimeoffset(7)");
    }
}

public sealed class IndexPublicationCandidateConfiguration : IEntityTypeConfiguration<IndexPublicationCandidateEntity>
{
    public void Configure(EntityTypeBuilder<IndexPublicationCandidateEntity> builder)
    {
        builder.ToTable("IndexPublicationCandidates", table =>
        {
            table.HasCheckConstraint("CK_IndexPublicationCandidates_State", "[State] BETWEEN 0 AND 3 AND [ExpectedCorpusVersion] >= 0 AND [PublicationCorpusVersion] >= [ExpectedCorpusVersion]");
            table.HasCheckConstraint("CK_IndexPublicationCandidates_Purpose", "[BuildPurpose] BETWEEN 0 AND 2 AND [ExpectedIndexFormat] IN (1, 2) AND ([BuildPurpose] <> 0 OR [ExpectedIndexFormat] = 2)");
        });
        builder.HasKey(value => value.Id);
        builder.Property(value => value.Id).ValueGeneratedNever();
        builder.Property(value => value.BuildPurpose).HasDefaultValue(0);
        builder.Property(value => value.ExpectedIndexFormat).HasDefaultValue(2);
        builder.Property(value => value.OwnerLeaseOwner).HasMaxLength(256);
        builder.Property(value => value.AffectedScopesJson).HasColumnType("nvarchar(max)").IsRequired();
        builder.Property(value => value.RepackedSegmentsJson).HasMaxLength(1024).HasDefaultValue("[]").IsRequired();
        SchemaConfiguration.ConfigureHash(builder.Property(value => value.BeforeChecksum));
        SchemaConfiguration.ConfigureHash(builder.Property(value => value.AfterChecksum));
        SchemaConfiguration.ConfigureHash(builder.Property(value => value.LastPageChecksum));
        SchemaConfiguration.ConfigureHash(builder.Property(value => value.OwnerMachineFingerprint));
        builder.Property(value => value.OwnerStartedAtUtc).HasColumnType("datetimeoffset(7)");
        builder.Property(value => value.CreatedAtUtc).HasColumnType("datetimeoffset(7)");
        builder.Property(value => value.CompletedAtUtc).HasColumnType("datetimeoffset(7)");
        builder.HasOne<IndexGenerationEntity>().WithMany().HasForeignKey(value => value.BaseGenerationId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<IndexGenerationEntity>().WithMany().HasForeignKey(value => value.ResultGenerationId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<IndexGenerationEntity>().WithMany().HasForeignKey(value => value.DraftGenerationId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<JobEntity>().WithMany().HasForeignKey(value => value.OwnerJobId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(value => new { value.State, value.CreatedAtUtc });
        SchemaConfiguration.ConfigureRowVersion(builder.Property(value => value.RowVersion));
    }
}

public sealed class IndexCandidateMemberConfiguration : IEntityTypeConfiguration<IndexCandidateMemberEntity>
{
    public void Configure(EntityTypeBuilder<IndexCandidateMemberEntity> builder)
    {
        builder.ToTable("IndexCandidateMembers", table => table.HasCheckConstraint("CK_IndexCandidateMembers_Phase", "[Phase] IN (0, 1) AND [VectorId] > 0"));
        builder.HasKey(value => new { value.CandidateId, value.Phase, value.VectorId });
        SchemaConfiguration.ConfigureHash(builder.Property(value => value.PayloadChecksum));
        builder.HasIndex(value => value.VectorId);
        builder.HasOne<IndexPublicationCandidateEntity>().WithMany().HasForeignKey(value => value.CandidateId).OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class IndexSegmentOwnerConfiguration : IEntityTypeConfiguration<IndexSegmentOwnerEntity>
{
    public void Configure(EntityTypeBuilder<IndexSegmentOwnerEntity> builder)
    {
        builder.ToTable("IndexSegmentOwners");
        builder.HasKey(value => value.Id);
        SchemaConfiguration.ConfigureHash(builder.Property(value => value.SegmentId));
        SchemaConfiguration.ConfigureHash(builder.Property(value => value.OwnerMachineFingerprint));
        builder.Property(value => value.OwnerStartedAtUtc).HasColumnType("datetimeoffset(7)");
        builder.Property(value => value.CreatedAtUtc).HasColumnType("datetimeoffset(7)");
        builder.HasOne<IndexSegmentEntity>().WithMany().HasForeignKey(value => value.SegmentId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(value => new { value.SegmentId, value.OwnerInstanceId }).IsUnique();
        SchemaConfiguration.ConfigureRowVersion(builder.Property(value => value.RowVersion));
    }
}
