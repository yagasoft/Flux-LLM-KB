using FluxKnowledge.Application.Ports;

namespace FluxKnowledge.Infrastructure.Inference.Search;

/// <summary>Scores independent passages in parallel, then restores the caller's exact ordering.</summary>
public sealed class ParallelCpuPassageReranker(IReadOnlyList<IPassageReranker> sessions, string fingerprint) : IPassageReranker
{
    public async ValueTask<RerankResult> RerankAsync(string query, IReadOnlyList<RerankPassage> passages,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (sessions.Count is < 1 or > 4 || string.IsNullOrWhiteSpace(query) || query.Length > 16384 ||
            passages.Count is < 1 or > 50 || passages.Any(p => p is null || p.PassageId <= 0 ||
                string.IsNullOrWhiteSpace(p.SearchText) || p.SearchText.Length > 16384) ||
            passages.Select(p => p.PassageId).Distinct().Count() != passages.Count)
            throw new BgeInferenceException("bge-reranker-input-invalid");
        var shardCount = Math.Min(sessions.Count, passages.Count);
        var groups = Enumerable.Range(0, shardCount)
            .Select(shard => passages.Where((_, index) => index % shardCount == shard).ToArray()).ToArray();
        var tasks = Enumerable.Range(0, shardCount).Select(shard => Task.Run(async () =>
            await sessions[shard].RerankAsync(query, groups[shard], cancellationToken).ConfigureAwait(false),
            CancellationToken.None)).ToArray();
        // When one shard fails or cancellation is requested, retain all sessions until every Run returns.
        var results = await Task.WhenAll(tasks).ConfigureAwait(false);
        if (results.Where((result, shard) => result.ModelFingerprint != fingerprint ||
            result.Scores.Count != groups[shard].Length ||
            !result.Scores.Select(score => score.PassageId).SequenceEqual(groups[shard].Select(p => p.PassageId)) ||
            result.Scores.Any(score => !float.IsFinite(score.Logit))).Any())
            throw new BgeInferenceException("bge-reranker-output-invalid");
        var byId = results.SelectMany(result => result.Scores).ToDictionary(score => score.PassageId);
        if (byId.Count != passages.Count) throw new BgeInferenceException("bge-reranker-output-invalid");
        return new(passages.Select(passage => byId[passage.PassageId]).ToArray(), fingerprint);
    }
}
