using FluxKnowledge.Application.Workers;
using FluxKnowledge.Domain.Jobs;
using FluxKnowledge.Domain.Pipeline;
using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using FluxKnowledge.Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Sources;

public sealed class SourceLifecycleIntegrationTests(NativeSqlServerFixture fixture)
    : IClassFixture<NativeSqlServerFixture>, IAsyncLifetime
{
    private readonly NativeSqlServerFixture _fixture = fixture;

    public Task InitializeAsync() => SqlTestData.ClearPhase3SourceDataAsync(_fixture);

    public Task DisposeAsync() => Task.CompletedTask;

    [NativeSqlServerFact]
    public async Task Paused_root_excludes_its_pipeline_work_without_blocking_unrelated_work_and_resume_reenables_it()
    {
        var now = DateTimeOffset.Parse("2026-09-18T09:30:00+00:00");
        var source = await SeedPausedSourceWorkAsync(now);
        var unrelated = await SeedUnrelatedWorkAsync(now);
        var factory = SqlTestData.CreateFactory(_fixture);
        var outbox = new SqlOutboxStore(factory);
        var jobs = new SqlJobClaimStore(factory);

        var unrelatedDispatch = await outbox.ClaimNextDueAsync(
            "paused-root-dispatcher",
            now,
            TimeSpan.FromMinutes(1),
            [PipelineOperations.ExtractUtf8],
            CancellationToken.None);
        var unrelatedJob = await jobs.ClaimNextDueAsync(
            "paused-root-worker",
            now,
            TimeSpan.FromMinutes(1),
            CancellationToken.None);

        Assert.NotNull(unrelatedDispatch);
        Assert.Equal(unrelated.PipelineRecordId, unrelatedDispatch.PipelineRecordId.Value);
        Assert.NotNull(unrelatedJob);
        Assert.Equal(unrelated.JobId, unrelatedJob.JobId.Value);
        await AssertUnclaimedAsync(source);

        await using (var resume = CreateContext())
        {
            var root = await resume.SourceRootConfigurations.SingleAsync(value => value.Id == source.RootId);
            root.State = (int)SourceRootState.Enabled;
            await resume.SaveChangesAsync();
        }

        var sourceDispatch = await outbox.ClaimNextDueAsync(
            "resumed-root-dispatcher",
            now,
            TimeSpan.FromMinutes(1),
            [PipelineOperations.ExtractUtf8],
            CancellationToken.None);
        var sourceJob = await jobs.ClaimNextDueAsync(
            "resumed-root-worker",
            now,
            TimeSpan.FromMinutes(1),
            CancellationToken.None);

        Assert.NotNull(sourceDispatch);
        Assert.Equal(source.PipelineRecordId, sourceDispatch.PipelineRecordId.Value);
        Assert.NotNull(sourceJob);
        Assert.Equal(source.JobId, sourceJob.JobId.Value);
    }

    [NativeSqlServerFact]
    public async Task Paused_root_does_not_offer_an_unlinked_in_process_activity_until_it_is_resumed()
    {
        var now = DateTimeOffset.Parse("2026-09-18T10:00:00+00:00");
        var source = await SeedPausedUnlinkedActivityAsync(now);
        var store = new SqlRetainedTextRegistrationStore(
            SqlTestData.CreateFactory(_fixture),
            new FixedTimeProvider(now));

        Assert.Equal(0, await store.OfferUnlinkedInProcessActivitiesAsync(CancellationToken.None));
        await AssertUnlinkedAsync(source.ActivityId);

        await using (var resume = CreateContext())
        {
            var root = await resume.SourceRootConfigurations.SingleAsync(value => value.Id == source.RootId);
            root.State = (int)SourceRootState.Enabled;
            await resume.SaveChangesAsync();
        }

        Assert.Equal(1, await store.OfferUnlinkedInProcessActivitiesAsync(CancellationToken.None));
        await using var verification = CreateContext();
        Assert.NotNull((await verification.SourceActivities.SingleAsync(value => value.Id == source.ActivityId)).ResultingPipelineRecordId);
    }

    private async Task<SourceWork> SeedPausedSourceWorkAsync(DateTimeOffset now)
    {
        var rootId = Guid.NewGuid();
        var revisionId = Guid.NewGuid();
        var identityId = Guid.NewGuid();
        var recordId = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        var outboxId = Guid.NewGuid();
        await using var context = CreateContext();
        context.SourceRootConfigurations.Add(new SourceRootConfigurationEntity
        {
            Id = rootId,
            CanonicalPath = $"C:\\source-lifecycle-tests\\{rootId:N}",
            DisplayName = "Paused source",
            State = (int)SourceRootState.Paused,
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
        context.SourceRevisions.Add(new SourceRevisionEntity
        {
            Id = revisionId,
            SourceRootId = rootId,
            StableSourceIdentity = $"paused-source:{revisionId:N}",
            Revision = 1,
            ContentSha256 = new string('a', 64),
            CanonicalPath = $"C:\\source-lifecycle-tests\\{rootId:N}\\document.txt",
            Classification = "AcceptedUtf8Text",
            Extension = ".txt",
            ByteLength = 4,
            DiscoveredAtUtc = now,
            DiscoveryEvidenceJson = "{}"
        });
        AddWork(context, identityId, recordId, jobId, outboxId, revisionId, now);
        await context.SaveChangesAsync();
        return new SourceWork(rootId, recordId, jobId, outboxId);
    }

    private async Task<UnrelatedWork> SeedUnrelatedWorkAsync(DateTimeOffset now)
    {
        var identityId = Guid.NewGuid();
        var recordId = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        var outboxId = Guid.NewGuid();
        await using var context = CreateContext();
        AddWork(context, identityId, recordId, jobId, outboxId, sourceRevisionId: null, now);
        await context.SaveChangesAsync();
        return new UnrelatedWork(recordId, jobId, outboxId);
    }

    private async Task<UnlinkedActivity> SeedPausedUnlinkedActivityAsync(DateTimeOffset now)
    {
        var rootId = Guid.NewGuid();
        var revisionId = Guid.NewGuid();
        var activityId = Guid.NewGuid();
        var hash = new string('c', 64);
        await using var context = CreateContext();
        context.SourceRootConfigurations.Add(new SourceRootConfigurationEntity
        {
            Id = rootId,
            CanonicalPath = $"C:\\source-lifecycle-tests\\activity-{rootId:N}",
            DisplayName = "Paused activity source",
            State = (int)SourceRootState.Paused,
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
        context.SourceRevisions.Add(new SourceRevisionEntity
        {
            Id = revisionId,
            SourceRootId = rootId,
            StableSourceIdentity = $"paused-activity:{revisionId:N}",
            Revision = 1,
            ContentSha256 = hash,
            CanonicalPath = $"C:\\source-lifecycle-tests\\activity-{rootId:N}\\document.txt",
            Classification = "AcceptedUtf8Text",
            Extension = ".txt",
            ByteLength = 4,
            DiscoveredAtUtc = now,
            DiscoveryEvidenceJson = "{}"
        });
        context.SourceArtifacts.Add(new SourceArtifactEntity
        {
            Id = Guid.NewGuid(),
            SourceRevisionId = revisionId,
            ContentSha256 = hash,
            StoreRelativePath = $"sha256\\{hash[..2]}\\{hash}.bin",
            ByteLength = 4,
            ChecksumVerifiedAtUtc = now,
            ReferenceCount = 1
        });
        context.SourceActivities.Add(new SourceActivityEntity
        {
            Id = activityId,
            SourceRevisionId = revisionId,
            ActivityKind = (int)SourceActivityKind.TextExtraction,
            ExecutionClass = (int)ExecutionClass.InProcess,
            ProcessorVersion = "phase-3a-v1",
            InputFingerprint = hash,
            State = (int)SourceActivityState.Pending,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        });
        await context.SaveChangesAsync();
        return new UnlinkedActivity(rootId, activityId);
    }

    private static void AddWork(
        FluxKnowledgeDbContext context,
        Guid identityId,
        Guid recordId,
        Guid jobId,
        Guid outboxId,
        Guid? sourceRevisionId,
        DateTimeOffset now)
    {
        context.SourceIdentities.Add(new SourceIdentityEntity
        {
            Id = identityId,
            SourceKind = "local file",
            StableKey = $"C:\\source-lifecycle-tests\\{recordId:N}.txt",
            CreatedAtUtc = now
        });
        context.PipelineRecords.Add(new PipelineRecordEntity
        {
            Id = recordId,
            SourceIdentityId = identityId,
            SourceRevisionId = sourceRevisionId,
            Revision = 1,
            ContentHash = new string('b', 64),
            RootLineageRecordId = recordId,
            CurrentStage = (int)PipelineStage.Extract,
            RegisteredAtUtc = now
        });
        context.Jobs.Add(new JobEntity
        {
            Id = jobId,
            PipelineRecordId = recordId,
            SourceRevision = 1,
            Stage = (int)PipelineStage.Extract,
            Operation = PipelineOperations.ExtractUtf8,
            PublicState = (int)PublicJobState.WorkerQueued,
            DueAtUtc = now
        });
        context.OutboxMessages.Add(new OutboxMessageEntity
        {
            Id = outboxId,
            PipelineRecordId = recordId,
            SourceRevision = 1,
            Stage = (int)PipelineStage.Extract,
            Operation = PipelineOperations.ExtractUtf8,
            DispatchGeneration = 0,
            IdempotencyKey = $"{recordId:N}:1:extract:0",
            DueAtUtc = now,
            CreatedAtUtc = now
        });
    }

    private async Task AssertUnclaimedAsync(SourceWork source)
    {
        await using var verification = CreateContext();
        var job = await verification.Jobs.SingleAsync(value => value.Id == source.JobId);
        var dispatch = await verification.OutboxMessages.SingleAsync(value => value.Id == source.OutboxId);
        Assert.Equal((int)PublicJobState.WorkerQueued, job.PublicState);
        Assert.Null(job.LeaseOwner);
        Assert.Null(dispatch.LeaseOwner);
    }

    private async Task AssertUnlinkedAsync(Guid activityId)
    {
        await using var verification = CreateContext();
        Assert.Null((await verification.SourceActivities.SingleAsync(value => value.Id == activityId)).ResultingPipelineRecordId);
    }

    private FluxKnowledgeDbContext CreateContext() => new(
        new DbContextOptionsBuilder<FluxKnowledgeDbContext>().UseSqlServer(_fixture.ConnectionString).Options);

    private sealed record SourceWork(Guid RootId, Guid PipelineRecordId, Guid JobId, Guid OutboxId);

    private sealed record UnrelatedWork(Guid PipelineRecordId, Guid JobId, Guid OutboxId);

    private sealed record UnlinkedActivity(Guid RootId, Guid ActivityId);

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
