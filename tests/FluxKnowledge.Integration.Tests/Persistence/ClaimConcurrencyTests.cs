using FluxKnowledge.Application.Workers;
using FluxKnowledge.Domain.Jobs;
using FluxKnowledge.Domain.Pipeline;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Integration.Tests.Support;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Persistence;

public sealed class ClaimConcurrencyTests(NativeSqlServerFixture fixture)
    : IClassFixture<NativeSqlServerFixture>
{
    private readonly NativeSqlServerFixture _fixture = fixture;

    [NativeSqlServerFact]
    public async Task Two_workers_cannot_claim_the_same_due_job()
    {
        await SqlTestData.ClearPipelineAsync(_fixture);
        var now = DateTimeOffset.Parse("2026-07-27T08:00:00+00:00");
        var seeded = await SqlTestData.SeedWorkItemAsync(
            _fixture,
            now,
            PublicJobState.WorkerQueued,
            leaseExpiresAtUtc: null);
        var store = new SqlJobClaimStore(SqlTestData.CreateFactory(_fixture));

        var claims = await Task.WhenAll(
            store.ClaimNextDueAsync(
                "worker-a", now, TimeSpan.FromMinutes(1), CancellationToken.None).AsTask(),
            store.ClaimNextDueAsync(
                "worker-b", now, TimeSpan.FromMinutes(1), CancellationToken.None).AsTask());

        Assert.Single(claims, static claim => claim is not null);
        Assert.Equal(seeded.JobId, claims.Single(static claim => claim is not null)!.JobId);
    }

    [NativeSqlServerFact]
    public async Task Two_dispatchers_cannot_claim_the_same_due_outbox_message()
    {
        await SqlTestData.ClearPipelineAsync(_fixture);
        var now = DateTimeOffset.Parse("2026-07-27T08:00:00+00:00");
        var seeded = await SqlTestData.SeedWorkItemAsync(
            _fixture,
            now,
            PublicJobState.WorkerQueued,
            leaseExpiresAtUtc: null);
        var store = new SqlOutboxStore(SqlTestData.CreateFactory(_fixture));
        string[] registeredOperations = [PipelineOperations.ExtractUtf8];

        var claims = await Task.WhenAll(
            store.ClaimNextDueAsync(
                    "dispatcher-a",
                    now,
                    TimeSpan.FromMinutes(1),
                    registeredOperations,
                    CancellationToken.None)
                .AsTask(),
            store.ClaimNextDueAsync(
                    "dispatcher-b",
                    now,
                    TimeSpan.FromMinutes(1),
                    registeredOperations,
                    CancellationToken.None)
                .AsTask());

        Assert.Single(claims, static claim => claim is not null);
        Assert.Equal(
            seeded.DispatchMessageId,
            claims.Single(static claim => claim is not null)!.DispatchMessageId);
    }

    [NativeSqlServerFact]
    public async Task Expired_processing_job_is_reclaimed_under_a_new_lease_generation()
    {
        await SqlTestData.ClearPipelineAsync(_fixture);
        var now = DateTimeOffset.Parse("2026-07-27T08:00:00+00:00");
        var seeded = await SqlTestData.SeedWorkItemAsync(
            _fixture,
            now,
            PublicJobState.WorkerProcessing,
            now.AddMinutes(-1),
            leaseGeneration: 3,
            attemptCount: 3);
        var store = new SqlJobClaimStore(SqlTestData.CreateFactory(_fixture));

        var claim = await store.ClaimNextDueAsync(
            "replacement-worker",
            now,
            TimeSpan.FromMinutes(2),
            CancellationToken.None);

        Assert.NotNull(claim);
        Assert.Equal(seeded.JobId, claim.JobId);
        Assert.Equal(PublicJobState.WorkerProcessing, claim.PublicState);
        Assert.Equal(4, claim.LeaseGeneration);
        Assert.Equal("replacement-worker", claim.LeaseOwner);
        Assert.Equal(now.AddMinutes(2), claim.LeaseExpiresAtUtc);
    }

    [NativeSqlServerFact]
    public async Task Outbox_claim_ignores_operations_without_a_registered_handler()
    {
        await SqlTestData.ClearPipelineAsync(_fixture);
        var now = DateTimeOffset.Parse("2026-07-27T08:00:00+00:00");
        await SqlTestData.SeedWorkItemAsync(
            _fixture,
            now,
            PublicJobState.WorkerQueued,
            leaseExpiresAtUtc: null,
            stage: PipelineStage.CanonicalIndex,
            operation: PipelineOperations.CanonicalIndex);
        var store = new SqlOutboxStore(SqlTestData.CreateFactory(_fixture));

        var claim = await store.ClaimNextDueAsync(
            "dispatcher",
            now,
            TimeSpan.FromMinutes(1),
            [PipelineOperations.ExtractUtf8, PipelineOperations.NormaliseText],
            CancellationToken.None);

        Assert.Null(claim);
    }
}
