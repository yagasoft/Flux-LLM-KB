using System.Data;
using FluxKnowledge.Application.Contracts;
using FluxKnowledge.Application.Indexing;
using FluxKnowledge.Application.Sources;
using FluxKnowledge.Domain.Gpu;
using FluxKnowledge.Domain.Jobs;
using FluxKnowledge.Domain.Outlook;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using Microsoft.EntityFrameworkCore;
using GpuSchedulerStore = FluxKnowledge.Application.Gpu.IGpuSchedulerStore;
using SchedulerStoreStatusSnapshot = FluxKnowledge.Application.Gpu.GpuSchedulerStatusSnapshot;

namespace FluxKnowledge.Web.Components.Status;

public sealed record PipelineRecordProjection(
    Guid Id,
    string SourceIdentity,
    long Revision,
    string CurrentStage,
    string Status,
    string ContentHashPrefix,
    DateTimeOffset RegisteredAtUtc,
    DateTimeOffset? DueAtUtc);

public interface IProjectionReader
{
    ValueTask<OverviewProjection> ReadOverviewAsync(CancellationToken cancellationToken);
    ValueTask<GpuSchedulerStatusProjection> ReadGpuSchedulerStatusAsync(CancellationToken cancellationToken);
    ValueTask<IReadOnlyList<PipelineRecordProjection>> ReadPipelineRecordsAsync(CancellationToken cancellationToken);
    ValueTask<PipelineRecordProjection?> ReadPipelineRecordAsync(Guid id, CancellationToken cancellationToken);
    ValueTask<PipelineAttentionPage> ReadPipelineAttentionAsync(string unit, Guid? sourceRootId, int page,
        CancellationToken cancellationToken) => ValueTask.FromResult(PipelineAttentionPage.Empty);
}

