using System.Security.Cryptography;
using FluxKnowledge.Application.Pipeline;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Workers;
using FluxKnowledge.Domain.Pipeline;
using FluxKnowledge.Application.Gpu;

namespace FluxKnowledge.Application.Indexing;

public sealed class EmbedStageWorker(
    IIndexGenerationStore indexStore,
    IEmbeddingProvider embeddings,
    StageTransitionService transitions,
    TimeProvider timeProvider,
    IEmbeddingCheckpointStore? checkpoints = null,
    IEmbeddingGpuHandoff? gpuHandoff = null) : IStageWorker
{
    public string Operation => PipelineOperations.Embed;

    public async ValueTask ExecuteAsync(StageWorkItem workItem, CancellationToken cancellationToken)
    {
        if (checkpoints is not null && gpuHandoff is not null)
        {
            await ExecuteCheckpointedAsync(workItem, gpuHandoff.Profile, null, checkpoints, cancellationToken);
            return;
        }
        if (checkpoints is not null && embeddings is IBatchedEmbeddingProvider batched)
        {
            if (batched.MaximumBatchSize < 4) throw new InvalidOperationException("embedding-provider-batch-limit-invalid");
            await ExecuteCheckpointedAsync(workItem, batched.Profile, batched, checkpoints, cancellationToken);
            return;
        }
        var chunks = await indexStore.ReadChunksAsync(
            workItem.Job.PipelineRecordId, workItem.Job.SourceRevision, cancellationToken);
        var generationId = Guid.NewGuid();
        var vectors = new List<CanonicalVector>(chunks.Count);
        foreach (var chunk in chunks)
        {
            var embedding = await embeddings.CreateEmbeddingAsync(chunk.SearchText, cancellationToken);
            var values = new byte[embedding.Values.Count * sizeof(float)];
            Buffer.BlockCopy(embedding.Values.ToArray(), 0, values, 0, values.Length);
            vectors.Add(new CanonicalVector(0, chunk.Id, embedding.ModelFingerprint, embedding.Values.Count,
                values, chunk.ContentHash, Convert.ToHexStringLower(SHA256.HashData(values)),
                workItem.Job.SourceRevision));
        }

        await transitions.TransitionAsync(new StageTransitionRequest(
            workItem.DispatchMessage,
            workItem.Job,
            new StageArtifact(Guid.NewGuid(), PipelineStage.Embed,
                Convert.ToHexStringLower(SHA256.HashData(vectors.SelectMany(vector => vector.Values).ToArray())),
                EmbedDraftDefaults.ArtifactContentType, generationId.ToString("D"), timeProvider.GetUtcNow()),
            PipelineStage.Publish,
            PipelineOperations.Publish,
            nameof(EmbedStageWorker),
            new IndexingStageOutput(
                IndexGenerationId: generationId,
                ModelFingerprint: DeterministicFingerprint(vectors),
                Vectors: vectors)), cancellationToken);
    }

    private async ValueTask ExecuteCheckpointedAsync(StageWorkItem work, EmbeddingProfile profile, IBatchedEmbeddingProvider? provider,
        IEmbeddingCheckpointStore store, CancellationToken cancellationToken)
    {
        var batch = await store.ReadNextAsync(work, profile, cancellationToken);
        if (batch.Chunks.Count > 0)
        {
            if (gpuHandoff is not null)
            {
                await gpuHandoff.QueueAsync(work, batch, cancellationToken);
                return;
            }
            var results = await provider!.CreateEmbeddingsAsync(batch.Chunks.Select(chunk => chunk.SearchText).ToArray(), cancellationToken);
            await store.CommitAsync(work, batch, results, cancellationToken);
            batch = await store.ReadNextAsync(work, profile, cancellationToken);
            if (batch.Chunks.Count > 0)
            {
                // Each delivery owns only one bounded inference batch. Requeue through the
                // existing fenced transition rather than extending an expiring job lease.
                await transitions.RetryAsync(new(work.DispatchMessage, work.Job, timeProvider.GetUtcNow(),
                    "embedding-batch-persisted", nameof(EmbedStageWorker)), cancellationToken);
                return;
            }
        }
        if (batch.CompletedChecksum is null) throw new InvalidOperationException("embedding-checkpoint-incomplete");
        await transitions.TransitionAsync(new(work.DispatchMessage, work.Job,
            new StageArtifact(Guid.NewGuid(), PipelineStage.Embed, batch.CompletedChecksum,
                EmbedDraftDefaults.ArtifactContentType, batch.GenerationId.ToString("D"), timeProvider.GetUtcNow()),
            PipelineStage.Publish, PipelineOperations.Publish, nameof(EmbedStageWorker),
            new IndexingStageOutput(IndexGenerationId: batch.GenerationId, ModelFingerprint: batch.Profile.ModelFingerprint,
                UsePersistedEmbeddingDraft: true)), cancellationToken);
    }

    private static string DeterministicFingerprint(IReadOnlyList<CanonicalVector> vectors) =>
        vectors.Count == 0 ? EmbedDraftDefaults.ModelFingerprint : vectors[0].ModelFingerprint;
}
