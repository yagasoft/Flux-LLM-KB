using FluxKnowledge.Application.Gpu;
using FluxKnowledge.Application.Models;
using FluxKnowledge.Application.Ports;

namespace FluxKnowledge.Infrastructure.Inference.Search;

public interface IBgeGpuModelFactory
{
    ValueTask<BgeGpuModelLease<IBatchedEmbeddingProvider>> OpenEmbeddingAsync(GpuOwnedWorkContext ownership);
    ValueTask<BgeGpuModelLease<IPassageReranker>> OpenRerankerAsync(GpuOwnedWorkContext ownership);
}

public sealed class BgeGpuModelLease<T>(T model, Action release) : IDisposable where T : class
{
    private Action? _release = release;
    public T Model { get; } = model;
    public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
}

public sealed record BgeGpuModelStores(ILocalModelStore Embedding, ILocalModelStore Reranker,
    ILocalModelStore RerankerTokenizer, ILocalModelStore TokenizerRuntime) : IDisposable
{
    public void Dispose()
    {
        foreach (var store in new[] { Embedding, Reranker, RerankerTokenizer, TokenizerRuntime }
                     .Distinct(ReferenceEqualityComparer.Instance))
            (store as IDisposable)?.Dispose();
    }
}

/// <summary>Fixed verified local models; each lease owns its native session and tokenizer.</summary>
public sealed class BgeGpuModelFactory(BgeGpuModelStores stores) : IBgeGpuModelFactory
{
    public async ValueTask<BgeGpuModelLease<IBatchedEmbeddingProvider>> OpenEmbeddingAsync(GpuOwnedWorkContext ownership)
    {
        ownership.RequireActive(BgeOfflineModels.GpuRuntimeKey, BgeOfflineModels.GpuSettingsFingerprint);
        var tokenizer = await BgeOfflineModels.CreateTokenizerAsync(stores.Embedding, stores.TokenizerRuntime, false, ownership.CancellationToken).ConfigureAwait(false);
        try
        {
            var model = await BgeOfflineModels.OpenGpuEmbeddingAsync(stores.Embedding, tokenizer, ownership).ConfigureAwait(false);
            return new(model, () => { try { model.Dispose(); } finally { tokenizer.Dispose(); } });
        }
        catch { tokenizer.Dispose(); throw; }
    }

    public async ValueTask<BgeGpuModelLease<IPassageReranker>> OpenRerankerAsync(GpuOwnedWorkContext ownership)
    {
        ownership.RequireActive(BgeOfflineModels.GpuRuntimeKey, BgeOfflineModels.GpuSettingsFingerprint);
        var tokenizer = await BgeOfflineModels.CreateTokenizerAsync(stores.RerankerTokenizer, stores.TokenizerRuntime, true, ownership.CancellationToken).ConfigureAwait(false);
        try
        {
            var model = await BgeOfflineModels.OpenGpuRerankerAsync(stores.Reranker, tokenizer, ownership).ConfigureAwait(false);
            return new(model, () => { try { model.Dispose(); } finally { tokenizer.Dispose(); } });
        }
        catch { tokenizer.Dispose(); throw; }
    }
}
