namespace FluxKnowledge.Application.Ports;

public sealed record RerankPassage(long PassageId, string SearchText);
public sealed record RerankScore(long PassageId, float Logit);
public sealed record RerankResult(IReadOnlyList<RerankScore> Scores, string ModelFingerprint);

public interface IPassageReranker
{
    ValueTask<RerankResult> RerankAsync(string query, IReadOnlyList<RerankPassage> passages, CancellationToken cancellationToken);
}
