using FluxKnowledge.Application.Gpu;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Search;
using System.Diagnostics;

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
        var traceId = Activity.Current?.TraceId.ToString() ?? string.Empty;
        var spanId = Activity.Current?.SpanId.ToString() ?? string.Empty;
        return executor.ExecuteWithOwnershipAsync(owner =>
        {
            HybridSearchDiagnostics.Log.NativeSearch(traceId, spanId, owner.Handle.BatchId.ToString("N"));
            return models.ExecuteSearchAsync(owner, work);
        }, cancellationToken, declineWhenGpuBusy);
    }
}
