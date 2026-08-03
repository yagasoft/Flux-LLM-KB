using FluxKnowledge.Application.Contracts;
using FluxKnowledge.Application.Indexing;
using FluxKnowledge.Domain.Gpu;
using FluxKnowledge.Domain.Jobs;
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
}

public sealed class SqlProjectionReader(
    IDbContextFactory<FluxKnowledgeDbContext> contextFactory,
    IDerivedIndexRecoveryStatus recoveryStatus,
    GpuSchedulerStore schedulerStore) : IProjectionReader
{
    public async ValueTask<OverviewProjection> ReadOverviewAsync(CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
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

        var recovery = recoveryStatus.Snapshot;
        var gpuSchedulerStatus = await ReadGpuSchedulerStatusAsync(cancellationToken)
            .ConfigureAwait(false);
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
            GpuSchedulerStatus = gpuSchedulerStatus
        };

        int GetCount(PublicJobState state) => jobCounts.GetValueOrDefault((int)state);
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
