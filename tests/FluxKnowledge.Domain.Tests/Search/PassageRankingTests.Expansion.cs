using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Search;
using Xunit;

namespace FluxKnowledge.Domain.Tests.Search;

public sealed partial class PassageRankingTests
{
    [Fact]
    public void Expansion_preserves_every_baseline_binding_and_reserves_fifty_additions()
    {
        var lexical = Enumerable.Range(1, 80).Select(i => Passage(i, "Lexical " + i)).ToArray();
        var dense = Enumerable.Range(1001, 80).Select(i => Passage(i, "Dense " + i)).ToArray();
        var pool = Enumerable.Range(2001, 100).Select(i => Passage(i, "Pool " + i)).ToArray();
        var plan = PassageRanking.PlanExpansion("question", lexical, dense, pool);
        Assert.Equal(210, plan.DisclosureCandidates.Count);
        Assert.Equal(lexical.Concat(dense), plan.DisclosureCandidates.Take(160));
        Assert.Equal(50, plan.DisclosureCandidates.Count(p => p.ChunkId >= 2001));
        var ranked = plan.Complete(plan.DisclosureCandidates.Select(p => p.ChunkId).ToHashSet());
        Assert.Equal(100, ranked.Count);
        Assert.Equal(PassageRanking.Fuse("question", lexical, dense), ranked.Take(50));
    }

    [Fact]
    public void An_input_outside_baseline_fifty_can_be_added_from_already_reserved_bindings()
    {
        var lexical = Enumerable.Range(1, 100).Select(i => Passage(i, "Lexical " + i)).ToArray();
        var dense = Enumerable.Range(1001, 100).Select(i => Passage(i, "Dense " + i)).ToArray();
        var plan = PassageRanking.PlanExpansion("question", lexical, dense, [dense[^1], Passage(3000, "No spare slot")]);
        Assert.Equal(201, plan.DisclosureCandidates.Count);
        Assert.Contains(plan.DisclosureCandidates, p => p.ChunkId == 3000);
        var ranked = plan.Complete(plan.DisclosureCandidates.Select(p => p.ChunkId).ToHashSet());
        Assert.Equal(52, ranked.Count);
        Assert.Equal(new[] { dense[^1].ChunkId, 3000L }, ranked.Skip(50).Select(p => p.Passage.ChunkId));
    }

    [Fact]
    public void Two_hundred_baseline_bindings_cannot_starve_fifty_new_model_inputs()
    {
        var lexical = Enumerable.Range(1, 100).Select(i => Passage(i, "Lexical " + i)).ToArray();
        var dense = Enumerable.Range(1001, 100).Select(i => Passage(i, "Dense " + i)).ToArray();
        var pool = Enumerable.Range(2001, 100).Select(i => Passage(i, "New pool input " + i)).ToArray();
        var plan = PassageRanking.PlanExpansion("question", lexical, dense, pool);
        Assert.Equal(250, plan.DisclosureCandidates.Count);
        Assert.Equal(lexical.Concat(dense), plan.DisclosureCandidates.Take(200));
        Assert.Equal(pool.Take(50), plan.DisclosureCandidates.Skip(200));
        var ranked = plan.Complete(plan.DisclosureCandidates.Select(p => p.ChunkId).ToHashSet());
        Assert.Equal(PassageRanking.Fuse("question", lexical, dense), ranked.Take(50));
        Assert.Equal(pool.Take(50).Select(p => p.ChunkId), ranked.Skip(50).Select(p => p.Passage.ChunkId));
        Assert.Equal(100, ranked.Count);
    }

    [Fact]
    public void Expansion_appends_at_most_fifty_new_verified_inputs_to_the_actual_baseline()
    {
        var baseline = Enumerable.Range(1, 50).Select(i => Passage(i, "Baseline " + i)).ToArray();
        var pool = Enumerable.Range(1001, 150).Select(i => Passage(i, "Pool " + i)).ToArray();
        var plan = PassageRanking.PlanExpansion("question", baseline, [], pool);
        var ranked = plan.Complete(plan.DisclosureCandidates.Select(p => p.ChunkId).ToHashSet());
        Assert.Equal(100, ranked.Count);
        Assert.Equal(PassageRanking.Fuse("question", baseline, []), ranked.Take(50));
        Assert.Equal(Enumerable.Range(1001, 50).Select(i => (long)i), ranked.Skip(50).Select(p => p.Passage.ChunkId));
    }