public sealed class SqlProjectionReader(
    IDbContextFactory<FluxKnowledgeDbContext> contextFactory,
    IDerivedIndexRecoveryStatus recoveryStatus,
    GpuSchedulerStore schedulerStore,
    IDeploymentValidationHold? validationHold = null,
    TimeProvider? timeProvider = null) : IProjectionReader
{
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;
    private readonly SqlPipelineActivityReader _activityReader = new(validationHold, timeProvider ?? TimeProvider.System);

    public async ValueTask<PipelineAttentionPage> ReadPipelineAttentionAsync(string unit, Guid? sourceRootId, int page,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            var result = await _activityReader.ReadAttentionAsync(context, unit, sourceRootId, page, cancellationToken).ConfigureAwait(false);
            if (result is not null) return result;
        }
        throw new InvalidOperationException("Status changed during both attention reads.");
    }

    public async ValueTask<OverviewProjection> ReadOverviewAsync(CancellationToken cancellationToken)
    {
        // One deadline includes the deletion precheck, lock acquisition and all
        // coordinated attempts/retry delays. Idle reads keep their caller token.
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(TimeSpan.FromSeconds(2));
        try
        {
            await using var execution = await contextFactory.CreateDbContextAsync(budget.Token).ConfigureAwait(false);
            return await execution.Database.CreateExecutionStrategy().ExecuteAsync(
                state: cancellationToken,
                operation: (_, callerToken, boundedToken) => ReadOverviewCoreAsync(callerToken, boundedToken),
                verifySucceeded: null,
                cancellationToken: budget.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested && budget.IsCancellationRequested)
        {
            // Disposal in the core completes before status is marked unavailable.
            // Caller cancellation and genuine SQL failures are not translated.
            throw new OverviewReadBudgetExceededException(exception);
        }
    }

    private async Task<OverviewProjection> ReadOverviewCoreAsync(CancellationToken cancellationToken, CancellationToken coordinationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(coordinationToken).ConfigureAwait(false);
        var startedAt = _clock.GetUtcNow();
        var deleting = await context.SourceDeletionOperations.AsNoTracking()
            .AnyAsync(operation => operation.State == 0 || operation.State == 1, coordinationToken).ConfigureAwait(false);
        if (deleting) cancellationToken = coordinationToken;
        await using var transaction = deleting
            ? await context.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken).ConfigureAwait(false)
            : null;
        if (deleting)
        {
            // Match public deletion/purge order. Claim and receipt writers only
            // need the operation rows, and finish before we capture the fence.
            await SqlPublishedPassageSelection.AcquireFenceAsync(context, cancellationToken).ConfigureAwait(false);
            _ = await context.Database.SqlQuery<Guid>($"""
                SELECT [Id] AS [Value] FROM [SourceDeletionOperations] WITH (UPDLOCK, HOLDLOCK)
                WHERE [State] IN (0, 1)
                """).ToArrayAsync(cancellationToken).ConfigureAwait(false);
        }
        var before = await SqlPipelineActivityQuery.ReadFenceAsync(context, cancellationToken).ConfigureAwait(false);
        if (!before.IsStable)
            return new(0, 0, 0, 0, 0, 0, 0, "none", new("Unknown", null, null, null, null, 0))
            {
                Activity = PipelineActivitySummary.Unknown with { ReadStartedAtUtc = startedAt, IsConsistent = false }
            };
        var jobCounts = await context.Jobs.AsNoTracking()
            .GroupBy(job => job.PublicState)
            .Select(group => new { State = group.Key, Count = group.Count() })
            .ToDictionaryAsync(item => item.State, item => item.Count, cancellationToken)
            .ConfigureAwait(false);
        var indexed = await context.PipelineRecords.AsNoTracking()
            .CountAsync(record => record.CompletionCriteriaMet && !record.IsDeleted, cancellationToken)
            .ConfigureAwait(false);
        var activeGeneration = await (
                from state in context.IndexState.AsNoTracking()
                join generation in context.IndexGenerations.AsNoTracking()
                    on state.ActiveIndexGenerationId equals generation.Id into generations
                from generation in generations.DefaultIfEmpty()
                where state.Id == 1
                select generation == null ? null : generation.Id.ToString("N"))
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false) ?? "none";
        var outlookProfileCount = await context.OutlookCaptureProfiles.AsNoTracking()
            .CountAsync(profile => profile.State != (int)OutlookCaptureState.Stale, cancellationToken).ConfigureAwait(false);
        var outlookEnabledProfileCount = await context.OutlookCaptureProfiles.AsNoTracking()
            .CountAsync(profile => profile.IsEnabled, cancellationToken).ConfigureAwait(false);
        var outlookFolderCount = await context.OutlookCaptureFolders.AsNoTracking()
            .CountAsync(folder => folder.State != (int)OutlookCaptureState.Disabled, cancellationToken).ConfigureAwait(false);
        var outlookExportCounts = await context.OutlookCaptureExports.AsNoTracking()
            .GroupBy(export => export.State)
            .Select(group => new { State = group.Key, Count = group.Count() })
            .ToDictionaryAsync(row => row.State, row => row.Count, cancellationToken)
            .ConfigureAwait(false);

        var recovery = recoveryStatus.Snapshot;
        var gpuSchedulerStatus = await ReadGpuSchedulerStatusAsync(cancellationToken)
            .ConfigureAwait(false);
        var activity = await _activityReader.ReadAsync(context, startedAt, cancellationToken).ConfigureAwait(false);
        var after = await SqlPipelineActivityQuery.ReadFenceAsync(context, cancellationToken).ConfigureAwait(false);
        activity = activity with { ObservedAtUtc = _clock.GetUtcNow(), IsConsistent = before.Accepts(after) };
        if (transaction is not null) await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new OverviewProjection(
            GetCount(PublicJobState.WorkerQueued),
            GetCount(PublicJobState.WorkerProcessing),
            GetCount(PublicJobState.GpuQueued),
            GetCount(PublicJobState.GpuProcessing),
            GetCount(PublicJobState.Completed),
            GetCount(PublicJobState.Failed),
            indexed,
            activeGeneration,
            new IndexRecoverySummary(
                recovery.State.ToString(),
                recovery.ActiveGenerationId?.ToString("N"),
                recovery.LastCompletedAtUtc,
                recovery.NextRetryAtUtc,
                recovery.FailureCategory?.ToString(),
                recovery.CleanedCandidateCount))
        {
            GpuSchedulerStatus = gpuSchedulerStatus,
            Activity = activity,
            SourceIndexing = new SourceIndexingSummary(
                activity.Sources.Count,
                activity.Sources.Sum(source => source.PublishedFiles),
                activity.Sources.Sum(source => source.DeferredFiles),
                activity.Sources.Sum(source => source.BlockedFiles),
                activity.Sources.Sum(source => source.FailedFiles + source.ScanErrors)),
            OutlookCapture = new OutlookCaptureSummary(
                outlookProfileCount,
                outlookEnabledProfileCount,
                outlookFolderCount,
                OutlookCount(OutlookExportState.Ingested),
                OutlookCount(OutlookExportState.Deferred),
                OutlookCount(OutlookExportState.Blocked))
        };

        int GetCount(PublicJobState state) => jobCounts.GetValueOrDefault((int)state);
        int OutlookCount(OutlookExportState state) => outlookExportCounts.GetValueOrDefault((int)state);
    }

    public async ValueTask<IReadOnlyList<PipelineRecordProjection>> ReadPipelineRecordsAsync(
        CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var rows = await BuildPipelineRecordQuery(context)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return rows.Select(ToProjection).ToArray();
    }

    public async ValueTask<PipelineRecordProjection?> ReadPipelineRecordAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var row = await BuildPipelineRecordQuery(context, id)
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        return row is null ? null : ToProjection(row);
    }

    public async ValueTask<GpuSchedulerStatusProjection> ReadGpuSchedulerStatusAsync(
        CancellationToken cancellationToken)
    {
        var snapshot = await schedulerStore.ReadGpuSchedulerStatusAsync(cancellationToken)
            .ConfigureAwait(false);
        return ToGpuSchedulerStatusProjection(snapshot);
    }

    private static GpuSchedulerStatusProjection ToGpuSchedulerStatusProjection(
        SchedulerStoreStatusSnapshot snapshot) =>
        new(
            snapshot.ReadyCount,
            snapshot.ActiveCount,
            snapshot.DeferredCount,
            snapshot.OutcomeUncertainCount,
            new GpuSchedulerLaneCounts(
                snapshot.LaneCounts.GetValueOrDefault(GpuPriorityLane.InteractiveRetrieval),
                snapshot.LaneCounts.GetValueOrDefault(GpuPriorityLane.DocumentIndexing),
                snapshot.LaneCounts.GetValueOrDefault(GpuPriorityLane.ImageOcr),
                snapshot.LaneCounts.GetValueOrDefault(GpuPriorityLane.ImageEnrichment),
                snapshot.LaneCounts.GetValueOrDefault(GpuPriorityLane.VideoOrUnknown)),
            snapshot.HasActiveBatch,
            snapshot.ActiveBatchLane?.ToString(),
            snapshot.AvailableSlotCount,
            snapshot.ReservedSlotCount,
            snapshot.UncertainSlotCount,
            snapshot.NextDeferredAtUtc,
            new GpuCapacityUncertaintySummary(
                snapshot.UncertainSlotCount == 0 ? "None" : "Uncertain",
                ToBoundedAgeMinutes(snapshot.UncertainCapacityAge)));

    private static int? ToBoundedAgeMinutes(TimeSpan? uncertainCapacityAge) =>
        uncertainCapacityAge is not { } age
            ? null
            : Math.Clamp(
                (int)Math.Ceiling(age.TotalMinutes),
                0,
                MaximumUncertainCapacityAgeMinutes);

    private const int MaximumUncertainCapacityAgeMinutes = 24 * 60;

    private static IQueryable<PipelineRecordProjectionRow> BuildPipelineRecordQuery(
        FluxKnowledgeDbContext context,
        Guid? pipelineRecordId = null)
    {
        var records = context.PipelineRecords
            .AsNoTracking()
            .Where(record => !record.IsDeleted);
        if (pipelineRecordId is { } id)
        {
            records = records.Where(record => record.Id == id);
        }

        return
        from record in records
        join identity in context.SourceIdentities.AsNoTracking() on record.SourceIdentityId equals identity.Id
        join job in context.Jobs.AsNoTracking()
                .Where(job => job.SourceRevision > 0)
                .OrderByDescending(job => job.DueAtUtc)
            on new { PipelineRecordId = record.Id, SourceRevision = record.Revision, Stage = record.CurrentStage }
            equals new { job.PipelineRecordId, job.SourceRevision, job.Stage } into jobs
        from job in jobs.Take(1).DefaultIfEmpty()
        orderby record.RegisteredAtUtc descending
        select new PipelineRecordProjectionRow(
            record.Id,
            identity.StableKey,
            record.Revision,
            record.CurrentStage,
            job == null ? null : job.PublicState,
            record.ContentHash,
            record.RegisteredAtUtc,
            job == null ? null : job.DueAtUtc);
    }

    private static PipelineRecordProjection ToProjection(PipelineRecordProjectionRow row) =>
        new(
            row.Id,
            row.SourceIdentity,
            row.Revision,
            ((FluxKnowledge.Domain.Pipeline.PipelineStage)row.CurrentStage).ToString(),
            row.PublicState is null ? "Unscheduled" : ((PublicJobState)row.PublicState.Value).ToString(),
            row.ContentHash[..12],
            row.RegisteredAtUtc,
            row.DueAtUtc);

    private sealed record PipelineRecordProjectionRow(
        Guid Id,
        string SourceIdentity,
        long Revision,
        int CurrentStage,
        int? PublicState,
        string ContentHash,
        DateTimeOffset RegisteredAtUtc,
        DateTimeOffset? DueAtUtc);
}
