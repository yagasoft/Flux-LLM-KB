using FluxKnowledge.Application.Pipeline;
using FluxKnowledge.Application.Workers;
using FluxKnowledge.Domain.Jobs;
using FluxKnowledge.Domain.Pipeline;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Persistence;

public sealed class StageTransitionAtomicityTests(NativeSqlServerFixture fixture)
    : IClassFixture<NativeSqlServerFixture>
{
    private readonly NativeSqlServerFixture _fixture = fixture;

    [NativeSqlServerFact]
    public async Task Failure_after_artifact_write_rolls_back_the_entire_transition()
    {
        await SqlTestData.ClearPipelineAsync(_fixture);
        var now = DateTimeOffset.Parse("2026-07-27T08:00:00+00:00");
        var seeded = await SqlTestData.SeedWorkItemAsync(
            _fixture,
            now,
            PublicJobState.WorkerQueued,
            leaseExpiresAtUtc: null);
        var factory = SqlTestData.CreateFactory(_fixture);
        var request = await ClaimExtractAsync(factory, now);
        var store = new SqlStageTransitionStore(factory, new ThrowAfterArtifactWrite());

        await Assert.ThrowsAsync<InjectedTransitionException>(
            async () => await store.TransitionAsync(request, CancellationToken.None));

        await using var context = await factory.CreateDbContextAsync();
        Assert.Empty(
            await context.Artifacts
                .Where(artifact => artifact.PipelineRecordId == seeded.PipelineRecordId.Value)
                .ToListAsync());
        Assert.DoesNotContain(
            await context.Jobs
                .Where(job => job.PipelineRecordId == seeded.PipelineRecordId.Value)
                .ToListAsync(),
            job => job.PublicState == (int)PublicJobState.Completed);
        Assert.Single(
            await context.Jobs
                .Where(job => job.PipelineRecordId == seeded.PipelineRecordId.Value)
                .ToListAsync());
        Assert.Single(
            await context.OutboxMessages
                .Where(message => message.PipelineRecordId == seeded.PipelineRecordId.Value)
                .ToListAsync());
    }

    [NativeSqlServerFact]
    public async Task Duplicate_delivery_returns_the_durable_original_transition()
    {
        await SqlTestData.ClearPipelineAsync(_fixture);
        var now = DateTimeOffset.Parse("2026-07-27T08:00:00+00:00");
        var seeded = await SqlTestData.SeedWorkItemAsync(
            _fixture,
            now,
            PublicJobState.WorkerQueued,
            leaseExpiresAtUtc: null);
        var factory = SqlTestData.CreateFactory(_fixture);
        var request = await ClaimExtractAsync(factory, now);
        var store = new SqlStageTransitionStore(factory);

        var first = await store.TransitionAsync(request, CancellationToken.None);
        var second = await store.TransitionAsync(request, CancellationToken.None);

        Assert.False(first.ExistingTransition);
        Assert.True(second.ExistingTransition);
        Assert.Equal(first.ArtifactId, second.ArtifactId);
        Assert.Equal(first.NextJobId, second.NextJobId);
        Assert.Equal(first.NextDispatchMessageId, second.NextDispatchMessageId);
        await using var context = await factory.CreateDbContextAsync();
        Assert.Single(
            await context.Artifacts
                .Where(artifact => artifact.PipelineRecordId == seeded.PipelineRecordId.Value)
                .ToListAsync());
        Assert.Equal(
            2,
            await context.Jobs.CountAsync(
                job => job.PipelineRecordId == seeded.PipelineRecordId.Value));
        Assert.Equal(
            2,
            await context.OutboxMessages.CountAsync(
                message => message.PipelineRecordId == seeded.PipelineRecordId.Value));
    }

    private static async Task<StageTransitionRequest> ClaimExtractAsync(
        IDbContextFactory<FluxKnowledgeDbContext> factory,
        DateTimeOffset now)
    {
        var outbox = await new SqlOutboxStore(factory).ClaimNextDueAsync(
            "dispatcher",
            now,
            TimeSpan.FromMinutes(2),
            [PipelineOperations.ExtractUtf8],
            CancellationToken.None);
        Assert.NotNull(outbox);
        var job = await new SqlJobClaimStore(factory).ClaimForDispatchAsync(
            outbox,
            "worker",
            now,
            TimeSpan.FromMinutes(2),
            CancellationToken.None);
        Assert.NotNull(job);
        return new StageTransitionRequest(
            outbox,
            job,
            new StageArtifact(
                Guid.Parse("b9cd7de9-8d5f-4965-8d8a-4d9718d11f31"),
                PipelineStage.Extract,
                new string('a', 64),
                "text/plain; charset=utf-8",
                "hello",
                now),
            PipelineStage.Normalise,
            PipelineOperations.NormaliseText,
            "test-worker");
    }

    private sealed class ThrowAfterArtifactWrite : IStageTransitionFailureInjector
    {
        public ValueTask AfterArtifactWrittenAsync(CancellationToken cancellationToken) =>
            ValueTask.FromException(new InjectedTransitionException());
    }

    private sealed class InjectedTransitionException : Exception;
}
