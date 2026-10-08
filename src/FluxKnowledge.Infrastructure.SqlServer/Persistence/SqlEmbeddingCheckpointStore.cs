using System.Data;
using System.Security.Cryptography;
using FluxKnowledge.Application.Indexing;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Pipeline;
using FluxKnowledge.Application.Gpu;
using FluxKnowledge.Application.Workers;
using FluxKnowledge.Domain.Jobs;
using FluxKnowledge.Domain.Pipeline;
using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence;

public sealed class SqlEmbeddingCheckpointStore(
    IDbContextFactory<FluxKnowledgeDbContext> contextFactory, TimeProvider timeProvider, EmbeddingGpuRuntime? embeddingRuntime = null) : IEmbeddingCheckpointStore
{
    internal const int MaximumBatchSize = 4;

    public ValueTask<EmbeddingWorkBatch> ReadNextAsync(StageWorkItem work, EmbeddingProfile profile, CancellationToken cancellationToken)
        => ExecuteAsync(async context =>
        {
            ValidateProfile(profile);
            await SqlCorpusRebuildStore.ValidateMaintenanceJobAsync(context, work.Job.JobId.Value, cancellationToken, profile).ConfigureAwait(false);
            await ValidateClaimAsync(context, work, timeProvider.GetUtcNow(), cancellationToken).ConfigureAwait(false);
            var checkpointJob = await context.Jobs.SingleAsync(value => value.Id == work.Job.JobId.Value, cancellationToken).ConfigureAwait(false);
            var refusal = await SqlRepositoryWorkRecovery.CheckCheckpointAsync(context, checkpointJob, embeddingRuntime, cancellationToken, profile).ConfigureAwait(false);
            if (refusal is not null) throw new RepositorySourceDeferredException(refusal);
            var state = await context.IndexState.SingleAsync(value => value.Id == 1, cancellationToken).ConfigureAwait(false);
            var draft = await context.IndexGenerations.SingleOrDefaultAsync(value => value.EmbeddingJobId == work.Job.JobId.Value, cancellationToken).ConfigureAwait(false);
            if (draft is null)
            {
                draft = new IndexGenerationEntity
                {
                    Id = Guid.NewGuid(), EmbeddingJobId = work.Job.JobId.Value, CorpusEpoch = state.CorpusEpoch,
                    CorpusVersion = state.CorpusVersion, ModelFingerprint = profile.ModelFingerprint, Dimensions = profile.Dimensions,
                    MetadataChecksum = EmbedDraftDefaults.MetadataChecksum, CreatedAtUtc = timeProvider.GetUtcNow()
                };
                context.IndexGenerations.Add(draft);
                state.EmptyCatalogueValidatedAtUtc = null;
                await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }
            ValidateDraft(draft, work, profile, state.CorpusEpoch);
            await ValidateStoredVectorsAsync(context, draft, work, cancellationToken).ConfigureAwait(false);
            var chunks = await Chunks(context, work).Where(chunk => !context.Vectors.Any(vector =>
                    vector.IndexGenerationId == draft.Id && vector.TextChunkId == chunk.Id))
                .OrderBy(chunk => chunk.Ordinal).ThenBy(chunk => chunk.Id).Take(MaximumBatchSize)
                .Select(chunk => new CanonicalTextChunk(chunk.Id, chunk.Ordinal, chunk.StartOffset, chunk.Length,
                    chunk.Content, chunk.ContentHash, chunk.PassagePolicyFingerprint, chunk.ContextHeader))
                .ToArrayAsync(cancellationToken).ConfigureAwait(false);
            var checksum = chunks.Length == 0
                ? await ReadCompletedChecksumAsync(context, draft, work, cancellationToken).ConfigureAwait(false) : null;
            return new EmbeddingWorkBatch(draft.Id, state.CorpusEpoch, profile, chunks, checksum);
        }, cancellationToken);

    public async ValueTask CommitAsync(StageWorkItem work, EmbeddingWorkBatch batch,
        IReadOnlyList<EmbeddingResult> results, CancellationToken cancellationToken)
    {
        ValidateProfile(batch.Profile);
        if (batch.Chunks.Count is < 1 or > MaximumBatchSize || results.Count != batch.Chunks.Count ||
            batch.Chunks.Select(chunk => chunk.Id).Distinct().Count() != batch.Chunks.Count || batch.CompletedChecksum is not null)
            throw new InvalidOperationException("embedding-checkpoint-batch-invalid");
        var values = results.Select(result => ValidateResult(result, batch.Profile)).ToArray();
        await ExecuteAsync(async context =>
        {
            await ValidateClaimAsync(context, work, timeProvider.GetUtcNow(), cancellationToken).ConfigureAwait(false);
            var epoch = (await SqlPublishedPassageSelection.ReadStampAsync(context, cancellationToken).ConfigureAwait(false)).CorpusEpoch;
            var draft = await context.IndexGenerations.SingleAsync(value => value.Id == batch.GenerationId, cancellationToken).ConfigureAwait(false);
            if (batch.CorpusEpoch != epoch) throw new InvalidOperationException("embedding-checkpoint-epoch-changed");
            ValidateDraft(draft, work, batch.Profile, epoch);
            await CommitVectorsAsync(context, work.Job.PipelineRecordId.Value, work.Job.SourceRevision, draft, batch, values, timeProvider.GetUtcNow(), cancellationToken).ConfigureAwait(false);
            return true;
        }, cancellationToken).ConfigureAwait(false);
    }

    internal static async Task CommitVectorsAsync(FluxKnowledgeDbContext context, Guid recordId, long sourceRevision,
        IndexGenerationEntity draft, EmbeddingWorkBatch batch, byte[][] values, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var ids = batch.Chunks.Select(chunk => chunk.Id).ToArray();
        var chunks = await Chunks(context, recordId, sourceRevision).Where(chunk => ids.Contains(chunk.Id)).ToArrayAsync(cancellationToken).ConfigureAwait(false);
        if (chunks.Length != ids.Length) throw new InvalidOperationException("embedding-checkpoint-input-changed");
        var existing = await context.Vectors.Where(vector => vector.IndexGenerationId == draft.Id && ids.Contains(vector.TextChunkId))
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        for (var row = 0; row < batch.Chunks.Count; row++)
        {
            var input = batch.Chunks[row];
            var current = chunks.Single(chunk => chunk.Id == input.Id);
            var currentInput = ToCanonical(current);
            if (currentInput != input || current.SearchInputHash != input.SearchInputHash)
                throw new InvalidOperationException("embedding-checkpoint-input-changed");
            var checksum = Convert.ToHexStringLower(SHA256.HashData(values[row]));
            var previous = existing.SingleOrDefault(vector => vector.TextChunkId == input.Id);
            if (previous is not null)
            {
                if (previous.IsDeleted || previous.SourceRevision != sourceRevision ||
                    previous.SearchInputHash != input.SearchInputHash || previous.TextChunkContentHash != input.ContentHash ||
                    previous.ModelFingerprint != batch.Profile.ModelFingerprint || previous.Dimensions != batch.Profile.Dimensions ||
                    previous.PayloadChecksum != checksum || !previous.Values.AsSpan().SequenceEqual(values[row]))
                    throw new InvalidOperationException("embedding-checkpoint-replay-conflict");
                continue;
            }
            context.Vectors.Add(new VectorEntity
            {
                TextChunkId = input.Id, SourceRevision = sourceRevision, IndexGenerationId = draft.Id,
                ModelFingerprint = batch.Profile.ModelFingerprint, Dimensions = batch.Profile.Dimensions,
                Values = values[row], TextChunkContentHash = input.ContentHash, SearchInputHash = input.SearchInputHash,
                PayloadChecksum = checksum, CreatedAtUtc = now
            });
            draft.VectorCount++;
        }
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<T> ExecuteAsync<T>(Func<FluxKnowledgeDbContext, Task<T>> operation, CancellationToken cancellationToken)
    {
        await using var strategyContext = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await strategyContext.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false);
            await SqlPublishedPassageSelection.AcquireFenceAsync(context, cancellationToken).ConfigureAwait(false);
            var result = await operation(context).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return result;
        }).ConfigureAwait(false);
    }

    internal static async Task ValidateClaimAsync(FluxKnowledgeDbContext context, StageWorkItem work, DateTimeOffset now, CancellationToken ct)
    {
        await SqlCorpusRebuildStore.ValidateMaintenanceJobAsync(context, work.Job.JobId.Value, ct).ConfigureAwait(false);
        if (work.Job.Stage != PipelineStage.Embed || work.Job.Operation != PipelineOperations.Embed ||
            work.DispatchMessage.Stage != PipelineStage.Embed || work.DispatchMessage.Operation != PipelineOperations.Embed ||
            work.DispatchMessage.PipelineRecordId != work.Job.PipelineRecordId || work.DispatchMessage.SourceRevision != work.Job.SourceRevision)
            throw new InvalidOperationException("embedding-checkpoint-claim-invalid");
        var dispatch = await context.OutboxMessages.FromSqlInterpolated($"SELECT * FROM [OutboxMessages] WITH (UPDLOCK,HOLDLOCK) WHERE [Id] = {work.DispatchMessage.DispatchMessageId.Value}")
            .AsNoTracking().SingleOrDefaultAsync(ct).ConfigureAwait(false);
        var job = await context.Jobs.FromSqlInterpolated($"SELECT * FROM [Jobs] WITH (UPDLOCK,HOLDLOCK) WHERE [Id] = {work.Job.JobId.Value}")
            .AsNoTracking().SingleOrDefaultAsync(ct).ConfigureAwait(false);
        if (dispatch is null || job is null || dispatch.DispatchedAtUtc is not null ||
            dispatch.JobId != job.Id ||
            dispatch.PipelineRecordId != work.Job.PipelineRecordId.Value || dispatch.SourceRevision != work.Job.SourceRevision ||
            dispatch.Stage != (int)PipelineStage.Embed || dispatch.Operation != PipelineOperations.Embed ||
            dispatch.IdempotencyKey != work.DispatchMessage.IdempotencyKey || dispatch.DispatchGeneration != work.DispatchMessage.DispatchGeneration ||
            dispatch.LeaseOwner != work.DispatchMessage.LeaseOwner || dispatch.LeaseGeneration != work.DispatchMessage.LeaseGeneration ||
            dispatch.LeaseExpiresAtUtc is null || dispatch.LeaseExpiresAtUtc <= now ||
            job.PipelineRecordId != work.Job.PipelineRecordId.Value || job.SourceRevision != work.Job.SourceRevision ||
            job.Stage != (int)PipelineStage.Embed || job.Operation != PipelineOperations.Embed ||
            job.PublicState != (int)PublicJobState.WorkerProcessing || job.LeaseOwner != work.Job.LeaseOwner ||
            job.LeaseGeneration != work.Job.LeaseGeneration || job.LeaseExpiresAtUtc is null || job.LeaseExpiresAtUtc <= now ||
            !await context.PipelineRecords.AnyAsync(record => record.Id == job.PipelineRecordId && record.Revision == job.SourceRevision &&
                !record.IsDeleted && record.CurrentStage == (int)PipelineStage.Embed, ct).ConfigureAwait(false))
            throw new InvalidOperationException("embedding-checkpoint-lease-lost");
        await SqlRepositoryWorkRecovery.ValidateAsync(context, job, now, ct).ConfigureAwait(false);
        var unavailable = await (from record in context.PipelineRecords
            join retained in context.SourceRevisions on record.SourceRevisionId equals retained.Id
            join root in context.SourceRootConfigurations on retained.SourceRootId equals root.Id
            where record.Id == job.PipelineRecordId && (retained.SuppressedAtUtc != null || root.State == (int)SourceRootState.Deleting)
            select record.Id).AnyAsync(ct).ConfigureAwait(false);
        if (unavailable) throw new InvalidOperationException("embedding-checkpoint-source-unavailable");
    }

    internal static void ValidateDraft(IndexGenerationEntity draft, StageWorkItem work, EmbeddingProfile profile, Guid epoch)
        => ValidateDraft(draft, work.Job.JobId.Value, profile, epoch);

    internal static void ValidateDraft(IndexGenerationEntity draft, Guid jobId, EmbeddingProfile profile, Guid epoch)
    {
        if (draft.EmbeddingJobId != jobId || draft.CorpusEpoch != epoch || draft.CorpusVersion is null ||
            draft.ModelFingerprint != profile.ModelFingerprint || draft.Dimensions != profile.Dimensions ||
            draft.IndexPath.Length != 0 || draft.ValidatedAtUtc is not null || draft.RetiredAtUtc is not null ||
            draft.MetadataChecksum != EmbedDraftDefaults.MetadataChecksum)
            throw new InvalidOperationException("embedding-checkpoint-draft-conflict");
    }

    internal static IQueryable<TextChunkEntity> Chunks(FluxKnowledgeDbContext context, StageWorkItem work) =>
        Chunks(context, work.Job.PipelineRecordId.Value, work.Job.SourceRevision);

    internal static IQueryable<TextChunkEntity> Chunks(FluxKnowledgeDbContext context, Guid recordId, long sourceRevision) =>
        context.TextChunks.Where(chunk => chunk.SourceRevision == sourceRevision &&
            chunk.Artifact.PipelineRecordId == recordId && chunk.Artifact.SourceRevision == sourceRevision &&
            chunk.Artifact.Stage == (int)PipelineStage.CanonicalIndex);

    private static CanonicalTextChunk ToCanonical(TextChunkEntity chunk) => new(chunk.Id, chunk.Ordinal, chunk.StartOffset, chunk.Length,
        chunk.Content, chunk.ContentHash, chunk.PassagePolicyFingerprint, chunk.ContextHeader);

    private static void ValidateProfile(EmbeddingProfile profile)
    {
        if (string.IsNullOrWhiteSpace(profile.ModelFingerprint) || profile.ModelFingerprint.Length > 256 || profile.Dimensions is < 1 or > 4096)
            throw new InvalidOperationException("embedding-checkpoint-profile-invalid");
    }

    internal static byte[] ValidateResult(EmbeddingResult result, EmbeddingProfile profile)
    {
        if (result.ModelFingerprint != profile.ModelFingerprint || result.Values.Count != profile.Dimensions ||
            result.Values.Any(value => !float.IsFinite(value)) || Math.Abs(result.Values.Sum(value => (double)value * value) - 1) > 0.001)
            throw new InvalidOperationException("embedding-checkpoint-result-invalid");
        var bytes = new byte[profile.Dimensions * sizeof(float)];
        Buffer.BlockCopy(result.Values.ToArray(), 0, bytes, 0, bytes.Length);
        return bytes;
    }

    internal static Task ValidateStoredVectorsAsync(FluxKnowledgeDbContext context, IndexGenerationEntity draft, StageWorkItem work, CancellationToken ct)
        => ValidateStoredVectorsAsync(context, draft, work.Job.PipelineRecordId.Value, work.Job.SourceRevision, ct);

    internal static async Task ValidateStoredVectorsAsync(FluxKnowledgeDbContext context, IndexGenerationEntity draft,
        Guid recordId, long sourceRevision, CancellationToken ct)
    {
        var chunks = Chunks(context, recordId, sourceRevision);
        if (await context.Vectors.LongCountAsync(vector => vector.IndexGenerationId == draft.Id, ct).ConfigureAwait(false) != draft.VectorCount ||
            await context.Vectors.AnyAsync(vector => vector.IndexGenerationId == draft.Id && (vector.IsDeleted ||
                vector.SourceRevision != sourceRevision || vector.ModelFingerprint != draft.ModelFingerprint || vector.Dimensions != draft.Dimensions ||
                vector.SearchInputHash == null || !chunks.Any(chunk => chunk.Id == vector.TextChunkId &&
                    chunk.ContentHash == vector.TextChunkContentHash && chunk.SearchInputHash == vector.SearchInputHash)), ct).ConfigureAwait(false))
            throw new InvalidOperationException("embedding-checkpoint-stored-input-invalid");
    }

    internal static async Task<string> ReadCompletedChecksumAsync(FluxKnowledgeDbContext context, IndexGenerationEntity draft, StageWorkItem work, CancellationToken ct)
        => await ReadCheckpointChecksumAsync(context, draft, work, requireComplete: true, ct).ConfigureAwait(false);

    internal static Task<string> ReadCheckpointChecksumAsync(FluxKnowledgeDbContext context, IndexGenerationEntity draft, StageWorkItem work, bool requireComplete, CancellationToken ct)
        => ReadCheckpointChecksumAsync(context, draft, work.Job.PipelineRecordId.Value, work.Job.SourceRevision, requireComplete, ct);

    internal static async Task<string> ReadCheckpointChecksumAsync(FluxKnowledgeDbContext context, IndexGenerationEntity draft,
        Guid recordId, long sourceRevision, bool requireComplete, CancellationToken ct)
    {
        await ValidateStoredVectorsAsync(context, draft, recordId, sourceRevision, ct).ConfigureAwait(false);
        if (requireComplete && await Chunks(context, recordId, sourceRevision).LongCountAsync(ct).ConfigureAwait(false) != draft.VectorCount)
            throw new InvalidOperationException("embedding-checkpoint-incomplete");
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        // EF's retrying SQL strategy can buffer an entire streaming result. Keyset
        // pages bound payload memory even when connection retries are enabled.
        var lastOrdinal = -1;
        var lastChunkId = 0L;
        while (true)
        {
            var page = await context.Vectors.AsNoTracking().Include(vector => vector.TextChunk)
                .Where(vector => vector.IndexGenerationId == draft.Id && (vector.TextChunk.Ordinal > lastOrdinal ||
                    (vector.TextChunk.Ordinal == lastOrdinal && vector.TextChunkId > lastChunkId)))
                .OrderBy(vector => vector.TextChunk.Ordinal).ThenBy(vector => vector.TextChunkId).Take(128)
                .ToArrayAsync(ct).ConfigureAwait(false);
            if (page.Length == 0) break;
            foreach (var vector in page)
            {
                ValidateStoredPayload(vector, vector.TextChunk, draft);
                hash.AppendData(vector.Values);
            }
            lastOrdinal = page[^1].TextChunk.Ordinal;
            lastChunkId = page[^1].TextChunkId;
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    internal static void ValidateStoredPayload(VectorEntity vector, TextChunkEntity chunk, IndexGenerationEntity draft)
    {
        if (vector.ModelFingerprint != draft.ModelFingerprint || vector.Dimensions != draft.Dimensions ||
            vector.Values.Length != draft.Dimensions * sizeof(float) || Convert.ToHexStringLower(SHA256.HashData(vector.Values)) != vector.PayloadChecksum ||
            ToCanonical(chunk).SearchInputHash != vector.SearchInputHash || chunk.SearchInputHash != vector.SearchInputHash)
            throw new InvalidOperationException("embedding-checkpoint-stored-payload-invalid");
        var values = new float[draft.Dimensions];
        Buffer.BlockCopy(vector.Values, 0, values, 0, vector.Values.Length);
        _ = ValidateResult(new(values, draft.ModelFingerprint), new(draft.ModelFingerprint, draft.Dimensions));
    }
}
