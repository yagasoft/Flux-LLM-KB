using FluxKnowledge.Domain.Common;
using FluxKnowledge.Domain.Jobs;
using FluxKnowledge.Domain.Pipeline;
using FluxKnowledge.Application.Workers;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace FluxKnowledge.Integration.Tests.Support;

internal static class SqlTestData
{
    public static IDbContextFactory<FluxKnowledgeDbContext> CreateFactory(
        NativeSqlServerFixture fixture) =>
        new TestDbContextFactory(fixture.ConnectionString);

    internal static Guid AddPhysicalRevisionHistory(
        FluxKnowledgeDbContext db, SourceRevisionEntity current,
        bool suppressPrevious = true, bool differentIdentity = false)
    {
        if (db.Entry(current).State is not (EntityState.Detached or EntityState.Added) ||
            current.OriginKind != 0 || current.ParentSourceRevisionId is not null)
            throw new InvalidOperationException("Physical history must be arranged before saving the current revision.");
        current.Revision = Math.Max(2, current.Revision);
        var now = DateTimeOffset.UtcNow;
        var previousBytes = System.Text.Encoding.UTF8.GetBytes("previous physical content:" + current.ContentSha256);
        var previous = new SourceRevisionEntity
        {
            Id = Guid.NewGuid(), SourceRootId = current.SourceRootId,
            StableSourceIdentity = differentIdentity ? "different-physical-item:" + Guid.NewGuid().ToString("N") : current.StableSourceIdentity,
            Revision = current.Revision - 1, ContentSha256 = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(previousBytes)),
            CanonicalPath = current.CanonicalPath, Classification = current.Classification,
            Extension = current.Extension, OriginKind = 0, ByteLength = previousBytes.Length,
            DiscoveredAtUtc = now.AddMinutes(-1), SuppressedAtUtc = suppressPrevious ? now : null,
            RetainUntilUtc = suppressPrevious ? now.AddDays(30) : null
        };
        db.SourceRevisions.Add(previous);
        current.ParentSourceRevisionId = previous.Id;
        return previous.Id;
    }

    /// <summary>Clears only the retained OOXML/operator-action lifecycle tables between class-fixture tests.</summary>
    public static async Task ClearOoxmlOperatorActionDataAsync(NativeSqlServerFixture fixture)
    {
        await using var context = new FluxKnowledgeDbContext(
            new DbContextOptionsBuilder<FluxKnowledgeDbContext>()
                .UseSqlServer(fixture.ConnectionString)
                .Options);
        await context.AuditEvents.ExecuteDeleteAsync();
        await context.OperatorActionOperationLedger.ExecuteDeleteAsync();
        await context.SourceProcessorActionIgnoreHeads.ExecuteDeleteAsync();
        await context.OperatorActionActionLedger.ExecuteDeleteAsync();
        await context.SourceProcessorForceRequests.ExecuteDeleteAsync();
        await context.SourceProcessorAttempts.ExecuteDeleteAsync();
        await context.SourceProcessorBranchMembers.ExecuteDeleteAsync();
        await context.SourceProcessorBranches.ExecuteDeleteAsync();
    }

    public static Task ClearPipelineAsync(NativeSqlServerFixture fixture) =>
        fixture.MeasureAsync("reset-pipeline", () => ClearPipelineCoreAsync(fixture));

    private static async Task ClearPipelineCoreAsync(NativeSqlServerFixture fixture)
    {
        await using var context = new FluxKnowledgeDbContext(
            new DbContextOptionsBuilder<FluxKnowledgeDbContext>()
                .UseSqlServer(fixture.ConnectionString)
                .Options);
        await context.AuditEvents.ExecuteDeleteAsync();
        await context.SourceDeletionCleanupItems.ExecuteDeleteAsync();
        await context.SourceDeletionOperations.ExecuteDeleteAsync();
        await context.DocumentPublications.ExecuteDeleteAsync();
        await context.CorpusQueryLeases.ExecuteDeleteAsync();
        await context.OperatorActionOperationLedger.ExecuteDeleteAsync();
        await context.SourceProcessorActionIgnoreHeads.ExecuteDeleteAsync();
        await context.OperatorActionActionLedger.ExecuteDeleteAsync();
        await context.SourceProcessorForceRequests.ExecuteDeleteAsync();
        await context.SourceProcessorAttempts.ExecuteDeleteAsync();
        await context.SourceProcessorBranchMembers.ExecuteDeleteAsync();
        await context.SourceProcessorBranches.ExecuteDeleteAsync();
        await context.SourceActivityRelations.ExecuteDeleteAsync();
        await context.SourceActivities.ExecuteDeleteAsync();
        await context.GpuSchedulerOperationReceipts.ExecuteDeleteAsync();
        await context.NativeWorkerLifecycleEvidence.ExecuteDeleteAsync();
        await context.NativeWorkerInstances.ExecuteDeleteAsync();
        await context.GpuExecutorEvidence.ExecuteDeleteAsync();
        await context.GpuExecutorResultReceipts.ExecuteDeleteAsync();
        await context.GpuExecutorDispatches.ExecuteDeleteAsync();
        await context.DocumentOcrRequests.ExecuteDeleteAsync();
        await context.EmbeddingGpuRequests.ExecuteDeleteAsync();
        await context.GpuMiniTasks.ExecuteDeleteAsync();
        await context.GpuCapacitySlots
            .Where(slot => slot.ActiveBatchId != null)
            .ExecuteUpdateAsync(setters => setters.SetProperty(slot => slot.ActiveBatchId, (Guid?)null));
        await context.GpuBatches.ExecuteDeleteAsync();
        await context.GpuCapacitySlots.ExecuteDeleteAsync();
        var scheduler = await context.GpuSchedulerStates.SingleAsync(candidate => candidate.Id == 1);
        scheduler.WakeGeneration = 0;
        scheduler.SearchBatchesWhileOcrWaiting = 0;
        scheduler.PendingWakeReasons = 0;
        scheduler.NextDeferredAtUtc = null;
        scheduler.InFlightWakeOperationId = null;
        scheduler.InFlightWakeGeneration = null;
        scheduler.InFlightWakeReasons = 0;
        scheduler.InFlightNextDeferredAtUtc = null;
        scheduler.InFlightEffectiveAdmissionReasons = null;
        scheduler.UpdatedAtUtc = DateTimeOffset.UnixEpoch;
        var state = await context.IndexState.SingleAsync(candidate => candidate.Id == 1);
        state.ActiveIndexGenerationId = null;
        state.CorpusRebuildOperationId = null;
        state.RequiredIndexFormat = 1;
        state.ProjectionChangeFloorVersion = state.CorpusVersion;
        await context.SaveChangesAsync();
        await context.CurrentIndexMembers.ExecuteDeleteAsync();
        await context.IndexRecoveryWriters.ExecuteDeleteAsync();
        await context.IndexRecoveryPins.ExecuteDeleteAsync();
        await context.IndexRecoveryAudit.ExecuteUpdateAsync(setters => setters.SetProperty(value => value.IsValid, false)
            .SetProperty(value => value.FailurePath, (string?)null));
        await context.IndexSegmentOwners.ExecuteDeleteAsync();
        await context.IndexGenerationSegments.ExecuteDeleteAsync();
        await context.IndexSegmentVectors.ExecuteDeleteAsync();
        await context.IndexSegments.ExecuteDeleteAsync();
        await context.IndexCandidateMembers.ExecuteDeleteAsync();
        await context.IndexPublicationCandidates.ExecuteDeleteAsync();
        await context.IndexProjectionChanges.ExecuteDeleteAsync();
        await context.CorpusRebuildWorkItems.ExecuteDeleteAsync();
        await context.CorpusRebuildSupersededJobs.ExecuteDeleteAsync();
        await context.CorpusRebuildOperations.ExecuteUpdateAsync(setters => setters.SetProperty(value => value.SupersedesOperationId, (Guid?)null));
        await context.CorpusRebuildOperations.ExecuteDeleteAsync();
        await context.IndexGenerationVectors.ExecuteDeleteAsync();
        await context.Vectors.ExecuteDeleteAsync();
        await context.IndexGenerations.ExecuteDeleteAsync();
        await context.Artifacts.ExecuteDeleteAsync();
        await context.OutboxMessages.ExecuteDeleteAsync();
        await context.JobAttempts.ExecuteDeleteAsync();
        await context.Jobs.ExecuteDeleteAsync();
        await context.PipelineRecords.ExecuteDeleteAsync();
        await context.SourceIdentities.ExecuteDeleteAsync();
        await context.SaveChangesAsync();
    }

    public static async Task ClearPhase3SourceDataAsync(NativeSqlServerFixture fixture)
    {
        await using (var context = new FluxKnowledgeDbContext(
                         new DbContextOptionsBuilder<FluxKnowledgeDbContext>()
                             .UseSqlServer(fixture.ConnectionString)
                             .Options))
        {
            await context.AuditEvents.ExecuteDeleteAsync();
            await context.SourceRootWatchStates.ExecuteDeleteAsync();
            await context.SourceScanOutbox.ExecuteDeleteAsync();
            await context.SourceScanJobs.ExecuteDeleteAsync();
            await context.SourceScanRequests.ExecuteDeleteAsync();
        }

        await ClearPipelineAsync(fixture);

        await using var sourceContext = new FluxKnowledgeDbContext(
            new DbContextOptionsBuilder<FluxKnowledgeDbContext>()
                .UseSqlServer(fixture.ConnectionString)
                .Options);
        await sourceContext.SourceArtifacts.ExecuteDeleteAsync();
        while (await sourceContext.SourceRevisions
                   .Where(candidate => !sourceContext.SourceRevisions
                       .Any(child => child.ParentSourceRevisionId == candidate.Id))
                   .ExecuteDeleteAsync() > 0)
        {
        }

        await sourceContext.SourceRootConfigurations.ExecuteDeleteAsync();
    }

    public static async Task<SeededWorkItem> SeedWorkItemAsync(
        NativeSqlServerFixture fixture,
        DateTimeOffset now,
        PublicJobState state,
        DateTimeOffset? leaseExpiresAtUtc,
        long leaseGeneration = 0,
        int attemptCount = 0,
        PipelineStage stage = PipelineStage.Extract,
        string operation = PipelineOperations.ExtractUtf8,
        string? stablePath = null,
        string? contentHash = null)
    {
        var sourceId = Guid.NewGuid();
        var recordId = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        var dispatchId = Guid.NewGuid();
        var hash = contentHash ?? new string('a', 64);
        await using var context = new FluxKnowledgeDbContext(
            new DbContextOptionsBuilder<FluxKnowledgeDbContext>()
                .UseSqlServer(fixture.ConnectionString)
                .Options);
        context.SourceIdentities.Add(
            new SourceIdentityEntity
            {
                Id = sourceId,
                SourceKind = "local file",
                StableKey = stablePath ?? $"C:\\ingress\\{recordId:N}.txt",
                CreatedAtUtc = now
            });
        context.PipelineRecords.Add(
            new PipelineRecordEntity
            {
                Id = recordId,
                SourceIdentityId = sourceId,
                Revision = 1,
                ContentHash = hash,
                RootLineageRecordId = recordId,
                CurrentStage = (int)stage,
                RegisteredAtUtc = now
            });
        context.Jobs.Add(
            new JobEntity
            {
                Id = jobId,
                PipelineRecordId = recordId,
                SourceRevision = 1,
                Stage = (int)stage,
                Operation = operation,
                PublicState = (int)state,
                DueAtUtc = now,
                AttemptCount = attemptCount,
                LeaseOwner = state == PublicJobState.WorkerProcessing ? "expired-worker" : null,
                LeaseExpiresAtUtc = leaseExpiresAtUtc,
                LeaseGeneration = leaseGeneration
            });
        context.OutboxMessages.Add(
            new OutboxMessageEntity
            {
                Id = dispatchId,
                JobId = jobId,
                PipelineRecordId = recordId,
                SourceRevision = 1,
                Stage = (int)stage,
                Operation = operation,
                DispatchGeneration = 0,
                IdempotencyKey = $"{recordId:N}:1:{stage}:0",
                DueAtUtc = now,
                CreatedAtUtc = now
            });
        await context.SaveChangesAsync();
        return new SeededWorkItem(
            new PipelineRecordId(recordId),
            new JobId(jobId),
            new DispatchMessageId(dispatchId));
    }

    private sealed class TestDbContextFactory(string connectionString)
        : IDbContextFactory<FluxKnowledgeDbContext>
    {
        private readonly DbContextOptions<FluxKnowledgeDbContext> _options =
            new DbContextOptionsBuilder<FluxKnowledgeDbContext>()
                .UseSqlServer(connectionString)
                .Options;

        public FluxKnowledgeDbContext CreateDbContext() => new(_options);
    }
}

internal sealed record SeededWorkItem(
    PipelineRecordId PipelineRecordId,
    JobId JobId,
    DispatchMessageId DispatchMessageId);
