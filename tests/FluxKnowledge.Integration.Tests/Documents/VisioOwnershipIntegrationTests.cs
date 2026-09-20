using FluxKnowledge.Application.Pipeline;
using FluxKnowledge.Application.Workers;
using FluxKnowledge.Domain.Jobs;
using FluxKnowledge.Domain.Pipeline;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Documents;

public sealed class VisioOwnershipIntegrationTests(NativeSqlServerFixture fixture)
    : IClassFixture<NativeSqlServerFixture>, IAsyncLifetime
{
    private const string InteractiveOperation = "extract document visio interactive";
    private readonly DateTimeOffset _now = DateTimeOffset.UtcNow;
    public Task InitializeAsync() => SqlTestData.ClearPhase3SourceDataAsync(fixture);
    public Task DisposeAsync() => Task.CompletedTask;

    [NativeSqlServerTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Generic_worker_claims_never_take_interactive_Visio_even_after_lease_expiry(bool expired)
    {
        await SqlTestData.SeedWorkItemAsync(fixture, _now.AddMinutes(-2),
            expired ? PublicJobState.WorkerProcessing : PublicJobState.WorkerQueued,
            expired ? _now.AddMinutes(-1) : null, operation: InteractiveOperation);
        var store = new SqlJobClaimStore(SqlTestData.CreateFactory(fixture));
        Assert.Null(await store.ClaimNextDueAsync("iis", _now, TimeSpan.FromMinutes(2), default));
        Assert.Null(await store.ClaimWorkerAsync("iis", _now, _now.AddMinutes(2), default));
    }

    [NativeSqlServerFact]
    public async Task Visio_job_claim_refuses_a_dispatch_whose_durable_owner_has_changed()
    {
        await SqlTestData.SeedWorkItemAsync(fixture, _now, PublicJobState.WorkerQueued, null,
            operation: InteractiveOperation);
        var factory = SqlTestData.CreateFactory(fixture);
        var dispatch = await new SqlOutboxStore(factory).ClaimNextDueAsync(
            "desktop", _now, TimeSpan.FromMinutes(12), [InteractiveOperation], default);
        Assert.NotNull(dispatch);
        await using (var context = await factory.CreateDbContextAsync())
        {
            await context.OutboxMessages.ExecuteUpdateAsync(set => set
                .SetProperty(row => row.LeaseOwner, "replacement")
                .SetProperty(row => row.LeaseGeneration, row => row.LeaseGeneration + 1));
        }
        Assert.Null(await new SqlJobClaimStore(factory).ClaimForDispatchAsync(
            dispatch, "desktop", _now, TimeSpan.FromMinutes(12), default));
    }

    [NativeSqlServerFact]
    public async Task Expired_Visio_completion_writes_no_extract_artifact_or_successor()
    {
        await SqlTestData.SeedWorkItemAsync(fixture, _now, PublicJobState.WorkerQueued, null,
            operation: InteractiveOperation);
        var factory = SqlTestData.CreateFactory(fixture);
        var dispatch = (await new SqlOutboxStore(factory).ClaimNextDueAsync(
            "desktop", _now, TimeSpan.FromMinutes(1), [InteractiveOperation], default))!;
        var job = (await new SqlJobClaimStore(factory).ClaimForDispatchAsync(
            dispatch, "desktop", _now, TimeSpan.FromMinutes(1), default))!;
        var store = new SqlStageTransitionStore(factory, null, new TestClock(_now.AddMinutes(2)));
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await store.TransitionAsync(new StageTransitionRequest(dispatch, job,
                new StageArtifact(Guid.NewGuid(), PipelineStage.Extract, new string('a', 64),
                    "text/plain; charset=utf-8", "must never publish", _now),
                PipelineStage.Normalise, PipelineOperations.NormaliseText, "visio-test"), default));
        Assert.Equal("visio-lease-expired", exception.Message);
        await using var verification = await factory.CreateDbContextAsync();
        Assert.Empty(await verification.Artifacts.ToListAsync());
        Assert.Single(await verification.Jobs.ToListAsync());
    }

    private sealed class TestClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
