using System.Data;
using System.Text.Json;
using FluxKnowledge.Application.Contracts;
using FluxKnowledge.Domain.Pipeline;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Web.Components.Sources;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace FluxKnowledge.Web.Components.Status;

public sealed partial class SqlProjectionReader
{
    public async ValueTask<PipelineFilePage> ReadPipelineFilesAsync(PipelineFileFilter filter, Guid? sourceRootId,
        int page, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(filter);
        if (filter.Category is not ("all" or "Published" or "Pending" or "Blocked" or "Failed" or "Deferred" or "MetadataOnly" or "NoText" or "Unconfirmed"))
            throw new ArgumentException("Unknown file category.", nameof(filter));
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(TimeSpan.FromSeconds(2));
        try
        {
            await using var execution = await contextFactory.CreateDbContextAsync(budget.Token).ConfigureAwait(false);
            return await execution.Database.CreateExecutionStrategy().ExecuteAsync(async token =>
            {
                await using var context = await contextFactory.CreateDbContextAsync(token).ConfigureAwait(false);
                await using var transaction = ReadOptions.UseSnapshotIsolation
                    ? await context.Database.BeginTransactionAsync(IsolationLevel.Snapshot, token).ConfigureAwait(false) : null;
                var before = ReadOptions.UseSnapshotIsolation ? new ActivityReadFence { IsStable = true }
                    : await SqlPipelineActivityQuery.ReadFenceAsync(context, token).ConfigureAwait(false);
                if (!before.IsStable) throw new InvalidOperationException("Status has a pending transaction.");
                var roots = await context.SourceRootConfigurations.AsNoTracking()
                    .Where(r => sourceRootId == null || r.Id == sourceRootId)
                    .Select(r => new { r.Id, r.DisplayName, r.State }).ToArrayAsync(token).ConfigureAwait(false);
                var names = roots.ToDictionary(r => r.Id, r => r.DisplayName);
                if (sourceRootId is not null && roots.Length == 0) return PipelineFilePage.Unavailable;
                var sources = await SourceRootProjectionReader.ReadStateSummariesAsync(context, names.Keys.ToArray(), token).ConfigureAwait(false);
                await using var branches = await SqlSelectedBranchReadScope.CreateAsync(context,
                    sources.Values.SelectMany(s => s.SelectedBranches).Distinct().ToArray(), token).ConfigureAwait(false);
                var now = _clock.GetUtcNow();
                var admission = validationHold?.ReadAdmissionState() ?? new(false, null);
                var evidence = await SqlGpuActivityReadEvidence.ReadAsync(context, token).ConfigureAwait(false);
                await branches.MaterializeGpuFactsAsync(now, evidence, token).ConfigureAwait(false);
                var samples = await PipelineFileSample.ReadAsync(context, sources,
                    SqlPipelineActivityQuery.Jobs(context, now, admission, branches, evidence), names,
                    roots.ToDictionary(r => r.Id, r => ((FluxKnowledge.Domain.Sources.SourceRootState)r.State).ToString()), now, evidence, token).ConfigureAwait(false);
                var matches = samples.Select(s => s.File).Where(f =>
                    (filter.Category == "all" || f.Category == filter.Category) &&
                    (filter.Stage is null || f.Stage == filter.Stage) &&
                    (filter.Reason is null || f.Reason == filter.Reason) &&
                    (filter.State is null || f.State == filter.State))
                    .OrderBy(f => f.FullPath, StringComparer.Ordinal).ThenBy(f => f.SourceRevisionId).ToArray();
                const int pageSize = 25;
                page = Math.Clamp(page, 0, Math.Max(0, (matches.Length - 1) / pageSize));
                var items = matches.Skip(page * pageSize).Take(pageSize).ToArray();
                var progress = await ReadEmbeddingProgressAsync(context, items, token).ConfigureAwait(false);
                items = items.Select(f => f with { Embedding = f.Jobs.Where(j => j.Stage == "Embed")
                    .Select(j => progress.GetValueOrDefault(j.Id) ?? new(j.Id, null, null, null, "Compatible checkpoint evidence unavailable.")).ToArray() }).ToArray();
                var after = ReadOptions.UseSnapshotIsolation ? before
                    : await SqlPipelineActivityQuery.ReadFenceAsync(context, token).ConfigureAwait(false);
                await branches.CompleteAsync(token).ConfigureAwait(false);
                if (!before.Accepts(after)) throw new InvalidOperationException("Status changed during the file read.");
                if (transaction is not null) await transaction.CommitAsync(token).ConfigureAwait(false);
                return new PipelineFilePage(matches.Length, page, pageSize, now, items);
            }, budget.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested && budget.IsCancellationRequested)
        {
            throw new OverviewReadBudgetExceededException(exception);
        }
    }

    private static async Task<IReadOnlyDictionary<Guid, PipelineEmbeddingProgress>> ReadEmbeddingProgressAsync(
        FluxKnowledgeDbContext context, IReadOnlyList<PipelineFileItem> files, CancellationToken cancellationToken)
    {
        var ids = files.SelectMany(f => f.Jobs).Where(j => j.Stage == nameof(PipelineStage.Embed)).Select(j => j.Id).Distinct().ToArray();
        if (ids.Length == 0) return new Dictionary<Guid, PipelineEmbeddingProgress>();
        // One command for the bounded page. Count inventory chunks once, independently of
        // retries, copied vectors and index memberships. Never select payload columns.
        const string sql = """
            SELECT job.Id AS JobId,
              CASE WHEN draft.Id IS NULL OR inventory.ArtifactCount <> 1 THEN NULL ELSE progress.Total END AS TotalPassages,
              CASE WHEN draft.Id IS NULL OR inventory.ArtifactCount <> 1 THEN NULL ELSE progress.Saved END AS SavedPassages,
              CASE WHEN draft.Id IS NULL OR inventory.ArtifactCount <> 1 THEN NULL ELSE progress.LastSaved END AS LastSavedAtUtc
            FROM OPENJSON(@jobs) WITH (Id uniqueidentifier '$') chosen
            INNER JOIN Jobs job ON job.Id = chosen.Id AND job.Stage = 4
            INNER JOIN PipelineRecords record ON record.Id = job.PipelineRecordId AND record.Revision = job.SourceRevision
              AND record.CurrentStage = job.Stage AND record.IsDeleted = 0
            INNER JOIN IndexState state ON state.Id = 1
            LEFT JOIN IndexGenerations draft ON draft.EmbeddingJobId = job.Id AND draft.CorpusEpoch = state.CorpusEpoch
            OUTER APPLY (SELECT COUNT(*) AS ArtifactCount FROM Artifacts artifact
              WHERE artifact.PipelineRecordId = record.Id AND artifact.SourceRevision = record.Revision AND artifact.Stage = 3) inventory
            OUTER APPLY (
              SELECT COUNT_BIG(*) AS Total, COUNT_BIG(saved.FirstSaved) AS Saved, MAX(saved.FirstSaved) AS LastSaved
              FROM TextChunks chunk INNER JOIN Artifacts artifact ON artifact.Id = chunk.ArtifactId
              OUTER APPLY (SELECT MIN(vector.CreatedAtUtc) AS FirstSaved FROM Vectors vector
                WHERE vector.IndexGenerationId = draft.Id AND vector.TextChunkId = chunk.Id AND vector.IsDeleted = 0
                  AND vector.SourceRevision = record.Revision
                  AND vector.ModelFingerprint COLLATE Latin1_General_100_BIN2 = draft.ModelFingerprint COLLATE Latin1_General_100_BIN2
                  AND vector.Dimensions = draft.Dimensions
                  AND vector.TextChunkContentHash COLLATE Latin1_General_100_BIN2 = chunk.ContentHash COLLATE Latin1_General_100_BIN2
                  AND vector.SearchInputHash COLLATE Latin1_General_100_BIN2 = chunk.SearchInputHash COLLATE Latin1_General_100_BIN2) saved
              WHERE artifact.PipelineRecordId = record.Id AND artifact.SourceRevision = record.Revision
                AND artifact.Stage = 3 AND chunk.SourceRevision = record.Revision
            ) progress
            """;
        var rows = await context.Database.SqlQueryRaw<PipelineEmbeddingProgressRow>(sql,
            new SqlParameter("@jobs", SqlDbType.NVarChar, -1) { Value = JsonSerializer.Serialize(ids) })
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        return rows.ToDictionary(r => r.JobId, r => new PipelineEmbeddingProgress(r.JobId, r.SavedPassages,
            r.TotalPassages, r.LastSavedAtUtc, r.SavedPassages is null ? "Compatible current checkpoint or passage inventory unavailable."
                : "Distinct compatible passages in the current embedding checkpoint. Timing describes saved checkpoint data; native execution is separate."));
    }

    private sealed class PipelineEmbeddingProgressRow
    {
        public Guid JobId { get; set; }
        public long? SavedPassages { get; set; }
        public long? TotalPassages { get; set; }
        public DateTimeOffset? LastSavedAtUtc { get; set; }
    }
}
