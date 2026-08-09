using FluxKnowledge.Application.Pipeline;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Workers;
using FluxKnowledge.Domain.Common;
using FluxKnowledge.Domain.Jobs;
using FluxKnowledge.Domain.Pipeline;
using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using FluxKnowledge.Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Sources;

public sealed class SourceActivityLifecycleIntegrationTests(NativeSqlServerFixture fixture) : IClassFixture<NativeSqlServerFixture>
{
    private readonly NativeSqlServerFixture _fixture = fixture;

    [NativeSqlServerFact]
    public async Task Exact_linked_source_activities_follow_claim_terminal_failure_and_final_publish_without_mutating_another_receipt()
    {
        await ClearSourceLifecycleDataAsync();
        var now = DateTimeOffset.Parse("2026-08-09T10:00:00+00:00");
        var primary = await SqlTestData.SeedWorkItemAsync(
            _fixture,
            now,
            PublicJobState.WorkerQueued,
            leaseExpiresAtUtc: null,
            stage: PipelineStage.Extract,
            operation: PipelineOperations.ExtractUtf8);
        var unrelated = await SqlTestData.SeedWorkItemAsync(
            _fixture,
            now,
            PublicJobState.WorkerQueued,
            leaseExpiresAtUtc: null,
            stage: PipelineStage.Normalise,
            operation: PipelineOperations.NormaliseText);
        var published = await SqlTestData.SeedWorkItemAsync(
            _fixture,
            now,
            PublicJobState.WorkerQueued,
            leaseExpiresAtUtc: null,
            stage: PipelineStage.Publish,
            operation: PipelineOperations.Publish);
        var activityIds = await SeedLinkedActivitiesAsync(primary.PipelineRecordId, unrelated.PipelineRecordId, published.PipelineRecordId, now);
        var factory = new ContextFactory(_fixture.ConnectionString);
        var outboxStore = new SqlOutboxStore(factory);
        var jobStore = new SqlJobClaimStore(factory);
        var transitions = new SqlStageTransitionStore(factory, null, new FixedTimeProvider(now.AddMinutes(6)));

        var extractDispatch = await outboxStore.ClaimNextDueAsync(
            "source-lifecycle-dispatcher",
            now.AddMinutes(1),
            TimeSpan.FromMinutes(2),
            [PipelineOperations.ExtractUtf8],
            CancellationToken.None);
        Assert.NotNull(extractDispatch);
        Assert.Equal(primary.PipelineRecordId, extractDispatch.PipelineRecordId);
        var extractJob = await jobStore.ClaimForDispatchAsync(
            extractDispatch,
            "source-lifecycle-worker",
            now.AddMinutes(1),
            TimeSpan.FromMinutes(2),
            CancellationToken.None);
        Assert.NotNull(extractJob);

        await using (var afterClaim = CreateContext())
        {
            var claimed = await afterClaim.SourceActivities.SingleAsync(value => value.Id == activityIds.Primary);
            var untouched = await afterClaim.SourceActivities.SingleAsync(value => value.Id == activityIds.Unrelated);
            Assert.Equal((int)SourceActivityState.Running, claimed.State);
            Assert.Equal(1, claimed.AttemptCount);
            Assert.NotNull(claimed.LastAttemptAtUtc);
            Assert.Contains("claimLeaseGeneration", claimed.AttemptEvidenceJson, StringComparison.Ordinal);
            Assert.Equal((int)SourceActivityState.Pending, untouched.State);
            Assert.Equal(0, untouched.AttemptCount);
        }

        var retryDispatch = await outboxStore.ClaimNextDueAsync(
            "source-lifecycle-retry-dispatcher",
            now.AddMinutes(4),
            TimeSpan.FromMinutes(2),
            [PipelineOperations.ExtractUtf8],
            CancellationToken.None);
        Assert.NotNull(retryDispatch);
        var retryJob = await jobStore.ClaimForDispatchAsync(
            retryDispatch,
            "source-lifecycle-retry-worker",
            now.AddMinutes(4),
            TimeSpan.FromMinutes(2),
            CancellationToken.None);
        Assert.NotNull(retryJob);
        await using (var afterReclaim = CreateContext())
        {
            var reclaimed = await afterReclaim.SourceActivities.SingleAsync(value => value.Id == activityIds.Primary);
            Assert.Equal((int)SourceActivityState.Running, reclaimed.State);
            Assert.Equal(2, reclaimed.AttemptCount);
            Assert.Contains("claimLeaseGeneration\":2", reclaimed.AttemptEvidenceJson, StringComparison.Ordinal);
        }

        await transitions.FailAsync(
            new StageFailureRequest(
                retryDispatch,
                retryJob,
                "terminal\r\nfailure",
                "intentional terminal lifecycle proof",
                nameof(SourceActivityLifecycleIntegrationTests)),
            CancellationToken.None);

        await using (var afterFailure = CreateContext())
        {
            var failed = await afterFailure.SourceActivities.SingleAsync(value => value.Id == activityIds.Primary);
            var untouched = await afterFailure.SourceActivities.SingleAsync(value => value.Id == activityIds.Unrelated);
            Assert.Equal((int)SourceActivityState.FailedTerminal, failed.State);
            Assert.Equal("terminalfailure", failed.Reason);
            Assert.Contains("terminalStageFailure", failed.AttemptEvidenceJson, StringComparison.Ordinal);
            Assert.Equal((int)SourceActivityState.Pending, untouched.State);
            Assert.Equal(0, untouched.AttemptCount);
        }

        var publishDispatch = await outboxStore.ClaimNextDueAsync(
            "publish-lifecycle-dispatcher",
            now.AddMinutes(6),
            TimeSpan.FromMinutes(2),
            [PipelineOperations.Publish],
            CancellationToken.None);
        Assert.NotNull(publishDispatch);
        Assert.Equal(published.PipelineRecordId, publishDispatch.PipelineRecordId);
        var publishJob = await jobStore.ClaimForDispatchAsync(
            publishDispatch,
            "publish-lifecycle-worker",
            now.AddMinutes(6),
            TimeSpan.FromMinutes(2),
            CancellationToken.None);
        Assert.NotNull(publishJob);
        await transitions.TransitionAsync(
            new StageTransitionRequest(
                publishDispatch,
                publishJob,
                new StageArtifact(
                    Guid.NewGuid(),
                    PipelineStage.Publish,
                    new string('d', 64),
                    "application/vnd.fluxknowledge.usearch-generation",
                    "published-generation",
                    now.AddMinutes(6)),
                NextStage: null,
                NextOperation: null,
                nameof(SourceActivityLifecycleIntegrationTests)),
            CancellationToken.None);

        await using var verification = CreateContext();
        var completed = await verification.SourceActivities.SingleAsync(value => value.Id == activityIds.Published);
        var failedAgain = await verification.SourceActivities.SingleAsync(value => value.Id == activityIds.Primary);
        var unrelatedAgain = await verification.SourceActivities.SingleAsync(value => value.Id == activityIds.Unrelated);
        Assert.Equal((int)SourceActivityState.Completed, completed.State);
        Assert.Null(completed.Reason);
        Assert.Equal((int)SourceActivityState.FailedTerminal, failedAgain.State);
        Assert.Equal((int)SourceActivityState.Pending, unrelatedAgain.State);
    }

