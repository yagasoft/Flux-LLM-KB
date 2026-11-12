using System.Collections;
using Cloud.Unum.USearch;
using FluxKnowledge.Infrastructure.Usearch;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Indexing;

public sealed partial class UsearchFilteredReadTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "FluxKnowledgeFiltered_" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Default_cache_and_native_reader_use_the_qualified_search_width()
    {
        // Normal composition omits this argument; staging explicitly supplied 512
        // in the full-corpus qualification. Guard both public default entry points.
        foreach (var type in new[] { typeof(UsearchSegmentCache), typeof(UsearchReadIndex) })
        {
            var defaults = type.GetConstructors().SelectMany(c => c.GetParameters())
                .Where(p => p.Name == "searchExpansion").ToArray();
            Assert.NotEmpty(defaults);
            Assert.All(defaults, p => Assert.Equal(512, p.DefaultValue));
        }
        using var index = Open();
        Assert.Equal(1, Assert.Single(index.Search([1, 0], 1, null, default)).VectorId);
    }

    [Theory]
    [InlineData(256)]
    [InlineData(512)]
    [InlineData(1024)]
    [InlineData(2048)]
    public void Sparse_eligible_neighbours_survive_hundreds_of_closer_out_of_scope_vectors(int expansion)
    {
        using var index = Open(expansion);
        var matches = index.Search([1, 0], 2, new HashSet<long> { 1001, 1002 }, CancellationToken.None);
        Assert.Equal(new long[] { 1001, 1002 }, matches.Select(value => value.VectorId));
        Assert.All(matches, value => Assert.True(float.IsFinite(value.Distance)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(8193)]
    public void Invalid_search_width_is_rejected_before_allocating_a_native_index(int expansion) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new UsearchReadIndex(2, expansion));

    [Fact]
    public void Empty_eligibility_returns_no_neighbours_and_an_owned_view_cannot_be_used_after_disposal()
    {
        var index = Open();
        Assert.Empty(index.Search([1, 0], 2, new HashSet<long>(), CancellationToken.None));
        index.Dispose();
        Assert.Throws<ObjectDisposedException>(() => index.Search([1, 0], 2, null, CancellationToken.None));
        index.Dispose();
    }

    [Fact]
    public void A_filter_failure_is_contained_until_native_search_returns()
    {
        using var index = Open(crowding: 2200);
        var error = Assert.Throws<InvalidOperationException>(() =>
            index.Search([1, 0], 2, new ScriptedKeys(() => throw new InvalidOperationException("synthetic-filter-failure")), CancellationToken.None));
        Assert.Equal("synthetic-filter-failure", error.Message);
        Assert.Equal(1, Assert.Single(index.Search([1, 0], 1, null, CancellationToken.None)).VectorId);
    }

    [Fact]
    public void Cancellation_during_the_filter_does_not_dispose_the_view_before_native_return()
    {
        using var index = Open(crowding: 2200);
        using var cancellation = new CancellationTokenSource();
        Assert.ThrowsAny<OperationCanceledException>(() => index.Search([1, 0], 2,
            new ScriptedKeys(() => { cancellation.Cancel(); return true; }), cancellation.Token));
        Assert.Single(index.Search([1, 0], 1, null, CancellationToken.None));
    }

    private UsearchReadIndex Open(int expansion = UsearchReadIndex.DefaultSearchExpansion, int crowding = 200)
    {
        Directory.CreateDirectory(_root);
        var file = Path.Combine(_root, "index.usearch");
        using (var index = new USearchIndex(MetricKind.Cos, ScalarKind.Float32, 2))
        {
            for (ulong id = 1; id <= (ulong)crowding; id++)
            {
                var angle = id == 1 ? 0 : id * 0.001f;
                index.Add(id is 1001 or 1002 ? id + 10000 : id, new float[] { MathF.Cos(angle), MathF.Sin(angle) });
            }
            index.Add(1001, new float[] { 0.98f, 0.19899748f });
            index.Add(1002, new float[] { 0.96f, 0.28f });
            index.Save(file);
        }
        return new(file, 2, crowding + 2, expansion);
    }

    public void Dispose()
    {
        // This exact random test leaf contains only its own generated index.
        if (File.Exists(Path.Combine(_root, "index.usearch"))) File.Delete(Path.Combine(_root, "index.usearch"));
        if (Directory.Exists(_root)) Directory.Delete(_root);
    }

    private sealed class ScriptedKeys(Func<bool> contains, int count = 2202, Func<IEnumerable<long>>? enumerate = null) : IReadOnlySet<long>
    {
        public int Count => count;
        public bool Contains(long value) => contains();
        public IEnumerator<long> GetEnumerator() => enumerate?.Invoke().GetEnumerator() ?? throw new NotSupportedException();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        public bool IsProperSubsetOf(IEnumerable<long> other) => throw new NotSupportedException();
        public bool IsProperSupersetOf(IEnumerable<long> other) => throw new NotSupportedException();
        public bool IsSubsetOf(IEnumerable<long> other) => throw new NotSupportedException();
        public bool IsSupersetOf(IEnumerable<long> other) => throw new NotSupportedException();
        public bool Overlaps(IEnumerable<long> other) => throw new NotSupportedException();
        public bool SetEquals(IEnumerable<long> other) => throw new NotSupportedException();
    }
}
