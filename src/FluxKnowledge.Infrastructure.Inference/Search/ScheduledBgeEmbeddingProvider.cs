using FluxKnowledge.Application.Gpu;
using FluxKnowledge.Application.Ports;

namespace FluxKnowledge.Infrastructure.Inference.Search;

/// <summary>Compatibility consumers also use acknowledged scheduler ownership; no direct GPU fallback.</summary>
public sealed class ScheduledBgeEmbeddingProvider(GpuInteractiveExecutor executor, BgeGpuInferenceSession models) : IEmbeddingProvider
{
    public ValueTask<EmbeddingResult> CreateEmbeddingAsync(string text, CancellationToken cancellationToken) =>
        executor.ExecuteWithOwnershipAsync(owner => models.ExecuteSearchAsync(owner,
            (embedding, _, ct) => embedding.CreateEmbeddingAsync(text, ct)), cancellationToken);
}
