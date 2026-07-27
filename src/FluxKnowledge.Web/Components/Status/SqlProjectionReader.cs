using FluxKnowledge.Application.Contracts;
using FluxKnowledge.Domain.Jobs;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FluxKnowledge.Web.Components.Status;

public sealed record PipelineRecordProjection(
    Guid Id,
    string SourceIdentity,
    long Revision,
    string CurrentStage,
    string Status,
    string ContentHashPrefix,
    DateTimeOffset RegisteredAtUtc,
    DateTimeOffset? LastActivityAtUtc);

public interface IProjectionReader
{
    ValueTask<OverviewProjection> ReadOverviewAsync(CancellationToken cancellationToken);
    ValueTask<IReadOnlyList<PipelineRecordProjection>> ReadPipelineRecordsAsync(CancellationToken cancellationToken);
    ValueTask<PipelineRecordProjection?> ReadPipelineRecordAsync(Guid id, CancellationToken cancellationToken);
}

public sealed class SqlProjectionReader(IDbContextFactory<FluxKnowledgeDbContext> contextFactory) : IProjectionReader
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

        return new OverviewProjection(
            GetCount(PublicJobState.WorkerQueued),
            GetCount(PublicJobState.WorkerProcessing),
            GetCount(PublicJobState.GpuQueued),
            GetCount(PublicJobState.GpuProcessing),
            GetCount(PublicJobState.Completed),
            GetCount(PublicJobState.Failed),
            indexed,
            activeGeneration);

        int GetCount(PublicJobState state) => jobCounts.GetValueOrDefault((int)state);
    }

    public async ValueTask<IReadOnlyList<PipelineRecordProjection>> ReadPipelineRecordsAsync(
        CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await BuildPipelineRecordQuery(context)
            .OrderByDescending(record => record.RegisteredAtUtc)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public async ValueTask<PipelineRecordProjection?> ReadPipelineRecordAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await BuildPipelineRecordQuery(context)
            .SingleOrDefaultAsync(record => record.Id == id, cancellationToken)
            .ConfigureAwait(false);
    }

    private static IQueryable<PipelineRecordProjection> BuildPipelineRecordQuery(FluxKnowledgeDbContext context) =>
        from record in context.PipelineRecords.AsNoTracking()
        join identity in context.SourceIdentities.AsNoTracking() on record.SourceIdentityId equals identity.Id
        join job in context.Jobs.AsNoTracking()
                .Where(job => job.SourceRevision > 0)
                .OrderByDescending(job => job.DueAtUtc)
            on new { PipelineRecordId = record.Id, SourceRevision = record.Revision, Stage = record.CurrentStage }
            equals new { job.PipelineRecordId, job.SourceRevision, job.Stage } into jobs
        from job in jobs.Take(1).DefaultIfEmpty()
        where !record.IsDeleted
        select new PipelineRecordProjection(
            record.Id,
            identity.StableKey,
            record.Revision,
            ((FluxKnowledge.Domain.Pipeline.PipelineStage)record.CurrentStage).ToString(),
            job == null ? "Unscheduled" : ((PublicJobState)job.PublicState).ToString(),
            record.ContentHash.Substring(0, 12),
            record.RegisteredAtUtc,
            job == null ? null : job.DueAtUtc);
}
