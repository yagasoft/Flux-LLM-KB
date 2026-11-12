using FluxKnowledge.Application.Ports;
using FluxKnowledge.Infrastructure.Inference.Search;

namespace FluxKnowledge.Web.Configuration;

/// <summary>Explicit staging ingestion uses the same verified profile, with the pool's bounded admission.</summary>
internal sealed class StagingCpuEmbeddingProvider(ResidentCpuPassageInference pool) : IBatchedEmbeddingProvider
{
    public EmbeddingProfile Profile => pool.EmbeddingProfile;
    public int MaximumBatchSize => 4;
    public ValueTask<EmbeddingResult> CreateEmbeddingAsync(string text, CancellationToken cancellationToken) =>
        pool.ExecuteAsync((embedding, _, ct) => embedding.CreateEmbeddingAsync(text, ct), cancellationToken);

    public ValueTask<IReadOnlyList<EmbeddingResult>> CreateEmbeddingsAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken) =>
        pool.ExecuteAsync((embedding, _, ct) => ((IBatchedEmbeddingProvider)embedding).CreateEmbeddingsAsync(texts, ct), cancellationToken);
}
