using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Indexing;

namespace FluxKnowledge.Web.Configuration;

/// <summary>Model-free transport/lifecycle acceptance only; never registered in production.</summary>
internal sealed class StagingPassageInference(IEmbeddingProvider embedding) : IScheduledPassageInference, IPassageReranker, IBatchedEmbeddingProvider
{
    public EmbeddingProfile EmbeddingProfile { get; } = new(
        FluxKnowledge.Infrastructure.Inference.DeterministicTokenHashEmbeddingProvider.Fingerprint, 256);
    public string RerankerFingerprint => "synthetic-staging-order-v1";
    public EmbeddingProfile Profile => EmbeddingProfile;
    public int MaximumBatchSize => 4;
    public ValueTask<EmbeddingResult> CreateEmbeddingAsync(string text, CancellationToken cancellationToken) =>
        embedding.CreateEmbeddingAsync(text, cancellationToken);
    public async ValueTask<IReadOnlyList<EmbeddingResult>> CreateEmbeddingsAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken)
    {
        if (texts.Count > MaximumBatchSize) throw new ArgumentException("staging-embedding-batch-too-large");
        var result = new List<EmbeddingResult>(texts.Count);
        foreach (var text in texts) result.Add(await embedding.CreateEmbeddingAsync(text, cancellationToken));
        return result;
    }
    internal static PassageBuilder CreatePassageBuilder() => new(new Words());
    private sealed class Words : IPassageTokenizer
    {
        public string Fingerprint => "synthetic-staging-words-v1";
        public int CountTokens(string text) => text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
    }
    public ValueTask<T> ExecuteAsync<T>(Func<IEmbeddingProvider, IPassageReranker, CancellationToken, ValueTask<T>> work,
        CancellationToken cancellationToken) => work(embedding, this, cancellationToken);
    public ValueTask<RerankResult> RerankAsync(string query, IReadOnlyList<RerankPassage> passages, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(new RerankResult(passages.Select((value, index) => new RerankScore(value.PassageId, -index)).ToArray(), RerankerFingerprint));
    }
}
