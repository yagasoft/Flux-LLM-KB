using FluxKnowledge.Application.Ports;

namespace FluxKnowledge.Infrastructure.Inference.Search;

public sealed class BgeReranker : IPassageReranker, IDisposable
{
    private readonly IBgeTokenizer _tokenizer;
    private readonly IBgeTensorRunner _runner;
    private readonly string _fingerprint;
    private bool _disposed;

    // The runner owns its model lease. The caller retains ownership of the tokenizer.
    internal BgeReranker(IBgeTokenizer tokenizer, IBgeTensorRunner runner, string fingerprint)
    {
        _tokenizer = tokenizer;
        _runner = runner;
        _fingerprint = fingerprint;
    }

    public ValueTask<RerankResult> RerankAsync(string query, IReadOnlyList<RerankPassage> passages, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(passages);
        if (string.IsNullOrWhiteSpace(query) || query.Length > 16384 || passages.Count is < 1 or > 50 ||
            passages.Any(static p => p is null || p.PassageId <= 0 || string.IsNullOrWhiteSpace(p.SearchText) || p.SearchText.Length > 16384) ||
            passages.Select(static p => p.PassageId).Distinct().Count() != passages.Count)
            throw new BgeInferenceException("bge-reranker-input-invalid");
        var queryIds = _tokenizer.EncodeUntruncated(query);
        // Preflight every complete pair before any native execution. A later invalid pair
        // must not cause partial scoring or leave the caller to guess which scores exist.
        var batches = passages.Select(p => BgeInputBatch.Pair(queryIds, _tokenizer.EncodeUntruncated(p.SearchText)))
            .Chunk(BgeInputBatch.MaximumBatch).Select(BgeInputBatch.Create).ToArray();
        var scores = new List<RerankScore>(passages.Count);
        foreach (var batch in batches)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var output = _runner.Run(batch, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            var logits = BgeOutputValidation.Logits(output.Values, output.Dimensions, batch.Count);
            foreach (var logit in logits) scores.Add(new(passages[scores.Count].PassageId, logit));
        }
        return ValueTask.FromResult(new RerankResult(scores.AsReadOnly(), _fingerprint));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _runner.Dispose();
    }
}
