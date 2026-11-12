using Xunit;

namespace FluxKnowledge.Integration.Tests.Indexing;

public sealed partial class UsearchFilteredReadTests
{
    [Fact]
    public void Sparse_candidate_pool_is_exact_and_preserves_the_normal_hundred_result_limit()
    {
        using var index = Open(expansion: 512, crowding: 1000);
        var keys = Enumerable.Range(1, 512).Select(i => (long)i).ToHashSet();
        var pool = index.SearchCandidatePool([1, 0], 400, keys, default);
        Assert.Equal(400, pool.Count);
        Assert.Equal(Enumerable.Range(1, 400).Select(i => (long)i), pool.Select(p => p.VectorId));
        Assert.Throws<ArgumentOutOfRangeException>(() => index.Search([1, 0], 101, keys, default));
        Assert.Equal(100, index.Search([1, 0], 100, keys, default).Count);
    }

    [Fact]
    public void Graph_candidate_pool_keeps_the_scope_filter_for_all_four_hundred_matches()
    {
        using var index = Open(expansion: 512, crowding: 2200);
        var keys = Enumerable.Range(2, 2199).Select(i => i is 1001 or 1002 ? (long)i + 10000 : i)
            .Concat(new long[] { 1001, 1002 }).ToHashSet();
        var pool = index.SearchCandidatePool([1, 0], 400, keys, default);
        Assert.Equal(400, pool.Count);
        Assert.Equal(400, pool.Select(p => p.VectorId).Distinct().Count());
        Assert.All(pool, p => { Assert.Contains(p.VectorId, keys); Assert.True(float.IsFinite(p.Distance)); });
        Assert.DoesNotContain(pool, p => p.VectorId == 1);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(401)]
    public void Candidate_pool_has_its_own_explicit_result_bound(int limit)
    {
        using var index = Open();
        Assert.Throws<ArgumentOutOfRangeException>(() => index.SearchCandidatePool([1, 0], limit, null, default));
    }

    [Fact]
    public void Candidate_pool_filter_cancellation_settles_before_the_view_can_be_reused()
    {
        using var index = Open(crowding: 2200);
        using var cancellation = new CancellationTokenSource();
        Assert.ThrowsAny<OperationCanceledException>(() => index.SearchCandidatePool([1, 0], 400,
            new ScriptedKeys(() => { cancellation.Cancel(); return true; }), cancellation.Token));
        Assert.Single(index.Search([1, 0], 1, null, default));
    }
}
