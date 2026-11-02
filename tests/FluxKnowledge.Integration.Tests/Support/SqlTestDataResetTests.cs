using FluxKnowledge.Domain.Jobs;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Support;

public sealed class SqlTestDataResetTests(NativeSqlServerFixture fixture) : IClassFixture<NativeSqlServerFixture>
{
    [NativeSqlServerFact]
    public async Task Reset_is_repeatable_and_preserves_the_corpus_identity_and_seed_contract()
    {
        await SqlTestData.ClearPipelineAsync(fixture);
        await SqlTestData.SeedWorkItemAsync(fixture, DateTimeOffset.UtcNow, PublicJobState.WorkerQueued, null);
        Guid epoch;
        await using (var setup = CreateContext())
        {
            var state = await setup.IndexState.SingleAsync();
            epoch = state.CorpusEpoch;
            state.CorpusVersion = 19;
            state.RequiredIndexFormat = 2;
            var scheduler = await setup.GpuSchedulerStates.SingleAsync();
            scheduler.WakeGeneration = 7;
            scheduler.SearchBatchesWhileOcrWaiting = 2;
            scheduler.PendingWakeReasons = 1;
            scheduler.NextDeferredAtUtc = DateTimeOffset.UtcNow;
            var slot = new GpuCapacitySlotEntity { SlotKey = "reset-slot", UpdatedAtUtc = DateTimeOffset.UtcNow };
            var batch = new GpuBatchEntity { Id = Guid.NewGuid(), CapacitySlotKey = slot.SlotKey,
                ModelRuntimeKey = "reset-model", SettingsFingerprint = "reset-settings", OwnerKey = "reset-owner" };
            setup.GpuCapacitySlots.Add(slot);
            setup.GpuBatches.Add(batch);
            var generation = new IndexGenerationEntity { Id = Guid.NewGuid(), ModelFingerprint = "reset-model",
                Dimensions = 2, IndexPath = "owned-reset-index", MetadataChecksum = new('a', 64), CreatedAtUtc = DateTimeOffset.UtcNow };
            setup.IndexGenerations.Add(generation);
            state.ActiveIndexGenerationId = generation.Id;
            var original = new CorpusRebuildOperationEntity { Id = Guid.NewGuid(), TargetEpoch = Guid.NewGuid(),
                ManifestHash = new('b', 64), ManifestJson = "{}" };
            var replacement = new CorpusRebuildOperationEntity { Id = Guid.NewGuid(), TargetEpoch = epoch,
                ManifestHash = new('c', 64), ManifestJson = "{}", SupersedesOperationId = original.Id };
            setup.CorpusRebuildOperations.AddRange(original, replacement);
            state.CorpusRebuildOperationId = replacement.Id;
            setup.IndexRecoveryPins.Add(new() { Id = new('d', 64), Packet = "{}", GenerationId = generation.Id });
            setup.IndexRecoveryWriters.Add(new() { Id = Guid.NewGuid(), RecoveryRoot = "owned-reset-recovery",
                OwnerProcessId = Environment.ProcessId, OwnerStartedAtUtc = DateTimeOffset.UtcNow,
                OwnerMachineFingerprint = "reset-test", CreatedAtUtc = DateTimeOffset.UtcNow });
            var audit = await setup.IndexRecoveryAudit.SingleAsync();
            audit.IsValid = true;
            audit.DatabaseIncarnation = epoch;
            audit.InventoryChecksum = new('e', 64);
            audit.FailurePath = "owned-reset-failure";
            await setup.SaveChangesAsync();
            slot.ActiveBatchId = batch.Id;
            await setup.SaveChangesAsync();
        }
        await SqlTestData.ClearPipelineAsync(fixture);
        await SqlTestData.ClearPipelineAsync(fixture);
        await using var verification = CreateContext();
        Assert.Empty(await verification.SourceIdentities.ToArrayAsync());
        Assert.Empty(await verification.PipelineRecords.ToArrayAsync());
        Assert.Empty(await verification.Jobs.ToArrayAsync());
        Assert.Empty(await verification.OutboxMessages.ToArrayAsync());
        Assert.Empty(await verification.GpuCapacitySlots.ToArrayAsync());
        Assert.Empty(await verification.GpuBatches.ToArrayAsync());
        Assert.Empty(await verification.IndexGenerations.ToArrayAsync());
        Assert.Empty(await verification.CorpusRebuildOperations.ToArrayAsync());
        Assert.Empty(await verification.IndexRecoveryPins.ToArrayAsync());
        Assert.Empty(await verification.IndexRecoveryWriters.ToArrayAsync());
        var auditReset = await verification.IndexRecoveryAudit.SingleAsync();
        Assert.False(auditReset.IsValid);
        Assert.Null(auditReset.FailurePath);
        Assert.Equal(epoch, auditReset.DatabaseIncarnation);
        Assert.Equal(new string('e', 64), auditReset.InventoryChecksum);
        var reset = await verification.IndexState.SingleAsync();
        Assert.Equal(epoch, reset.CorpusEpoch);
        Assert.Equal(19, reset.CorpusVersion);
        Assert.Equal(19, reset.ProjectionChangeFloorVersion);
        Assert.Equal(1, reset.RequiredIndexFormat);
        Assert.Null(reset.ActiveIndexGenerationId);
        Assert.Null(reset.CorpusRebuildOperationId);
        var schedulerReset = await verification.GpuSchedulerStates.SingleAsync();
        Assert.Equal(0, schedulerReset.WakeGeneration);
        Assert.Equal(0, schedulerReset.SearchBatchesWhileOcrWaiting);
        Assert.Equal(0, schedulerReset.PendingWakeReasons);
        Assert.Null(schedulerReset.NextDeferredAtUtc);
        Assert.Null(schedulerReset.InFlightWakeOperationId);
        Assert.Null(schedulerReset.InFlightWakeGeneration);
        Assert.Equal(0, schedulerReset.InFlightWakeReasons);
        Assert.Null(schedulerReset.InFlightNextDeferredAtUtc);
        Assert.Null(schedulerReset.InFlightEffectiveAdmissionReasons);
        Assert.Equal(DateTimeOffset.UnixEpoch, schedulerReset.UpdatedAtUtc);
    }