    private async Task<(Guid Primary, Guid Unrelated, Guid Published)> SeedLinkedActivitiesAsync(
        PipelineRecordId primaryRecord,
        PipelineRecordId unrelatedRecord,
        PipelineRecordId publishedRecord,
        DateTimeOffset now)
    {
        var rootId = Guid.NewGuid();
        var primaryRevisionId = Guid.NewGuid();
        var unrelatedRevisionId = Guid.NewGuid();
        var publishedRevisionId = Guid.NewGuid();
        var primaryActivityId = Guid.NewGuid();
        var unrelatedActivityId = Guid.NewGuid();
        var publishedActivityId = Guid.NewGuid();
        await using var context = CreateContext();
        context.SourceRootConfigurations.Add(new SourceRootConfigurationEntity
        {
            Id = rootId,
            CanonicalPath = $"C:\\source-activity-lifecycle\\{rootId:N}",
            DisplayName = "Source activity lifecycle",
            State = (int)SourceRootState.Enabled,
            Recursive = true,
            IncludePatternsJson = "[]",
            ExcludePatternsJson = "[]",
            AllowedClassificationsJson = "[\"text/plain\"]",
            MaximumFileBytes = 16L * 1024 * 1024,
            ReconciliationCadenceSeconds = 900,
            ConfigurationRevision = 1,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        });
        context.SourceRevisions.AddRange(
            Revision(primaryRevisionId, rootId, "primary.txt", now),
            Revision(unrelatedRevisionId, rootId, "unrelated.txt", now),
            Revision(publishedRevisionId, rootId, "published.txt", now));
        context.SourceActivities.AddRange(
            Activity(primaryActivityId, primaryRevisionId, SourceActivityState.Pending, primaryRecord),
            Activity(unrelatedActivityId, unrelatedRevisionId, SourceActivityState.Pending, unrelatedRecord),
            Activity(publishedActivityId, publishedRevisionId, SourceActivityState.Running, publishedRecord));
        await context.SaveChangesAsync();
        return (primaryActivityId, unrelatedActivityId, publishedActivityId);
    }

