using FluxKnowledge.Application.Search;
using Xunit;

namespace FluxKnowledge.Domain.Tests.Search;

public sealed partial class PassageRankingTests
{
    [Fact]
    public void Spare_disclosure_capacity_reaches_later_inputs_when_earlier_inputs_are_withheld()
    {
        var baseline = Enumerable.Range(1, 50).Select(i => Passage(i, "Baseline " + i)).ToArray();
        var pool = Enumerable.Range(1001, 200).Select(i => Passage(i, "Pool " + i)).ToArray();
        var plan = PassageRanking.PlanExpansion("question", baseline, [], pool);

        Assert.Equal(250, plan.DisclosureCandidates.Count);
        Assert.Equal(baseline.Concat(pool), plan.DisclosureCandidates);
        var approved = plan.DisclosureCandidates.Where(p => p.ChunkId < 1001 || p.ChunkId > 1050)
            .Select(p => p.ChunkId).ToHashSet();
        var ranked = plan.Complete(approved);

        Assert.Equal(100, ranked.Count);
        Assert.Equal(PassageRanking.Fuse("question", baseline, []), ranked.Take(50));
        Assert.Equal(Enumerable.Range(1051, 50).Select(i => (long)i), ranked.Skip(50).Select(p => p.Passage.ChunkId));
        Assert.DoesNotContain(ranked, p => p.Passage.ChunkId is >= 1001 and <= 1050);
    }

    [Fact]
    public void Spare_slots_can_disclose_an_independent_copy_after_its_baseline_binding_is_withheld()
    {
        var baseline = Passage(1, "Shared body");
        var copy = Passage(2, "Shared body");
        var extra = Passage(3, "Another body");
        var plan = PassageRanking.PlanExpansion("question", [baseline], [], [copy, extra]);

        Assert.Equal(new long[] { 1, 3, 2 }, plan.DisclosureCandidates.Select(p => p.ChunkId));
        var ranked = plan.Complete(new HashSet<long> { 2, 3 });

        Assert.Equal(new long[] { 2, 3 }, ranked.Select(p => p.Passage.ChunkId));
        Assert.All(ranked, p => Assert.Empty(p.AlternativePassages));
    }

    [Fact]
    public void Additional_disclosure_bindings_do_not_add_duplicate_model_votes()
    {
        var baseline = Passage(1, "Baseline");
        var pool = Enumerable.Range(2, 100).Select(i => Passage(i, "Identical extra input")).ToArray();
        var plan = PassageRanking.PlanExpansion("question", [baseline], [], pool);
        var ranked = plan.Complete(plan.DisclosureCandidates.Select(p => p.ChunkId).ToHashSet());

        Assert.Equal(101, plan.DisclosureCandidates.Count);
        Assert.Equal(2, ranked.Count);
        Assert.Equal(99, ranked[1].AlternativePassages.Count);
    }
}
