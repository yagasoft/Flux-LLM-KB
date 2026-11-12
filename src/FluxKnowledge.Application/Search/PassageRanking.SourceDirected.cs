using FluxKnowledge.Application.Ports;

namespace FluxKnowledge.Application.Search;

public static partial class PassageRanking
{
    public static IReadOnlyList<EligiblePassageCandidate> SelectForDisclosure(string query,
        IReadOnlyList<EligiblePassageCandidate> lexical, IReadOnlyList<EligiblePassageCandidate> semantic,
        IReadOnlyList<EligiblePassageCandidate> sourceDirected)
    {
        var groups = FuseAll(query, lexical, semantic, sourceDirected).Take(ChannelBudget * 2).ToArray();
        // Spend capacity on distinct inputs first, then independently validated citation alternatives.
        var selected = groups.Select(group => group.Passage).ToList();
        foreach (var group in groups)
        foreach (var alternative in group.AlternativePassages)
        {
            if (selected.Count == ChannelBudget * 2) return selected;
            selected.Add(alternative);
        }
        return selected;
    }

    public static IReadOnlyList<RankedPassage> Fuse(string query,
        IReadOnlyList<EligiblePassageCandidate> lexical, IReadOnlyList<EligiblePassageCandidate> semantic,
        IReadOnlyList<EligiblePassageCandidate> sourceDirected) => sourceDirected.Count == 0
            ? Fuse(query, lexical, semantic) : FuseAll(query, lexical, semantic, sourceDirected).Take(ShortlistBudget).ToArray();

    private static IReadOnlyList<RankedPassage> FuseAll(string query,
        IReadOnlyList<EligiblePassageCandidate> lexical, IReadOnlyList<EligiblePassageCandidate> semantic,
        IReadOnlyList<EligiblePassageCandidate> sourceDirected)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        var identities = ReadCandidatePopulation(lexical, semantic, sourceDirected);
        var groups = identities.Values.GroupBy(InputKey).Select(group => group.OrderBy(p => p.ChunkId).ToArray()).ToArray();
        var representatives = groups.SelectMany(group => group.Select(p => (p.ChunkId, Representative: group[0].ChunkId)))
            .ToDictionary(pair => pair.ChunkId, pair => pair.Representative);
        var copies = groups.ToDictionary(group => group[0].ChunkId);
        RankedCandidate[] Ranks(IReadOnlyList<EligiblePassageCandidate> channel) => channel
            .Select(p => representatives[p.ChunkId]).Distinct().Select((id, rank) => new RankedCandidate(id, rank + 1)).ToArray();
        var baseline = ReciprocalRankFusion.Combine(Ranks(lexical), Ranks(semantic)).ToDictionary(p => p.VectorId);
        var directed = ReciprocalRankFusion.Combine(Ranks(sourceDirected), []).ToDictionary(p => p.VectorId);
        return copies.Select(pair =>
        {
            baseline.TryGetValue(pair.Key, out var original);
            directed.TryGetValue(pair.Key, out var hint);
            return new RankedPassage(pair.Value[0], (original?.Score ?? 0) + (hint?.Score ?? 0),
                original?.LexicalRank, original?.SemanticRank, pair.Value[0].Content.Contains(query, StringComparison.Ordinal))
            {
                SourceDirectedRank = hint?.LexicalRank,
                AlternativePassages = pair.Value.Skip(1).ToArray()
            };
        }).OrderByDescending(p => p.ExactBodyMatch).ThenByDescending(p => p.FusionScore)
            .ThenBy(p => p.Passage.ChunkId).ToArray();
    }

    internal static void ValidateCandidatePopulation(IReadOnlyList<EligiblePassageCandidate> lexical,
        IReadOnlyList<EligiblePassageCandidate> semantic, IReadOnlyList<EligiblePassageCandidate> sourceDirected) =>
        _ = ReadCandidatePopulation(lexical, semantic, sourceDirected);

    private static Dictionary<long, EligiblePassageCandidate> ReadCandidatePopulation(
        IReadOnlyList<EligiblePassageCandidate> lexical, IReadOnlyList<EligiblePassageCandidate> semantic,
        IReadOnlyList<EligiblePassageCandidate> sourceDirected)
    {
        if (lexical.Count > ChannelBudget || semantic.Count > ChannelBudget || sourceDirected.Count > ChannelBudget)
            throw new InvalidOperationException("passage-channel-budget-exceeded");
        var identities = new Dictionary<long, EligiblePassageCandidate>();
        foreach (var passage in lexical.Concat(semantic).Concat(sourceDirected))
        {
            // Inspect the complete bounded population before deduplication or truncation.
            if (passage.ChunkId <= 0 || identities.TryGetValue(passage.ChunkId, out var prior) && !SamePassage(prior, passage))
                throw new InvalidOperationException("passage-candidate-identity-conflict");
            identities.TryAdd(passage.ChunkId, passage);
        }
        return identities;
    }
}
