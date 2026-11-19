using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence.Configurations;

public sealed class GpuModelResidencyConfiguration : IEntityTypeConfiguration<GpuModelResidencyEntity>
{
    public void Configure(EntityTypeBuilder<GpuModelResidencyEntity> builder)
    {
        builder.ToTable("GpuModelResidencies", table =>
        {
            table.HasCheckConstraint("CK_GpuModelResidencies_Identity", """
                [ResidencyId] <> '00000000-0000-0000-0000-000000000000' AND [Version] > 0 AND [OwnerProcessId] > 0 AND
                DATEPART(TZOFFSET,[OwnerStartedAtUtc])=0 AND [OwnerStartedAtUtc] > '0001-01-01T00:00:00+00:00'
                """);
            table.HasCheckConstraint("CK_GpuModelResidencies_MachineFingerprint", SchemaConfiguration.Sha256CheckFor("MachineFingerprint"));
            table.HasCheckConstraint("CK_GpuModelResidencies_Child", """
                ([ChildJobId] IS NULL AND [ChildProcessId] IS NULL AND [ChildStartedAtUtc] IS NULL) OR
                ([ChildJobId] IS NOT NULL AND [ChildJobId]=[ResidencyId] AND [ChildProcessId] IS NOT NULL AND
                 [ChildProcessId]>0 AND [ChildStartedAtUtc] IS NOT NULL AND DATEPART(TZOFFSET,[ChildStartedAtUtc])=0 AND
                 [ChildStartedAtUtc]>=[OwnerStartedAtUtc])
                """);
            table.HasCheckConstraint("CK_GpuModelResidencies_Phase", """
                ([State]=0 AND [BorrowedDispatchId] IS NOT NULL AND [BorrowedDispatchId]<>'00000000-0000-0000-0000-000000000000'
                 AND [BorrowedExecutionInstanceId] IS NOT NULL AND [BorrowedExecutionInstanceId]<>'00000000-0000-0000-0000-000000000000'
                 AND [IdleUntilUtc] IS NULL) OR
                ([BorrowedDispatchId] IS NULL AND [BorrowedExecutionInstanceId] IS NULL AND
                 (([State]=1 AND [IdleUntilUtc] IS NOT NULL) OR [State]=2 OR ([State] IN (3,4) AND [IdleUntilUtc] IS NULL)))
                """);
            table.HasCheckConstraint("CK_GpuModelResidencies_Deadline", """
                [IdleUntilUtc] IS NULL OR (DATEPART(TZOFFSET,[IdleUntilUtc])=0 AND [IdleUntilUtc]>=[OwnerStartedAtUtc])
                """);
            table.HasCheckConstraint("CK_GpuModelResidencies_Release", """
                ([State]<>4 AND [ReleasedAtUtc] IS NULL AND [ReleaseKind] IS NULL) OR
                ([State]=4 AND [ReleasedAtUtc] IS NOT NULL AND DATEPART(TZOFFSET,[ReleasedAtUtc])=0 AND
                 [ReleasedAtUtc]>=[OwnerStartedAtUtc] AND ([ChildStartedAtUtc] IS NULL OR [ReleasedAtUtc]>=[ChildStartedAtUtc]) AND
                 [ReleaseKind] IS NOT NULL AND
                 (([ChildJobId] IS NULL AND [ReleaseKind] IN (0,2)) OR ([ChildJobId] IS NOT NULL AND [ReleaseKind]=1)))
                """);
            foreach (var key in new[] { "SlotKey", "RuntimeKey", "SettingsFingerprint", "ModelKey" })
                table.HasCheckConstraint($"CK_GpuModelResidencies_{key}", SchemaConfiguration.NoTrailingWhitespaceCheckFor(key, false));
        });
        builder.HasKey(row => row.ResidencyId);
        builder.Property(row => row.ResidencyId).ValueGeneratedNever();
        builder.Property(row => row.Version).IsConcurrencyToken();
        builder.Property(row => row.SlotKey).HasMaxLength(256).UseCollation(SchemaConfiguration.SchedulerFenceCollation);
        builder.Property(row => row.RuntimeKey).HasMaxLength(256).UseCollation(SchemaConfiguration.SchedulerFenceCollation);
        builder.Property(row => row.SettingsFingerprint).HasMaxLength(256).UseCollation(SchemaConfiguration.SchedulerFenceCollation);
        builder.Property(row => row.ModelKey).HasMaxLength(256).UseCollation(SchemaConfiguration.SchedulerFenceCollation);
        SchemaConfiguration.ConfigureHash(builder.Property(row => row.MachineFingerprint));
        builder.HasOne<GpuCapacitySlotEntity>().WithMany().HasForeignKey(row => row.SlotKey).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(row => row.SlotKey).IsUnique().HasFilter("[State] < 4");
    }
}
