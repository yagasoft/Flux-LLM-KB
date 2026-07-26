namespace FluxKnowledge.Application.Ports;

public sealed record EmbeddingResult(
    IReadOnlyList<float> Values,
    string ModelFingerprint);

public interface IEmbeddingProvider
{
    ValueTask<EmbeddingResult> CreateEmbeddingAsync(string text, CancellationToken cancellationToken);
}
