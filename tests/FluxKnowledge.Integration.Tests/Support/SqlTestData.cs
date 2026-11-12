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

    public static async Task WaitForFullTextAsync(IDbContextFactory<FluxKnowledgeDbContext> factory)
    {
        await using var context = await factory.CreateDbContextAsync();
        await WaitForFullTextAsync(context);
    }

    public static async Task WaitForFullTextAsync(FluxKnowledgeDbContext context)
    {
        // Keyword assertions require this fixture's AUTO population to settle.
        // Observe it without refreshing/reconfiguring the index or consuming a
        // measured retrieval request's deadline.
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        const string sql = """
            SELECT CASE WHEN COUNT(*) = 1 AND SUM(CASE WHEN is_enabled = 1
                AND change_tracking_state_desc = N'AUTO' AND has_crawl_completed = 1
                AND CONVERT(int, OBJECTPROPERTYEX(object_id, 'TableFulltextPopulateStatus')) = 0
                AND CONVERT(int, OBJECTPROPERTYEX(object_id, 'TableFulltextPendingChanges')) = 0
                THEN 1 ELSE 0 END) = 1 THEN 1 ELSE 0 END AS [Value]
            FROM sys.fulltext_indexes WHERE object_id = OBJECT_ID(N'TextChunks')
            """;
        try
        {
            while (await context.Database.SqlQueryRaw<int>(sql).SingleAsync(deadline.Token) != 1)
                await Task.Delay(100, deadline.Token);
        }
        catch (OperationCanceledException error) when (deadline.IsCancellationRequested)
        { throw new TimeoutException("Owned keyword fixture did not reach enabled/AUTO/complete/idle/zero-pending Full-Text state.", error); }
    }

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
        await ExecuteResetBatchAsync(context, $"""
            {Delete(context.AuditEvents)}
            {Delete(context.SourceDeletionCleanupItems)}
            {Delete(context.SourceDeletionOperations)}
            {Delete(context.DocumentPublications)}
            {Delete(context.CorpusQueryLeases)}
            {Delete(context.OperatorActionOperationLedger)}
            {Delete(context.SourceProcessorActionIgnoreHeads)}
            {Delete(context.OperatorActionActionLedger)}
            {Delete(context.SourceProcessorForceRequests)}
            {Delete(context.SourceProcessorAttempts)}
            {Delete(context.SourceProcessorBranchMembers)}
            {Delete(context.SourceProcessorBranches)}
            {Delete(context.SourceActivityRelations)}
            {Delete(context.SourceActivities)}
            {Delete(context.GpuSchedulerOperationReceipts)}
            {Delete(context.NativeWorkerLifecycleEvidence)}
            {Delete(context.NativeWorkerInstances)}
            {Delete(context.GpuExecutorEvidence)}
            {Delete(context.GpuExecutorResultReceipts)}
            {Delete(context.GpuExecutorDispatches)}
            {Delete(context.DocumentOcrRequests)}
            {Delete(context.EmbeddingGpuRequests)}
            {Delete(context.GpuMiniTasks)}
            UPDATE {Table(context.GpuCapacitySlots)} SET [ActiveBatchId]=NULL WHERE [ActiveBatchId] IS NOT NULL;
            {Delete(context.GpuBatches)}
            {Delete(context.GpuCapacitySlots)}
            """);
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
        await ExecuteResetBatchAsync(context, $"""
            {Delete(context.CurrentIndexMembers)}
            {Delete(context.IndexRecoveryWriters)}
            {Delete(context.IndexRecoveryPins)}
            UPDATE {Table(context.IndexRecoveryAudit)} SET [IsValid]=0, [FailurePath]=NULL;
            {Delete(context.IndexSegmentOwners)}
            {Delete(context.IndexGenerationSegments)}
            {Delete(context.IndexSegmentVectors)}
            {Delete(context.IndexSegments)}
            {Delete(context.IndexCandidateMembers)}
            {Delete(context.IndexPublicationCandidates)}
            {Delete(context.IndexProjectionChanges)}
            {Delete(context.CorpusRebuildWorkItems)}
            {Delete(context.CorpusRebuildSupersededJobs)}
            UPDATE {Table(context.CorpusRebuildOperations)} SET [SupersedesOperationId]=NULL;
            {Delete(context.CorpusRebuildOperations)}
            {Delete(context.IndexGenerationVectors)}
            {Delete(context.Vectors)}
            {Delete(context.IndexGenerations)}
            {Delete(context.Artifacts)}
            {Delete(context.OutboxMessages)}
            {Delete(context.JobAttempts)}
            {Delete(context.Jobs)}
            {Delete(context.PipelineRecords)}
            {Delete(context.SourceIdentities)}
            """);
    }

    private static string Table<TEntity>(DbSet<TEntity> rows) where TEntity : class
    {
        var entity = rows.EntityType;
        static string Quote(string name) => "[" + name.Replace("]", "]]", StringComparison.Ordinal) + "]";
        return Quote(entity.GetSchema() ?? "dbo") + "." + Quote(entity.GetTableName()!);
    }

    private static string Delete<TEntity>(DbSet<TEntity> rows) where TEntity : class => $"DELETE FROM {Table(rows)};";

    private static Task ExecuteResetBatchAsync(FluxKnowledgeDbContext context, string statements)
    {
        // Keep the existing autocommit boundaries and EF singleton SaveChanges.
        // Unlike an unguarded batch, the first failed statement must stop all later work.
        var sql = $"SET NOCOUNT ON; BEGIN TRY {statements} END TRY BEGIN CATCH THROW; END CATCH;";
        return context.Database.ExecuteSqlRawAsync(sql);
    }

    public static async Task ClearPhase3SourceDataAsync(NativeSqlServerFixture fixture)
    {
        await using (var context = new FluxKnowledgeDbContext(
                         new DbContextOptionsBuilder<FluxKnowledgeDbContext>()
                             .UseSqlServer(fixture.ConnectionString)
                             .Options))
        {
            await ExecuteResetBatchAsync(context, $"""
                {Delete(context.AuditEvents)}
                {Delete(context.SourceRootWatchStates)}
                {Delete(context.SourceScanOutbox)}
                {Delete(context.SourceScanJobs)}
                {Delete(context.SourceScanRequests)}
                """);
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
