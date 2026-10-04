using FluxKnowledge.Application.Ports;

namespace FluxKnowledge.Application.Search;

public sealed record RankedPassage(EligiblePassageCandidate Passage, double FusionScore,
    int? LexicalRank, int? SemanticRank, bool ExactBodyMatch, float? RerankerScore = null);

/// <summary>Fusion and trained scores always refer to the same complete canonical passage.</summary>
public static class PassageRanking
{
    public const int ChannelBudget = 100;
    public const int ShortlistBudget = 50;

    public static IReadOnlyList<RankedPassage> Fuse(string query,
        IReadOnlyList<EligiblePassageCandidate> lexical, IReadOnlyList<EligiblePassageCandidate> semantic)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        if (lexical.Count > ChannelBudget || semantic.Count > ChannelBudget)
            throw new InvalidOperationException("passage-channel-budget-exceeded");
        var identities = new Dictionary<long, EligiblePassageCandidate>();
        foreach (var passage in lexical.Concat(semantic))
        {
            if (passage.ChunkId <= 0 || identities.TryGetValue(passage.ChunkId, out var prior) && !SamePassage(prior, passage))
                throw new InvalidOperationException("passage-candidate-identity-conflict");
            identities.TryAdd(passage.ChunkId, passage);
        }
        var fused = ReciprocalRankFusion.Combine(
            lexical.Select((passage, i) => new RankedCandidate(passage.ChunkId, i + 1)).ToArray(),
            semantic.Select((passage, i) => new RankedCandidate(passage.ChunkId, i + 1)).ToArray());
        return fused.Select(value => new RankedPassage(identities[value.VectorId], value.Score,
                value.LexicalRank, value.SemanticRank,
                identities[value.VectorId].Content.Contains(query, StringComparison.Ordinal)))
            .OrderByDescending(value => value.ExactBodyMatch).ThenByDescending(value => value.FusionScore)
            .ThenBy(value => value.Passage.ChunkId).Take(ShortlistBudget).ToArray();
    }

    public static IReadOnlyList<RankedPassage> ApplyScores(IReadOnlyList<RankedPassage> shortlist,
        RerankResult result, string expectedFingerprint)
    {
        if (shortlist.Count > ShortlistBudget || result.ModelFingerprint != expectedFingerprint ||
            result.Scores.Count != shortlist.Count || result.Scores.Any(value => !float.IsFinite(value.Logit)) ||
            !result.Scores.Select(value => value.PassageId).SequenceEqual(shortlist.Select(value => value.Passage.ChunkId)))
            throw new InvalidOperationException("passage-reranker-result-invalid");
        return shortlist.Select((value, i) => value with { RerankerScore = result.Scores[i].Logit })
            .OrderByDescending(value => value.ExactBodyMatch).ThenByDescending(value => value.RerankerScore)
            .ThenByDescending(value => value.FusionScore).ThenBy(value => value.Passage.ChunkId).ToArray();
    }

    public static (Guid? PublicationOwner, Guid UnrootedRecord) DocumentIdentity(EligiblePassageCandidate passage) =>
        passage.OwnerSourceRevisionId.HasValue ? (passage.OwnerSourceRevisionId, Guid.Empty) : (null, passage.PipelineRecordId);

    public static bool IsNearDuplicate(EligiblePassageCandidate passage, IReadOnlyList<EligiblePassageCandidate> accepted) =>
        accepted.Any(prior => DocumentIdentity(prior) == DocumentIdentity(passage) && prior.ArtifactId == passage.ArtifactId &&
            Math.Max(0L, Math.Min((long)prior.StartOffset + prior.Length, (long)passage.StartOffset + passage.Length) -
                Math.Max(prior.StartOffset, passage.StartOffset)) * 5 >= (long)Math.Min(prior.Length, passage.Length) * 4);

    internal static bool SamePassage(EligiblePassageCandidate first, EligiblePassageCandidate second) =>
        first with { FullTextRank = 0, DisclosureProof = null } ==
            second with { FullTextRank = 0, DisclosureProof = null };
}
