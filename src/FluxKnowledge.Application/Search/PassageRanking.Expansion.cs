using FluxKnowledge.Application.Ports;

namespace FluxKnowledge.Application.Search;

public static partial class PassageRanking
{
    public const int ExpandedPoolBudget = IAnnCandidatePool.MaximumResults;
    public const int AdditionalInputBudget = 50;
    public const int ExpandedDisclosureBudget = ChannelBudget * 2 + AdditionalInputBudget;

    public static void ValidateExpandedPopulation(IReadOnlyList<EligiblePassageCandidate> lexical,
        IReadOnlyList<EligiblePassageCandidate> baselineDense, IReadOnlyList<EligiblePassageCandidate> pool)
    {
        if (lexical.Count > ChannelBudget || baselineDense.Count > ChannelBudget || pool.Count > ExpandedPoolBudget)
            throw new InvalidOperationException("passage-expanded-channel-budget-exceeded");
        var identities = new Dictionary<long, EligiblePassageCandidate>();
        foreach (var p in lexical.Concat(baselineDense).Concat(pool))
        {
            if (p.ChunkId <= 0 || identities.TryGetValue(p.ChunkId, out var prior) && !SamePassage(prior, p))
                throw new InvalidOperationException("passage-candidate-identity-conflict");
            identities.TryAdd(p.ChunkId, p);
        }
    }

    public static ExpansionPlan PlanExpansion(string query, IReadOnlyList<EligiblePassageCandidate> lexical,
        IReadOnlyList<EligiblePassageCandidate> baselineDense, IReadOnlyList<EligiblePassageCandidate> pool)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        ValidateExpandedPopulation(lexical, baselineDense, pool);
        return new(query, lexical.ToArray(), baselineDense.ToArray(), pool.ToArray());
    }

    public sealed class ExpansionPlan
    {
        private readonly string _query;
        private readonly EligiblePassageCandidate[] _lexical, _dense, _pool;
        private readonly IReadOnlySet<long> _planned;
        public IReadOnlyList<EligiblePassageCandidate> DisclosureCandidates { get; }

        internal ExpansionPlan(string query, EligiblePassageCandidate[] lexical,
            EligiblePassageCandidate[] dense, EligiblePassageCandidate[] pool)
        {
            _query = query; _lexical = lexical; _dense = dense; _pool = pool;
            // Every baseline binding and its citation alternatives keep their capacity.
            var selected = lexical.Concat(dense).DistinctBy(p => p.ChunkId).ToList();
            var ids = selected.Select(p => p.ChunkId).ToHashSet();
            var baselineInputs = Fuse(query, lexical, dense).Select(p => InputKey(p.Passage)).ToHashSet();
            var extraGroups = pool.GroupBy(InputKey).Where(group => !baselineInputs.Contains(group.Key)).ToArray();
            var plannedExtras = new List<EligiblePassageCandidate[]>();
            foreach (var group in extraGroups)
            {
                if (plannedExtras.Count == AdditionalInputBudget) break;
                var copies = group.OrderBy(p => p.ChunkId).ToArray();
                if (!copies.Any(p => ids.Contains(p.ChunkId)))
                {
                    if (selected.Count == ExpandedDisclosureBudget) continue;
                    selected.Add(copies[0]); ids.Add(copies[0].ChunkId);
                }
                plannedExtras.Add(copies);
            }
            // Remaining capacity can preserve further independent bindings, never a vote.
            foreach (var copies in plannedExtras)
            foreach (var p in copies)
            {
                if (selected.Count == ExpandedDisclosureBudget) break;
                if (ids.Add(p.ChunkId)) selected.Add(p);
            }
            _planned = ids;
            DisclosureCandidates = Array.AsReadOnly(selected.ToArray());
        }

        public IReadOnlyList<RankedPassage> Complete(IReadOnlySet<long> approvedBindings)
        {
            var approved = approvedBindings.Where(_planned.Contains).ToHashSet();
            var lexical = _lexical.Where(p => approved.Contains(p.ChunkId)).ToArray();
            var dense = _dense.Where(p => approved.Contains(p.ChunkId)).ToArray();
            var pool = _pool.Where(p => approved.Contains(p.ChunkId)).ToArray();
            var result = Fuse(_query, lexical, dense).ToList();
            var baselineCount = result.Count;
            var included = result.Select(p => InputKey(p.Passage)).ToHashSet();
            var allCopies = lexical.Concat(dense).Concat(pool).DistinctBy(p => p.ChunkId)
                .GroupBy(InputKey).ToDictionary(group => group.Key, group => group.OrderBy(p => p.ChunkId).ToArray());
            var lexicalRanks = lexical.Select(InputKey).Distinct().Select((key, i) => (key, Rank: i + 1)).ToDictionary(p => p.key, p => p.Rank);
            var denseRanks = dense.Select(InputKey).Distinct().Select((key, i) => (key, Rank: i + 1)).ToDictionary(p => p.key, p => p.Rank);
            var poolRank = 0;
            foreach (var group in pool.GroupBy(InputKey))
            {
                poolRank++;
                if (!included.Add(group.Key)) continue;
                if (result.Count == baselineCount + AdditionalInputBudget) break;
                var copies = allCopies[group.Key]; var representative = copies[0];
                var lexRank = lexicalRanks.TryGetValue(group.Key, out var l) ? (int?)l : null;
                var semanticRank = denseRanks.GetValueOrDefault(group.Key, poolRank);
                var fused = ReciprocalRankFusion.Combine(lexRank.HasValue ? [new(representative.ChunkId, lexRank.Value)] : [],
                    [new(representative.ChunkId, semanticRank)]).Single();
                result.Add(new(representative, fused.Score, lexRank, semanticRank,
                    representative.Content.Contains(_query, StringComparison.Ordinal)) { AlternativePassages = copies.Skip(1).ToArray() });
            }
            return result;
        }
    }
}
