using System.Data;
using System.Diagnostics;
using System.Security.Cryptography;
using FluxKnowledge.Application.Indexing;
using FluxKnowledge.Application.Search;
using FluxKnowledge.Application.Workers;
using FluxKnowledge.Domain.Jobs;
using FluxKnowledge.Domain.Pipeline;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence;

public sealed partial class SqlDerivedIndexRecoveryStore
{
    // The caller retains its serializable publication fence. Batch metadata and
    // stream payloads without relaxing the retained-checkpoint integrity proof.
    private static async Task ValidateCheckpointDraftsAsync(FluxKnowledgeDbContext context,
        IReadOnlyList<GenerationRow> rows, Guid? activeGenerationId, CancellationToken ct)
    {
        if (rows.Count == 0) return;
        var epoch = (await SqlPublishedPassageSelection.ReadStampAsync(context, ct).ConfigureAwait(false)).CorpusEpoch;
        foreach (var batch in rows.Chunk(QueryBatchSize))
        {
            var ids = batch.Select(row => row.Id).ToArray();
            var drafts = await context.IndexGenerations.AsNoTracking().Where(value => ids.Contains(value.Id))
                .ToDictionaryAsync(value => value.Id, ct).ConfigureAwait(false);
            if (drafts.Count != ids.Length || ids.Contains(activeGenerationId ?? Guid.Empty) ||
                await context.IndexGenerationVectors.AnyAsync(value => ids.Contains(value.GenerationId), ct).ConfigureAwait(false))
                throw new InvalidOperationException("embedding-checkpoint-recovery-provenance-invalid");
            var jobIds = drafts.Values.Select(draft => draft.EmbeddingJobId!.Value).ToArray();
            var embeddingJobs = await context.Jobs.AsNoTracking().Where(value => jobIds.Contains(value.Id)).ToArrayAsync(ct).ConfigureAwait(false);
            var recordIds = embeddingJobs.Select(value => value.PipelineRecordId).Distinct().ToArray();
            var records = await context.PipelineRecords.AsNoTracking().Where(value => recordIds.Contains(value.Id))
                .ToDictionaryAsync(value => value.Id, ct).ConfigureAwait(false);
            var jobs = await context.Jobs.AsNoTracking().Where(value => recordIds.Contains(value.PipelineRecordId) &&
                value.Stage == (int)PipelineStage.Publish).ToArrayAsync(ct).ConfigureAwait(false);
            var outbox = await context.OutboxMessages.AsNoTracking().Where(value => recordIds.Contains(value.PipelineRecordId) &&
                (value.Stage == (int)PipelineStage.Embed || value.Stage == (int)PipelineStage.Publish)).ToArrayAsync(ct).ConfigureAwait(false);
            var artifacts = await context.Artifacts.AsNoTracking().Where(value => recordIds.Contains(value.PipelineRecordId) &&
                value.Stage == (int)PipelineStage.Embed).ToArrayAsync(ct).ConfigureAwait(false);
            var chunkCounts = await (from chunk in context.TextChunks.AsNoTracking()
                join artifact in context.Artifacts.AsNoTracking() on chunk.ArtifactId equals artifact.Id
                where recordIds.Contains(artifact.PipelineRecordId) && artifact.Stage == (int)PipelineStage.CanonicalIndex &&
                    chunk.SourceRevision == artifact.SourceRevision
                group chunk by new { artifact.PipelineRecordId, artifact.SourceRevision } into grouped
                select new { grouped.Key.PipelineRecordId, grouped.Key.SourceRevision, Count = grouped.LongCount() })
                .ToArrayAsync(ct).ConfigureAwait(false);
            var jobById = embeddingJobs.ToDictionary(value => value.Id);
            foreach (var draft in drafts.Values)
            {
                if (!jobById.TryGetValue(draft.EmbeddingJobId!.Value, out var job) ||
                    !records.TryGetValue(job.PipelineRecordId, out var record))
                    throw new InvalidOperationException("embedding-checkpoint-recovery-provenance-invalid");
                var dispatch = outbox.SingleOrDefault(value => value.JobId == job.Id && value.PipelineRecordId == job.PipelineRecordId &&
                    value.SourceRevision == job.SourceRevision && value.Stage == (int)PipelineStage.Embed && value.Operation == PipelineOperations.Embed);
                var completed = job.PublicState == (int)PublicJobState.Completed;
                var gpuPending = job.PublicState is (int)PublicJobState.GpuQueued or (int)PublicJobState.GpuProcessing;
                if (dispatch is null || job.Stage != (int)PipelineStage.Embed || job.Operation != PipelineOperations.Embed ||
                    record.IsDeleted || record.Revision != job.SourceRevision ||
                    record.CurrentStage != (completed ? (int)PipelineStage.Publish : (int)PipelineStage.Embed) ||
                    (!completed && !gpuPending && job.PublicState is not ((int)PublicJobState.WorkerQueued) and not ((int)PublicJobState.WorkerProcessing) and not ((int)PublicJobState.Failed)) ||
                    (gpuPending && !await HasEmbeddingGpuProvenanceAsync(context, draft, job, ct).ConfigureAwait(false)) ||
                    (completed && dispatch.DispatchedAtUtc is null))
                    throw new InvalidOperationException("embedding-checkpoint-recovery-provenance-invalid");
                SqlEmbeddingCheckpointStore.ValidateDraft(draft, job.Id, new(draft.ModelFingerprint, draft.Dimensions), epoch);
                if (completed && chunkCounts.SingleOrDefault(value => value.PipelineRecordId == record.Id && value.SourceRevision == record.Revision)?.Count != draft.VectorCount &&
                    !(draft.VectorCount == 0 && !chunkCounts.Any(value => value.PipelineRecordId == record.Id && value.SourceRevision == record.Revision)))
                    throw new InvalidOperationException("embedding-checkpoint-incomplete");
                if (!completed && artifacts.Any(value => value.PipelineRecordId == record.Id && value.SourceRevision == record.Revision))
                    throw new InvalidOperationException("embedding-checkpoint-premature-seal");
            }

            var hashes = drafts.Keys.ToDictionary(id => id, _ => IncrementalHash.CreateHash(HashAlgorithmName.SHA256));
            var counts = drafts.Keys.ToDictionary(id => id, _ => 0L);
            var readId = Guid.NewGuid().ToString("N");
            var readTimer = Stopwatch.StartNew();
            long readRows = 0;
            var readOutcome = "incomplete";
            try
            {
                // Use ADO directly: EF retry strategies may buffer a streaming
                // query, while keyset pages repeat this cross-draft join/sort.
                // The outer strategy still retries the complete read transaction.
                await using var command = context.Database.GetDbConnection().CreateCommand();
                command.Transaction = context.Database.CurrentTransaction!.GetDbTransaction();
                command.CommandTimeout = context.Database.GetCommandTimeout() ?? 30;
                var parameters = ids.Select((id, index) => new SqlParameter("@draft" + index, SqlDbType.UniqueIdentifier) { Value = id }).ToArray();
                command.Parameters.AddRange(parameters);
                command.CommandText = $"""
                    SELECT v.IndexGenerationId,v.VectorId,v.TextChunkId,v.ModelFingerprint,v.Dimensions,
                        v.TextChunkContentHash,v.PayloadChecksum,v.SearchInputHash,v.SourceRevision,v.IsDeleted,
                        c.Id,c.Ordinal,c.StartOffset,c.Length,c.Content,c.ContentHash,c.PassagePolicyFingerprint,c.ContextHeader,c.SearchInputHash,c.SourceRevision,
                        a.PipelineRecordId,a.SourceRevision,a.Stage,v.[Values]
                    FROM Vectors v JOIN TextChunks c ON c.Id=v.TextChunkId JOIN Artifacts a ON a.Id=c.ArtifactId
                    WHERE v.IndexGenerationId IN ({string.Join(',', parameters.Select(parameter => parameter.ParameterName))})
                    ORDER BY v.IndexGenerationId,c.Ordinal,c.Id,v.VectorId
                    """;
                await using var payloads = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, ct).ConfigureAwait(false);
                while (await payloads.ReadAsync(ct).ConfigureAwait(false))
                {
                    readRows++;
                    var vector = new VectorEntity
                    {
                        IndexGenerationId = payloads.GetGuid(0), VectorId = payloads.GetInt64(1), TextChunkId = payloads.GetInt64(2),
                        ModelFingerprint = payloads.GetString(3), Dimensions = payloads.GetInt32(4), TextChunkContentHash = payloads.GetString(5),
                        PayloadChecksum = payloads.GetString(6), SearchInputHash = payloads.IsDBNull(7) ? null : payloads.GetString(7),
                        SourceRevision = payloads.GetInt64(8), IsDeleted = payloads.GetBoolean(9)
                    };
                    var chunk = new TextChunkEntity
                    {
                        Id = payloads.GetInt64(10), Ordinal = payloads.GetInt32(11), StartOffset = payloads.GetInt32(12), Length = payloads.GetInt32(13),
                        Content = payloads.GetString(14), ContentHash = payloads.GetString(15), PassagePolicyFingerprint = payloads.GetString(16),
                        ContextHeader = payloads.GetString(17), SearchInputHash = payloads.GetString(18), SourceRevision = payloads.GetInt64(19)
                    };
                    var recordId = payloads.GetGuid(20);
                    var artifactRevision = payloads.GetInt64(21);
                    var artifactStage = payloads.GetInt32(22);
                    var draft = drafts[vector.IndexGenerationId];
                    var job = jobById[draft.EmbeddingJobId!.Value];
                    if (vector.IsDeleted || vector.SourceRevision != job.SourceRevision ||
                        vector.ModelFingerprint != draft.ModelFingerprint || vector.Dimensions != draft.Dimensions ||
                        vector.SearchInputHash is null || recordId != job.PipelineRecordId || artifactRevision != job.SourceRevision ||
                        chunk.SourceRevision != job.SourceRevision || artifactStage != (int)PipelineStage.CanonicalIndex ||
                        chunk.ContentHash != vector.TextChunkContentHash || chunk.SearchInputHash != vector.SearchInputHash)
                        throw new InvalidOperationException("embedding-checkpoint-stored-input-invalid");
                    if (payloads.GetBytes(23, 0, null, 0, 0) != (long)draft.Dimensions * sizeof(float))
                        throw new InvalidOperationException("embedding-checkpoint-stored-payload-invalid");
                    vector.Values = new byte[draft.Dimensions * sizeof(float)];
                    if (payloads.GetBytes(23, 0, vector.Values, 0, vector.Values.Length) != vector.Values.Length)
                        throw new InvalidOperationException("embedding-checkpoint-stored-payload-invalid");
                    SqlEmbeddingCheckpointStore.ValidateStoredPayload(vector, chunk, draft);
                    hashes[draft.Id].AppendData(vector.Values);
                    counts[draft.Id]++;
                }
                foreach (var draft in drafts.Values)
                {
                    if (counts[draft.Id] != draft.VectorCount) throw new InvalidOperationException("embedding-checkpoint-stored-input-invalid");
                    var job = jobById[draft.EmbeddingJobId!.Value];
                    if (job.PublicState != (int)PublicJobState.Completed) continue;
                    var checksum = Convert.ToHexStringLower(hashes[draft.Id].GetHashAndReset());
                    var artifact = artifacts.SingleOrDefault(value => value.PipelineRecordId == job.PipelineRecordId && value.SourceRevision == job.SourceRevision);
                    var dispatch = outbox.Single(value => value.JobId == job.Id && value.Stage == (int)PipelineStage.Embed && value.Operation == PipelineOperations.Embed &&
                        value.PipelineRecordId == job.PipelineRecordId && value.SourceRevision == job.SourceRevision);
                    if (artifact is null || artifact.SearchText != draft.Id.ToString("D") || artifact.ContentHash != checksum ||
                        artifact.ContentType != EmbedDraftDefaults.ArtifactContentType || !outbox.Any(next =>
                            next.PipelineRecordId == job.PipelineRecordId && next.SourceRevision == job.SourceRevision &&
                            next.Stage == (int)PipelineStage.Publish && next.Operation == PipelineOperations.Publish &&
                            next.DispatchGeneration == dispatch.DispatchGeneration + 1 && jobs.Any(nextJob => nextJob.Id == next.JobId &&
                                nextJob.PipelineRecordId == job.PipelineRecordId && nextJob.SourceRevision == job.SourceRevision &&
                                nextJob.Stage == (int)PipelineStage.Publish && nextJob.Operation == PipelineOperations.Publish)))
                        throw new InvalidOperationException("embedding-checkpoint-recovery-seal-invalid");
                }
                readOutcome = "completed";
            }
            finally
            {
                foreach (var hash in hashes.Values) hash.Dispose();
                HybridSearchDiagnostics.Log.CheckpointRead(readId, ids.Length, readRows, readTimer.Elapsed.TotalMilliseconds,
                    ct.IsCancellationRequested ? "cancelled" : readOutcome);
            }
        }
    }
}
