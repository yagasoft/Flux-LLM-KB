using FluxKnowledge.Application.Pipeline;
using FluxKnowledge.Application.Workers;
using FluxKnowledge.Domain.Jobs;
using FluxKnowledge.Domain.Pipeline;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using FluxKnowledge.Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
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
        var factory = new RetryingDbContextFactory(_fixture.ConnectionString);
        var request = await ClaimExtractAsync(factory, now);
        var store = new SqlStageTransitionStore(factory, new ThrowAfterArtifactWrite());

        await Assert.ThrowsAsync<InjectedTransitionException>(
            async () => await store.TransitionAsync(request, CancellationToken.None));

        await using var context = factory.CreateDbContext();
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
        var factory = new RetryingDbContextFactory(_fixture.ConnectionString);
        var request = await ClaimExtractAsync(factory, now);
        var store = new SqlStageTransitionStore(factory);

        var first = await store.TransitionAsync(request, CancellationToken.None);
        await using (var extra = factory.CreateDbContext())
        {
            extra.Jobs.Add(new JobEntity
            {
                Id = Guid.NewGuid(), PipelineRecordId = request.CurrentJob.PipelineRecordId.Value,
                SourceRevision = request.CurrentJob.SourceRevision, Stage = (int)request.NextStage!,
                Operation = request.NextOperation!, PublicState = (int)PublicJobState.Completed, DueAtUtc = now
            });
            await extra.SaveChangesAsync();
        }
        var second = await store.TransitionAsync(request, CancellationToken.None);

        Assert.False(first.ExistingTransition);
        Assert.True(second.ExistingTransition);
        Assert.Equal(first.ArtifactId, second.ArtifactId);
        Assert.Equal(first.NextJobId, second.NextJobId);
        Assert.Equal(first.NextDispatchMessageId, second.NextDispatchMessageId);
        await using var context = factory.CreateDbContext();
        Assert.Single(
            await context.Artifacts
                .Where(artifact => artifact.PipelineRecordId == seeded.PipelineRecordId.Value)
                .ToListAsync());
        Assert.Equal(
            3,
            await context.Jobs.CountAsync(
                job => job.PipelineRecordId == seeded.PipelineRecordId.Value));
        Assert.Equal(
            2,
            await context.OutboxMessages.CountAsync(
                message => message.PipelineRecordId == seeded.PipelineRecordId.Value));
    }

    [NativeSqlServerFact]
    public async Task Completed_delivery_cannot_resolve_a_replacement_stage_artifact()
    {
        await SqlTestData.ClearPipelineAsync(_fixture);
        var now = DateTimeOffset.UtcNow;
        await SqlTestData.SeedWorkItemAsync(_fixture, now, PublicJobState.WorkerQueued, null);
        var factory = new RetryingDbContextFactory(_fixture.ConnectionString);
        var request = await ClaimExtractAsync(factory, now);
        var store = new SqlStageTransitionStore(factory);
        await store.TransitionAsync(request, CancellationToken.None);
        var replacementId = Guid.NewGuid();
        await using (var context = factory.CreateDbContext())
        {
            await context.Artifacts.ExecuteDeleteAsync();
            context.Artifacts.Add(new ArtifactEntity
            {
                Id = replacementId, PipelineRecordId = request.CurrentJob.PipelineRecordId.Value,
                SourceRevision = request.CurrentJob.SourceRevision, Stage = (int)request.Artifact.Stage,
                ContentHash = request.Artifact.ContentHash, ContentType = request.Artifact.ContentType,
                SearchText = request.Artifact.SearchText, CreatedAtUtc = now
            });
            await context.SaveChangesAsync();
        }
        var error = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await store.TransitionAsync(request, CancellationToken.None));
        Assert.Equal("completed-delivery-artifact-replaced", error.Message);
        await using var verification = factory.CreateDbContext();
        Assert.Equal(replacementId, (await verification.Artifacts.SingleAsync()).Id);
        Assert.Equal(2, await verification.Jobs.CountAsync());
    }

    [NativeSqlServerFact]
    public async Task Completed_output_migration_backfills_exact_existing_artifact_and_replay_remains_valid()
    {
        await SqlTestData.ClearPipelineAsync(_fixture);
        var now = DateTimeOffset.UtcNow;
        await SqlTestData.SeedWorkItemAsync(_fixture, now, PublicJobState.WorkerQueued, null);
        var factory = new RetryingDbContextFactory(_fixture.ConnectionString);
        var request = await ClaimExtractAsync(factory, now);
        var store = new SqlStageTransitionStore(factory);
        var result = await store.TransitionAsync(request, CancellationToken.None);
        await using (var context = factory.CreateDbContext())
        {
            await context.GetService<IMigrator>().MigrateAsync("20260927115029_BindOutboxMessagesToJobs");
            await context.Database.MigrateAsync();
            Assert.Equal(result.ArtifactId, (await context.OutboxMessages.SingleAsync(message =>
                message.Id == request.DispatchMessage.DispatchMessageId.Value)).CompletedArtifactId);
        }
        var replay = await store.TransitionAsync(request, CancellationToken.None);
        Assert.True(replay.ExistingTransition);
        Assert.Equal(result.ArtifactId, replay.ArtifactId);
    }

    [NativeSqlServerFact]
    public async Task Production_retrying_execution_strategy_allows_stage_transition()
    {
        await SqlTestData.ClearPipelineAsync(_fixture);
        var now = DateTimeOffset.Parse("2026-07-27T08:00:00+00:00");
        var seeded = await SqlTestData.SeedWorkItemAsync(
            _fixture,
            now,
            PublicJobState.WorkerQueued,
            leaseExpiresAtUtc: null);
        var factory = new RetryingDbContextFactory(_fixture.ConnectionString);
        var request = await ClaimExtractAsync(factory, now);

        var result = await new SqlStageTransitionStore(factory)
            .TransitionAsync(request, CancellationToken.None);

        Assert.False(result.ExistingTransition);
        await using var context = factory.CreateDbContext();
        Assert.Single(
            await context.Artifacts.Where(
                    artifact => artifact.PipelineRecordId == seeded.PipelineRecordId.Value)
                .ToListAsync());
    }

    [NativeSqlServerFact]
    public async Task Production_retrying_execution_strategy_allows_stage_failure_transition()
    {
        await SqlTestData.ClearPipelineAsync(_fixture);
        var now = DateTimeOffset.Parse("2026-07-27T08:00:00+00:00");
        var seeded = await SqlTestData.SeedWorkItemAsync(
            _fixture,
            now,
            PublicJobState.WorkerQueued,
            leaseExpiresAtUtc: null);
        var factory = new RetryingDbContextFactory(_fixture.ConnectionString);
        var request = await ClaimExtractAsync(factory, now);

        await new SqlStageTransitionStore(factory).FailAsync(
            new StageFailureRequest(
                request.DispatchMessage,
                request.CurrentJob,
                "integration failure",
                "intentional retry-strategy regression coverage",
                "test-worker"),
            CancellationToken.None);

        await using var context = factory.CreateDbContext();
        var job = await context.Jobs.SingleAsync(job => job.Id == seeded.JobId.Value);
        Assert.Equal((int)PublicJobState.Failed, job.PublicState);
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

    private sealed class RetryingDbContextFactory(string connectionString)
        : IDbContextFactory<FluxKnowledgeDbContext>
    {
        private readonly DbContextOptions<FluxKnowledgeDbContext> _options =
            new DbContextOptionsBuilder<FluxKnowledgeDbContext>()
                .UseSqlServer(connectionString, sqlServer => sqlServer.EnableRetryOnFailure())
                .Options;

        public FluxKnowledgeDbContext CreateDbContext() => new(_options);
    }

    private sealed class InjectedTransitionException : Exception;
}
