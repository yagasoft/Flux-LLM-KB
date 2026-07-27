using FluxKnowledge.Application.Search;
using Xunit;

namespace FluxKnowledge.Domain.Tests.Search;

public sealed class ReciprocalRankFusionTests
{
    [Fact]
    public void Reciprocal_rank_fusion_uses_one_over_sixty_plus_rank_and_breaks_ties_by_vector_id()
    {
        var fused = ReciprocalRankFusion.Combine(
            [new RankedCandidate(8, 1), new RankedCandidate(4, 2)],
            [new RankedCandidate(4, 1), new RankedCandidate(8, 2)]);

        Assert.Equal(new long[] { 4, 8 }, fused.Select(static item => item.VectorId));
        Assert.Equal(2D / 61D, fused[0].Score, 10);
    }
}
