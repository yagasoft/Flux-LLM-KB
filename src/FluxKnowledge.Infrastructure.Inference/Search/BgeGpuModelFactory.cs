using FluxKnowledge.Application.Gpu;
using FluxKnowledge.Application.Models;
using FluxKnowledge.Application.Ports;

namespace FluxKnowledge.Infrastructure.Inference.Search;

public interface IBgeGpuModelFactory
{
    BgeRerankerPrecision Precision => BgeRerankerPrecision.Fp32;
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
public sealed class BgeGpuModelFactory : IBgeGpuModelFactory
{
    private readonly BgeGpuModelStores _stores;
    private readonly IGpuProcessModelResidency? _residency;
    public BgeGpuModelFactory(BgeGpuModelStores stores) : this(stores, BgeRerankerPrecision.Fp32) { }
    public BgeGpuModelFactory(BgeGpuModelStores stores, BgeRerankerPrecision precision,
        IGpuProcessModelResidency? residency = null)
    {
        ArgumentNullException.ThrowIfNull(stores);
        _ = BgeOfflineModels.GetRerankerFingerprint(precision);
        _stores = stores; Precision = precision; _residency = residency;
    }
    public BgeRerankerPrecision Precision { get; }

    public async ValueTask<BgeGpuModelLease<IBatchedEmbeddingProvider>> OpenEmbeddingAsync(GpuOwnedWorkContext ownership)
    {
        ownership.RequireActive(BgeOfflineModels.GetGpuRuntimeKey(Precision), BgeOfflineModels.GpuSettingsFingerprint);
        var tokenizer = await BgeOfflineModels.CreateTokenizerAsync(_stores.Embedding, _stores.TokenizerRuntime, false, ownership.CancellationToken).ConfigureAwait(false);
        try
        {
            BgeEmbeddingProvider model;
            if (_residency?.Supports(ownership) == true)
            {
                var phase = BgeGpuModelPhase.Embedding;
                var key = BgeOfflineModels.GetGpuResidentModelKey(phase, Precision);
                var resident = await _residency.GetModelAsync(ownership, key,
                    () => BgeOfflineModels.OpenGpuResidentAsync(_stores.Embedding, phase, ownership, Precision)).ConfigureAwait(false);
                model = new(tokenizer, resident.Borrow(ownership, key), BgeOfflineModels.EmbeddingFingerprint);
            }
            else
                model = await BgeOfflineModels.OpenGpuEmbeddingAsync(_stores.Embedding, tokenizer, ownership, Precision).ConfigureAwait(false);
            return new(model, () => { try { model.Dispose(); } finally { tokenizer.Dispose(); } });
        }
        catch { tokenizer.Dispose(); throw; }
    }

    public async ValueTask<BgeGpuModelLease<IPassageReranker>> OpenRerankerAsync(GpuOwnedWorkContext ownership)
    {
        ownership.RequireActive(BgeOfflineModels.GetGpuRuntimeKey(Precision), BgeOfflineModels.GpuSettingsFingerprint);
        var tokenizer = await BgeOfflineModels.CreateTokenizerAsync(_stores.RerankerTokenizer, _stores.TokenizerRuntime, true, ownership.CancellationToken).ConfigureAwait(false);
        try
        {
            BgeReranker model;
            if (_residency?.Supports(ownership) == true)
            {
                var phase = BgeGpuModelPhase.Reranker;
                var key = BgeOfflineModels.GetGpuResidentModelKey(phase, Precision);
                var resident = await _residency.GetModelAsync(ownership, key,
                    () => BgeOfflineModels.OpenGpuResidentAsync(_stores.Reranker, phase, ownership, Precision)).ConfigureAwait(false);
                model = new(tokenizer, resident.Borrow(ownership, key), BgeOfflineModels.GetRerankerFingerprint(Precision));
            }
            else
                model = await BgeOfflineModels.OpenGpuRerankerAsync(_stores.Reranker, tokenizer, ownership, Precision).ConfigureAwait(false);
            return new(model, () => { try { model.Dispose(); } finally { tokenizer.Dispose(); } });
        }
        catch { tokenizer.Dispose(); throw; }
    }
}
