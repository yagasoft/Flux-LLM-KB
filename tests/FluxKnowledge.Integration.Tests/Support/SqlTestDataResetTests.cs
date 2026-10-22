using FluxKnowledge.Domain.Jobs;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
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
            await setup.SaveChangesAsync();
        }
        await SqlTestData.ClearPipelineAsync(fixture);
        await SqlTestData.ClearPipelineAsync(fixture);
        await using var verification = CreateContext();
        Assert.Empty(await verification.SourceIdentities.ToArrayAsync());
        Assert.Empty(await verification.PipelineRecords.ToArrayAsync());
        Assert.Empty(await verification.Jobs.ToArrayAsync());
        Assert.Empty(await verification.OutboxMessages.ToArrayAsync());
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

    private FluxKnowledgeDbContext CreateContext() => new(
        new DbContextOptionsBuilder<FluxKnowledgeDbContext>().UseSqlServer(fixture.ConnectionString).Options);
}
