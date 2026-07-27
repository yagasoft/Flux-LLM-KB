namespace FluxKnowledge.Application.Search;

public sealed record RankedCandidate(long VectorId, int Rank);

public sealed record FusedCandidate(
    long VectorId,
    double Score,
    int? LexicalRank,
    int? SemanticRank);

public static class ReciprocalRankFusion
{
    private const double RankConstant = 60D;

    public static IReadOnlyList<FusedCandidate> Combine(
        IReadOnlyList<RankedCandidate> lexical,
        IReadOnlyList<RankedCandidate> semantic)
    {
        ArgumentNullException.ThrowIfNull(lexical);
        ArgumentNullException.ThrowIfNull(semantic);

        var candidates = new Dictionary<long, (int? LexicalRank, int? SemanticRank)>();
        Add(lexical, isLexical: true, candidates);
        Add(semantic, isLexical: false, candidates);

        return candidates
            .Select(candidate => new FusedCandidate(
                candidate.Key,
                Score(candidate.Value.LexicalRank, candidate.Value.SemanticRank),
                candidate.Value.LexicalRank,
                candidate.Value.SemanticRank))
            .OrderByDescending(static candidate => candidate.Score)
            .ThenBy(static candidate => candidate.VectorId)
            .ToArray();
    }

    private static void Add(
        IReadOnlyList<RankedCandidate> candidates,
        bool isLexical,
        Dictionary<long, (int? LexicalRank, int? SemanticRank)> combined)
    {
        foreach (var candidate in candidates)
        {
            if (candidate.Rank <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(candidates), "Candidate ranks must be positive.");
            }

            if (!combined.TryGetValue(candidate.VectorId, out var existing))
            {
                existing = default;
            }

            combined[candidate.VectorId] = isLexical
                ? (existing.LexicalRank ?? candidate.Rank, existing.SemanticRank)
                : (existing.LexicalRank, existing.SemanticRank ?? candidate.Rank);
        }
    }

    private static double Score(int? lexicalRank, int? semanticRank)
    {
        var bestRank = Math.Min(lexicalRank ?? int.MaxValue, semanticRank ?? int.MaxValue);
        var contributions = (lexicalRank is null ? 0 : 1) + (semanticRank is null ? 0 : 1);
        return contributions / (RankConstant + bestRank);
    }
}
