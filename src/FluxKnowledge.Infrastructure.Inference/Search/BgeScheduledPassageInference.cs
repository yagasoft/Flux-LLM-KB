using FluxKnowledge.Application.Gpu;
using FluxKnowledge.Application.Ports;

namespace FluxKnowledge.Infrastructure.Inference.Search;

public sealed class BgeScheduledPassageInference(GpuInteractiveExecutor executor, BgeGpuInferenceSession models) : IScheduledPassageInference
{
    public EmbeddingProfile EmbeddingProfile { get; } = new(BgeOfflineModels.EmbeddingFingerprint, 1024);
    public string RerankerFingerprint => BgeOfflineModels.RerankerFingerprint;
    public ValueTask<T> ExecuteAsync<T>(Func<IEmbeddingProvider, IPassageReranker, CancellationToken, ValueTask<T>> work,
        CancellationToken cancellationToken) =>
        executor.ExecuteWithOwnershipAsync(owner => models.ExecuteSearchAsync(owner, work), cancellationToken);
}
