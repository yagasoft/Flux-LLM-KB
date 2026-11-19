using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Integration.Tests.Support;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Persistence;

public sealed class GpuModelResidencyMigrationTests
{
    [Fact]
    public void Migration_extends_media_and_embedding_settlement_and_preserves_existing_capacity_slots()
    {
        using var db = new FluxKnowledgeDbContextFactory().CreateDbContext([]);
        var migrations = db.Database.GetMigrations().ToArray();
        var position = Array.FindIndex(migrations, name => name.EndsWith("_AddGpuModelResidencies", StringComparison.Ordinal));
        Assert.True(position > 0, "The GPU model residency migration is missing.");
        Assert.Equal("20261119125051_AddMediaTranscriptionGpuRequests", migrations[position - 1]);
        var sql = db.GetService<IMigrator>().GenerateScript(migrations[position - 1], migrations[position]);
        Assert.Contains("CREATE TABLE [GpuModelResidencies]", sql);
        Assert.Contains("CREATE TABLE [GpuModelDetachmentReceipts]", sql);
        Assert.Contains("ADD [ResidentDetachmentDispatchId] uniqueidentifier NULL", sql);
        Assert.Contains("ADD CONSTRAINT [FK_EmbeddingGpuRequests_GpuModelDetachmentReceipts_ResidentDetachmentDispatchId]", sql);
        Assert.Contains("ADD CONSTRAINT [CK_MediaTranscriptionGpuRequests_Cleanup]", sql);
        Assert.DoesNotContain("DROP TABLE", sql);
        Assert.DoesNotContain("ALTER TABLE [GpuCapacitySlots]", sql);
    }
}

[Collection("sql-full-text")]
public sealed class GpuModelResidencySchemaTests(NativeSqlServerFixture fixture) : IClassFixture<NativeSqlServerFixture>
{
    [NativeSqlServerFact]
    public async Task Live_slot_is_exclusive_and_release_and_detachment_evidence_survive_reuse()
    {
        var options = new DbContextOptionsBuilder<FluxKnowledgeDbContext>().UseSqlServer(fixture.ConnectionString).Options;
        await using var db = new FluxKnowledgeDbContext(options);
        var migration = db.Database.GetMigrations().SingleOrDefault(name => name.EndsWith("_AddGpuModelResidencies", StringComparison.Ordinal));
        Assert.NotNull(migration);
        const string predecessor = "20261119125051_AddMediaTranscriptionGpuRequests";
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync(predecessor);
        await db.Database.ExecuteSqlRawAsync("""
            INSERT INTO GpuCapacitySlots(SlotKey, State, UpdatedAtUtc) VALUES ('warm-slot',0,SYSUTCDATETIME());
            """);
        await migrator.MigrateAsync(migration);
        Guid residency = Guid.NewGuid(), dispatch = Guid.NewGuid(), execution = Guid.NewGuid(), batch = Guid.NewGuid();
        var deadline = DateTimeOffset.UtcNow.AddMinutes(2);
        Task InsertIdleAsync(Guid id) => db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO GpuModelResidencies(ResidencyId,SlotKey,RuntimeKey,SettingsFingerprint,ModelKey,OwnerProcessId,
                OwnerStartedAtUtc,MachineFingerprint,State,Version,IdleUntilUtc)
            VALUES ({id},'warm-slot','runtime','settings','embedding',12,SYSUTCDATETIME(),REPLICATE('a',64),1,1,{deadline})
            """);
        await InsertIdleAsync(residency);
        var duplicate = await Assert.ThrowsAsync<SqlException>(() => InsertIdleAsync(Guid.NewGuid()));
        Assert.Contains(duplicate.Number, new[] {2601, 2627});
        async Task RejectAsync(FormattableString sql, string constraint)
        {
            var error = await Assert.ThrowsAsync<SqlException>(() => db.Database.ExecuteSqlInterpolatedAsync(sql));
            Assert.Equal(547, error.Number);
            Assert.Contains(constraint, error.Message);
        }
        await RejectAsync($"UPDATE GpuModelResidencies SET State=4,IdleUntilUtc=NULL WHERE ResidencyId={residency}", "Release");
        await RejectAsync($"UPDATE GpuModelResidencies SET State=0,IdleUntilUtc=NULL WHERE ResidencyId={residency}", "Phase");
        await RejectAsync($"UPDATE GpuModelResidencies SET State=0,BorrowedDispatchId={dispatch},IdleUntilUtc=NULL WHERE ResidencyId={residency}", "Phase");
        await RejectAsync($"UPDATE GpuModelResidencies SET IdleUntilUtc=NULL WHERE ResidencyId={residency}", "Phase");
        await RejectAsync($"UPDATE GpuModelResidencies SET ChildProcessId=123 WHERE ResidencyId={residency}", "Child");
        await RejectAsync($"UPDATE GpuModelResidencies SET Version=0 WHERE ResidencyId={residency}", "Identity");
        await RejectAsync($"UPDATE GpuModelResidencies SET ModelKey='embedding ' WHERE ResidencyId={residency}", "ModelKey");
        await RejectAsync($"UPDATE GpuModelResidencies SET State=4,IdleUntilUtc=NULL,ReleasedAtUtc=SYSUTCDATETIME(),ReleaseKind=1 WHERE ResidencyId={residency}", "Release");
        Task InsertReceiptAsync() => db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO GpuModelDetachmentReceipts(DispatchId,ResidencyId,Version,BatchId,AdmissionGeneration,ExecutorKey,ExecutionInstanceId,DetachedAtUtc)
            VALUES({dispatch},{residency},1,{batch},1,'executor',{execution},SYSUTCDATETIME())
            """);
        await InsertReceiptAsync();
        Assert.Equal(2627, (await Assert.ThrowsAsync<SqlException>(InsertReceiptAsync)).Number);
        await RejectAsync($"DELETE FROM GpuModelResidencies WHERE ResidencyId={residency}", "FK_GpuModelDetachmentReceipts");
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE GpuModelResidencies SET State=4,IdleUntilUtc=NULL,ReleasedAtUtc=SYSUTCDATETIME(),ReleaseKind=0,Version=2 WHERE ResidencyId={residency}
            """);
        await InsertIdleAsync(Guid.NewGuid());
        Assert.Equal(1, await db.Database.SqlQuery<int>($"""
            SELECT COUNT(*) AS [Value] FROM GpuModelDetachmentReceipts r JOIN GpuModelResidencies m ON r.ResidencyId=m.ResidencyId
            WHERE r.DispatchId={dispatch} AND r.Version=1 AND m.State=4 AND m.Version=2
            """).SingleAsync());
        var refused = await Assert.ThrowsAsync<SqlException>(() => migrator.MigrateAsync(predecessor));
        Assert.Contains("gpu-residency-downgrade", refused.Message);
        Assert.Contains(migration!, await db.Database.GetAppliedMigrationsAsync());
    }
}
