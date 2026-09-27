using FluxKnowledge.Application.Indexing;

namespace FluxKnowledge.Infrastructure.Inference.Search;

/// <summary>CPU token counting opens only verified local tokenizer files, on first construction work.</summary>
public sealed class BgePassageTokenizer : IPassageTokenizer, IDisposable
{
    private readonly Lazy<IBgeTokenizer> _tokenizer;
    private readonly object _sync = new();
    private bool _disposed;
    public BgePassageTokenizer(BgeGpuModelStores stores) => _tokenizer = new(() =>
        BgeOfflineModels.CreateTokenizerAsync(stores.Embedding, stores.TokenizerRuntime, false, CancellationToken.None)
            .AsTask().GetAwaiter().GetResult(), LazyThreadSafetyMode.ExecutionAndPublication);
    public string Fingerprint => BgeOfflineModels.EmbeddingTokenizerFingerprint;
    public int CountTokens(string text)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _tokenizer.Value.CountTokens(text);
        }
    }
    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            if (_tokenizer.IsValueCreated) _tokenizer.Value.Dispose();
        }
    }
}
