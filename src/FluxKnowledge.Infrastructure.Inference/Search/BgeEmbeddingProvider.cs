using FluxKnowledge.Application.Ports;

namespace FluxKnowledge.Infrastructure.Inference.Search;

public sealed class BgeEmbeddingProvider : IBatchedEmbeddingProvider, IDisposable
{
    private readonly IBgeTokenizer _tokenizer;
    private readonly IBgeTensorRunner _runner;
    private readonly string _fingerprint;
    private bool _disposed;

    internal BgeEmbeddingProvider(IBgeTokenizer tokenizer, IBgeTensorRunner runner, string fingerprint)
    {
        _tokenizer = tokenizer;
        _runner = runner;
        _fingerprint = fingerprint;
    }

    public EmbeddingProfile Profile => new(_fingerprint, 1024);
    public int MaximumBatchSize => BgeInputBatch.MaximumBatch;

    public async ValueTask<EmbeddingResult> CreateEmbeddingAsync(string text, CancellationToken cancellationToken)
        => (await CreateEmbeddingsAsync([text], cancellationToken))[0];

    public ValueTask<IReadOnlyList<EmbeddingResult>> CreateEmbeddingsAsync(
        IReadOnlyList<string> texts, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        if (texts.Count is < 1 or > BgeInputBatch.MaximumBatch) throw new BgeInferenceException("bge-batch-invalid");
        if (texts.Any(static text => string.IsNullOrWhiteSpace(text) || text.Length > 16384))
            throw new BgeInferenceException("bge-embedding-input-invalid");
        var batch = BgeInputBatch.Create(texts.Select(_tokenizer.EncodeUntruncated).ToArray());
        var output = _runner.Run(batch, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<EmbeddingResult> results = BgeOutputValidation.Embeddings(output.Values, output.Dimensions, batch.Count)
            .Select(values => new EmbeddingResult(values, _fingerprint)).ToArray();
        return ValueTask.FromResult(results);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _runner.Dispose();
    }
}