    [NativeSqlServerFact]
    public async Task Reset_stops_at_a_failed_statement_and_preserves_later_work_and_recovery_evidence()
    {
        await SqlTestData.ClearPipelineAsync(fixture);
        await SqlTestData.SeedWorkItemAsync(fixture, DateTimeOffset.UtcNow, PublicJobState.WorkerQueued, null);
        await using var setup = CreateContext();
        await setup.Database.ExecuteSqlRawAsync("""
            UPDATE dbo.IndexRecoveryAudit SET IsValid=1;
            ALTER TABLE dbo.IndexRecoveryAudit ADD CONSTRAINT CK_TestResetFailure CHECK (IsValid=1);
            """);
        try
        {
            var failure = await Assert.ThrowsAsync<SqlException>(() => SqlTestData.ClearPipelineAsync(fixture));
            Assert.Equal(547, failure.Number);
            await using var verification = CreateContext();
            Assert.Single(await verification.OutboxMessages.ToArrayAsync());
            Assert.Single(await verification.Jobs.ToArrayAsync());
            Assert.Single(await verification.PipelineRecords.ToArrayAsync());
            Assert.True((await verification.IndexRecoveryAudit.SingleAsync()).IsValid);
        }
        finally
        {
            await setup.Database.ExecuteSqlRawAsync("ALTER TABLE dbo.IndexRecoveryAudit DROP CONSTRAINT CK_TestResetFailure;");
            await SqlTestData.ClearPipelineAsync(fixture);
        }
    }

    private FluxKnowledgeDbContext CreateContext() => new(
        new DbContextOptionsBuilder<FluxKnowledgeDbContext>().UseSqlServer(fixture.ConnectionString).Options);
}
