namespace FluxKnowledge.Application.Ports;

/// <summary>Bounded cohort ranking; individual model calls remain limited to fifty inputs.</summary>
public interface IExpandedPassageRanker
{
    string RerankerFingerprint { get; }
    IExpandedPassageRankTicket Reserve(DateTimeOffset apiArrivalUtc, string traceId, string spanId, CancellationToken cancellationToken);
}

public interface IExpandedPassageRankTicket : IAsyncDisposable
{
    Guid ClientId { get; }
    Guid CohortId { get; }
    ValueTask<RerankResult> SubmitAsync(string query, IReadOnlyList<RerankPassage> passages);
}
