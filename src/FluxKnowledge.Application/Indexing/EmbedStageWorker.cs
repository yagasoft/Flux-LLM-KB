using System.Security.Cryptography;
using FluxKnowledge.Application.Pipeline;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Workers;
using FluxKnowledge.Domain.Pipeline;

namespace FluxKnowledge.Application.Indexing;

public sealed class EmbedStageWorker(
    IIndexGenerationStore indexStore,
    IEmbeddingProvider embeddings,
    StageTransitionService transitions,
    TimeProvider timeProvider) : IStageWorker
{
    public string Operation => PipelineOperations.Embed;

    public async ValueTask ExecuteAsync(StageWorkItem workItem, CancellationToken cancellationToken)
    {
        var chunks = await indexStore.ReadChunksAsync(
            workItem.Job.PipelineRecordId, workItem.Job.SourceRevision, cancellationToken);
        var generationId = Guid.NewGuid();
        var vectors = new List<CanonicalVector>(chunks.Count);
        foreach (var chunk in chunks)
        {
            var embedding = await embeddings.CreateEmbeddingAsync(chunk.Content, cancellationToken);
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

    private static string DeterministicFingerprint(IReadOnlyList<CanonicalVector> vectors) =>
        vectors.Count == 0 ? EmbedDraftDefaults.ModelFingerprint : vectors[0].ModelFingerprint;
}