    [Fact]
    public void Fresh_approval_is_required_for_each_copy_and_cannot_be_invented_for_unplanned_bindings()
    {
        var baseline = Passage(1, "Shared"); var copy = Passage(2, "Shared"); var extra = Passage(3, "New");
        var plan = PassageRanking.PlanExpansion("question", [baseline], [], [copy, extra]);
        Assert.DoesNotContain(plan.DisclosureCandidates, p => p.ChunkId == 2);
        var ranked = plan.Complete(new HashSet<long> { 2, 3 });
        Assert.Equal(3, Assert.Single(ranked).Passage.ChunkId);
        Assert.DoesNotContain(ranked, p => p.Passage.ChunkId is 1 or 2);
    }

    [Fact]
    public void Baseline_is_recomputed_after_withdrawal_before_testing_whether_pool_inputs_are_new()
    {
        var lexical = Enumerable.Range(1, 60).Select(i => Passage(i, "Baseline " + i)).ToArray();
        var plan = PassageRanking.PlanExpansion("question", lexical, [], [lexical[^1], Passage(1001, "Extra")]);
        var approved = plan.DisclosureCandidates.Select(p => p.ChunkId).Where(id => id > 20).ToHashSet();
        var ranked = plan.Complete(approved);
        Assert.Equal(41, ranked.Count);
        Assert.Equal(1, ranked.Count(p => p.Passage.ChunkId == 60));
        Assert.Equal(1001, ranked[^1].Passage.ChunkId);
    }

    [Fact]
    public void Claimed_hashes_and_different_header_body_boundaries_do_not_merge_expansion_inputs()
    {
        var baseline = Passage(1, "Repeated");
        var unverified = Passage(2, "Repeated") with { SearchInputHash = new string('0', 64) };
        var otherBody = Passage(3, "header\nbody"); var otherHeader = Passage(4, "body", "header");
        var plan = PassageRanking.PlanExpansion("question", [baseline], [], [unverified, otherBody, otherHeader]);
        Assert.Equal(4, plan.Complete(plan.DisclosureCandidates.Select(p => p.ChunkId).ToHashSet()).Count);
    }

    [Fact]
    public void Raw_conflicts_beyond_the_disclosure_cutoff_refuse_before_truncation()
    {
        var lexical = Enumerable.Range(1, 100).Select(i => Passage(i, "Lexical " + i)).ToArray();
        var dense = Enumerable.Range(1001, 100).Select(i => Passage(i, "Dense " + i)).ToArray();
        var pool = Enumerable.Range(2001, 399).Select(i => Passage(i, "Pool " + i))
            .Append(lexical[^1] with { Content = "Contradictory raw identity" }).ToArray();
        Assert.Throws<InvalidOperationException>(() => PassageRanking.ValidateExpandedPopulation(lexical, dense, pool));
        Assert.Throws<InvalidOperationException>(() => PassageRanking.PlanExpansion("question", lexical, dense, pool));
    }

    [Theory]
    [InlineData(101, 0, 0)]
    [InlineData(0, 101, 0)]
    [InlineData(0, 0, 401)]
    public void Expansion_has_explicit_independent_raw_channel_limits(int lexicalCount, int denseCount, int poolCount)
    {
        EligiblePassageCandidate[] Rows(int start, int count) => Enumerable.Range(start, count).Select(i => Passage(i, "Input " + i)).ToArray();
        Assert.Throws<InvalidOperationException>(() => PassageRanking.PlanExpansion("question", Rows(1, lexicalCount), Rows(1001, denseCount), Rows(2001, poolCount)));
    }

    [Fact]
    public void Normal_trained_ranking_keeps_its_fifty_input_guard()
    {
        var rows = Enumerable.Range(1, 51).Select(i => new RankedPassage(Passage(i, "Input " + i), 0, null, null, false)).ToArray();
        var scores = new RerankResult(rows.Select(p => new RerankScore(p.Passage.ChunkId, 1)).ToArray(), "ranker");
        Assert.Throws<InvalidOperationException>(() => PassageRanking.ApplyScores(rows, scores, "ranker"));
    }
}