    private async Task ClearSourceLifecycleDataAsync()
    {
        await using (var context = CreateContext())
        {
            await context.SourceActivities.ExecuteDeleteAsync();
            await context.SourceArtifacts.ExecuteDeleteAsync();
            await context.SourceRevisions.ExecuteDeleteAsync();
            await context.SourceScanOutbox.ExecuteDeleteAsync();
            await context.SourceScanJobs.ExecuteDeleteAsync();
            await context.SourceScanRequests.ExecuteDeleteAsync();
            await context.SourceRootConfigurations.ExecuteDeleteAsync();
        }

        await SqlTestData.ClearPipelineAsync(_fixture);
    }

    private static SourceRevisionEntity Revision(Guid id, Guid rootId, string name, DateTimeOffset now) => new()
    {
        Id = id,
        SourceRootId = rootId,
        StableSourceIdentity = $"source-activity:{id:N}",
        Revision = 1,
        ContentSha256 = new string('c', 64),
        CanonicalPath = $"C:\\source-activity-lifecycle\\{rootId:N}\\{name}",
        Classification = "AcceptedUtf8Text",
        Extension = ".txt",
        ByteLength = 4,
        DiscoveredAtUtc = now,
        DiscoveryEvidenceJson = "{}"
    };

    private static SourceActivityEntity Activity(
        Guid id,
        Guid sourceRevisionId,
        SourceActivityState state,
        PipelineRecordId recordId) => new()
    {
        Id = id,
        SourceRevisionId = sourceRevisionId,
        ActivityKind = (int)SourceActivityKind.TextExtraction,
        ExecutionClass = (int)ExecutionClass.InProcess,
        ProcessorVersion = "phase-3a-v1",
        InputFingerprint = new string('c', 64),
        State = (int)state,
        ResultingPipelineRecordId = recordId.Value,
        ResultingPipelineRecordRevision = 1,
        CreatedAtUtc = DateTimeOffset.UnixEpoch,
        UpdatedAtUtc = DateTimeOffset.UnixEpoch
    };

    private FluxKnowledgeDbContext CreateContext() => new(
        new DbContextOptionsBuilder<FluxKnowledgeDbContext>().UseSqlServer(_fixture.ConnectionString).Options);

    private sealed class ContextFactory(string connectionString) : IDbContextFactory<FluxKnowledgeDbContext>
    {
        private readonly DbContextOptions<FluxKnowledgeDbContext> _options =
            new DbContextOptionsBuilder<FluxKnowledgeDbContext>()
                .UseSqlServer(connectionString, sql => sql.EnableRetryOnFailure())
                .Options;

        public FluxKnowledgeDbContext CreateDbContext() => new(_options);

        public Task<FluxKnowledgeDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
