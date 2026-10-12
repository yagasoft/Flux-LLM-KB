using FluxKnowledge.Application.Gpu;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Search;

namespace FluxKnowledge.Infrastructure.Inference.Search;

public sealed class BgeScheduledPassageInference(GpuInteractiveExecutor executor, BgeGpuInferenceSession models) : IConditionalGpuPassageInference
{
    public EmbeddingProfile EmbeddingProfile { get; } = new(BgeOfflineModels.EmbeddingFingerprint, 1024);
    public string RerankerFingerprint => BgeOfflineModels.RerankerFingerprint;
    public ValueTask<T> ExecuteAsync<T>(Func<IEmbeddingProvider, IPassageReranker, CancellationToken, ValueTask<T>> work,
        CancellationToken cancellationToken) => ExecuteCoreAsync(work, cancellationToken, false);

    public ValueTask<T> ExecuteWhenIdleAsync<T>(Func<IEmbeddingProvider, IPassageReranker, CancellationToken, ValueTask<T>> work,
        CancellationToken cancellationToken) => ExecuteCoreAsync(work, cancellationToken, true);

    private ValueTask<T> ExecuteCoreAsync<T>(Func<IEmbeddingProvider, IPassageReranker, CancellationToken, ValueTask<T>> work,
        CancellationToken cancellationToken, bool declineWhenGpuBusy)
    {
        var (traceId, spanId) = HybridSearchDiagnostics.CurrentCorrelation;
        return executor.ExecuteWithOwnershipAsync(owner => HybridSearchDiagnostics.WithRequestCorrelationAsync(traceId, spanId, async () =>
        {
            // Immutable IDs outlive the request Activity without changing scheduler context.
            HybridSearchDiagnostics.Log.NativeSearch(traceId, spanId, owner.Handle.BatchId.ToString("N"));
            return await models.ExecuteSearchAsync(owner, work).ConfigureAwait(false);
        }), cancellationToken, declineWhenGpuBusy);
    }
}
