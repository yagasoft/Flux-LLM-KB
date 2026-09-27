namespace FluxKnowledge.Application.Ports;

public sealed record EmbeddingResult(
    IReadOnlyList<float> Values,
    string ModelFingerprint);

public interface IEmbeddingProvider
{
    ValueTask<EmbeddingResult> CreateEmbeddingAsync(string text, CancellationToken cancellationToken);
}

public sealed record EmbeddingProfile(string ModelFingerprint, int Dimensions);

public interface IBatchedEmbeddingProvider : IEmbeddingProvider
{
    EmbeddingProfile Profile { get; }
    int MaximumBatchSize { get; }
    ValueTask<IReadOnlyList<EmbeddingResult>> CreateEmbeddingsAsync(
        IReadOnlyList<string> texts, CancellationToken cancellationToken);
}
