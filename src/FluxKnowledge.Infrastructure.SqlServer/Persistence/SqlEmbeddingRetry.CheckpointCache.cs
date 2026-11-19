using FluxKnowledge.Application.Gpu;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence;

internal sealed partial record SqlEmbeddingRetry
{
    internal const string CheckpointValidationKind = "embedding-checkpoint-validation/v1";
    private sealed record ReusedGpuProofs(Dictionary<Guid, byte[]> Digests,
        List<GpuSchedulerOperationReceiptEntity> Receipts);

    private static async Task<ReusedGpuProofs> ReadReusableSuccessfulResultDigestsAsync(
        FluxKnowledgeDbContext context, JobEntity job, IndexGenerationEntity draft, EmbeddingGpuRuntime runtime,
        EmbeddingGpuRequestEntity[] requests, IReadOnlyDictionary<Guid, GpuMiniTaskEntity> tasks,
        IReadOnlyDictionary<Guid, GpuBatchEntity> batches, ILookup<Guid, GpuExecutorDispatchEntity> dispatches,
        CancellationToken ct)
    {
        var successful = requests.Where(request => request.ResultDigest is { Length: 32 }).ToArray();
        var batchIds = successful.Select(request => tasks[request.MiniTaskId].BatchId)
            .Where(id => id.HasValue).Select(id => id!.Value).Distinct().ToArray();
        var saved = await context.GpuSchedulerOperationReceipts.AsNoTracking()
            .Where(receipt => receipt.OperationKind == CheckpointValidationKind && receipt.Accepted && receipt.Committed &&
                receipt.BatchId != null && batchIds.Contains(receipt.BatchId.Value) && receipt.RequestFingerprint != null)
            .Select(receipt => new { receipt.BatchId, receipt.RequestFingerprint }).ToArrayAsync(ct);
        var known = saved.Select(receipt => (receipt.BatchId!.Value, receipt.RequestFingerprint!)).ToHashSet();
        var digests = new Dictionary<Guid, byte[]>();
        var receipts = new List<GpuSchedulerOperationReceiptEntity>();
        var covered = new HashSet<long>();
        // Metadata has no vector bodies: allow up to 128 groups here. Actual
        // payload misses still use the original 128-chunk bounded reader.
        const int metadataGroupsPerPage = 128;
        for (var start = 0; start < successful.Length; start += metadataGroupsPerPage)
        {
            var groups = successful.Skip(start).Take(metadataGroupsPerPage)
                .Select(request => new { Request = request, Inputs = ReadGroupInputs(request) }).ToArray();
            var ids = groups.SelectMany(group => group.Inputs.Select(input => input.Id)).Distinct().ToArray();
            var metadata = await (from chunk in SqlEmbeddingCheckpointStore.Chunks(context, job.PipelineRecordId, job.SourceRevision)
                join vector in context.Vectors.AsNoTracking() on chunk.Id equals vector.TextChunkId
                where ids.Contains(chunk.Id) && vector.IndexGenerationId == draft.Id
                select new
                {
                    chunk.Id, chunk.ArtifactId, chunk.Ordinal, chunk.StartOffset, chunk.Length,
                    chunk.ContentHash, chunk.SearchInputHash, chunk.PassagePolicyFingerprint,
                    vector.VectorId, vector.RowVersion, vector.PayloadChecksum, vector.TextChunkContentHash,
                    VectorInput = vector.SearchInputHash, vector.ModelFingerprint, vector.Dimensions,
                    vector.SourceRevision, vector.IsDeleted, PayloadLength = vector.Values.Length
                }).ToArrayAsync(ct);
            var byChunk = metadata.ToDictionary(value => value.Id);
            var missing = new List<EmbeddingGpuRequestEntity>();
            var pending = new List<(EmbeddingGpuRequestEntity Request, Guid BatchId, string Stamp)>();
            foreach (var group in groups)
            {
                if (group.Inputs.Any(input => !byChunk.TryGetValue(input.Id, out var vector) || vector.SearchInputHash != input.Hash)) Refuse();
                covered.UnionWith(group.Inputs.Select(input => input.Id));
                var task = tasks[group.Request.MiniTaskId];
                var batchId = task.BatchId.GetValueOrDefault();
                var stamp = Hash(new
                {
                    Version = 1, JobId = job.Id, job.PipelineRecordId, job.SourceRevision,
                    GenerationId = draft.Id, draft.CorpusEpoch, draft.ModelFingerprint, draft.Dimensions,
                    runtime.RuntimeKey, runtime.SettingsFingerprint,
                    group.Request.MiniTaskId, RequestVersion = group.Request.RowVersion,
                    group.Request.InputDigest, group.Request.ResultDigest,
                    TaskVersion = task.RowVersion, BatchId = batchId,
                    BatchVersion = batches.GetValueOrDefault(batchId)?.RowVersion,
                    Dispatches = dispatches[batchId].OrderBy(dispatch => dispatch.DispatchId)
                        .Select(dispatch => new { dispatch.DispatchId, dispatch.RowVersion }).ToArray(),
                    Inputs = group.Inputs, Vectors = group.Inputs.Select(input => byChunk[input.Id]).ToArray()
                });
                if (known.Contains((batchId, stamp))) digests.Add(group.Request.MiniTaskId, group.Request.ResultDigest!);
                else
                {
                    missing.Add(group.Request);
                    pending.Add((group.Request, batchId, stamp));
                }
            }
            var validated = await ReadSuccessfulResultDigestsAsync(context, job, draft, missing.ToArray(), ct,
                validatePayloadMetadata: true);
            foreach (var item in pending)
            {
                var digest = validated[item.Request.MiniTaskId];
                if (!digest.AsSpan().SequenceEqual(item.Request.ResultDigest)) Refuse();
                digests.Add(item.Request.MiniTaskId, digest);
                receipts.Add(new()
                {
                    OperationId = Guid.NewGuid(), OperationKind = CheckpointValidationKind,
                    BatchId = item.BatchId, RequestFingerprint = item.Stamp,
                    Accepted = true, Committed = true, CreatedAtUtc = DateTimeOffset.UtcNow
                });
            }
        }
        // Mixed legacy/CPU work cannot borrow a native group's proof. Validate
        // any uncovered rows fully, without computing a discarded aggregate SHA.
        // A large NOT IN(OPENJSON(allCoveredIds)) becomes quadratic in SQL.
        // Page all identities once and compare against the in-memory set.
        long afterVector = 0;
        while (true)
        {
            var identities = await context.Vectors.AsNoTracking().Where(vector => vector.IndexGenerationId == draft.Id &&
                    vector.VectorId > afterVector).OrderBy(vector => vector.VectorId).Take(1024)
                .Select(vector => new { vector.VectorId, vector.TextChunkId }).ToArrayAsync(ct);
            if (identities.Length == 0) break;
            var uncovered = identities.Where(vector => !covered.Contains(vector.TextChunkId)).Select(vector => vector.VectorId).ToArray();
            for (var start = 0; start < uncovered.Length; start += 128)
            {
                var ids = uncovered.Skip(start).Take(128).ToArray();
                var page = await context.Vectors.AsNoTracking().Include(vector => vector.TextChunk)
                    .Where(vector => vector.IndexGenerationId == draft.Id && ids.Contains(vector.VectorId)).ToArrayAsync(ct);
                foreach (var vector in page) SqlEmbeddingCheckpointStore.ValidateStoredPayload(vector, vector.TextChunk, draft);
            }
            afterVector = identities[^1].VectorId;
        }
        return new(digests, receipts);
    }
}
