using Cloud.Unum.USearch;
using FluxKnowledge.Application.Pipeline;
using FluxKnowledge.Infrastructure.Usearch;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Indexing;

public sealed partial class UsearchFilteredReadTests
{
    [Fact]
    public void Cancellation_between_selected_native_reads_returns_before_the_view_can_be_reused()
    {
        using var index = Open();
        using var cancellation = new CancellationTokenSource();
        IEnumerable<long> Selected()
        {
            yield return 1001;
            cancellation.Cancel();
            yield return 1002;
        }
        Assert.ThrowsAny<OperationCanceledException>(() => index.Search([1, 0], 2,
            new ScriptedKeys(() => true, 2, Selected), cancellation.Token));
        Assert.Equal(1, Assert.Single(index.Search([1, 0], 1, null, CancellationToken.None)).VectorId);
    }

    [Fact]
    public void A_missing_native_vector_in_a_small_canonical_selection_refuses_instead_of_silently_omitting_it()
    {
        using var index = Open();
        Assert.Throws<IndexGenerationValidationException>(() =>
            index.Search([1, 0], 2, new HashSet<long> { 1001, 9999 }, CancellationToken.None));
        Assert.Equal(1, Assert.Single(index.Search([1, 0], 1, null, CancellationToken.None)).VectorId);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void An_invalid_key_in_a_small_selected_set_refuses(long invalid)
    {
        using var index = Open();
        Assert.Throws<IndexGenerationValidationException>(() =>
            index.Search([1, 0], 2, new HashSet<long> { 1001, invalid }, CancellationToken.None));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_small_set_cannot_exceed_its_declared_bound_or_repeat_native_reads(bool duplicate)
    {
        using var index = Open();
        IEnumerable<long> Selected() => duplicate ? [1001, 1001] : [1001, 1002];
        Assert.Throws<IndexGenerationValidationException>(() => index.Search([1, 0], 2,
            new ScriptedKeys(() => true, duplicate ? 2 : 1, Selected), CancellationToken.None));
    }

    [Fact]
    public void A_selected_zero_vector_refuses_cosine_scoring()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "index.usearch");
        using (var native = new USearchIndex(MetricKind.Cos, ScalarKind.Float32, 2))
        {
            native.Add(1, new float[] { 0, 0 }); native.Save(path);
        }
        using var index = new UsearchReadIndex(path, 2, 1);
        Assert.Throws<IndexGenerationValidationException>(() =>
            index.Search([1, 0], 1, new HashSet<long> { 1 }, CancellationToken.None));
    }

    [Fact]
    public void A_small_selected_set_returns_exact_cosine_order_with_deterministic_ties()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "index.usearch");
        using (var native = new USearchIndex(MetricKind.Cos, ScalarKind.Float32, 2))
        {
            // Reverse insertion and non-unit lengths must not decide ties or turn cosine into dot product.
            native.Add(30, new float[] { 0, 7 });
            native.Add(20, new float[] { 3, 3 });
            native.Add(12, new float[] { 7, 0 });
            native.Add(11, new float[] { 2, 0 });
            native.Add(1, new float[] { 100, 0 }); // closer out-of-scope vector
            native.Save(path);
        }
        using var index = new UsearchReadIndex(path, 2, 5, searchExpansion: 1);
        var result = index.Search([3, 0], 4, new HashSet<long> { 30, 20, 12, 11 }, CancellationToken.None);
        Assert.Equal(new long[] { 11, 12, 20, 30 }, result.Select(x => x.VectorId));
        Assert.Equal(0, result[0].Distance, 5);
        Assert.Equal(0, result[1].Distance, 5);
        Assert.Equal(1 - 1 / MathF.Sqrt(2), result[2].Distance, 5);
        Assert.Equal(1, result[3].Distance, 5);
    }

    [Theory]
    [InlineData(512)]
    [InlineData(1024)]
    public void A_sparse_selection_matches_an_independent_full_selected_vector_oracle(int selectedCount)
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "index.usearch");
        var selected = new HashSet<long>();
        var vectors = new Dictionary<long, float[]>();
        var random = new Random(9341);
        using (var native = new USearchIndex(MetricKind.Cos, ScalarKind.Float32, 3))
        {
            for (ulong i = 1; i <= 2048; i++) native.Add(i, new float[] { 1, .0001f * i, 0 });
            for (var i = 0; i < selectedCount; i++)
            {
                var id = 3000L + i;
                var vector = new[] { (float)random.NextDouble(), (float)random.NextDouble() + 1, (float)random.NextDouble() };
                selected.Add(id); vectors.Add(id, vector); native.Add((ulong)id, vector);
            }
            native.Save(path);
        }
        using var index = new UsearchReadIndex(path, 3, 2048 + selectedCount, searchExpansion: 1);
        var query = new float[] { 2, .1f, -.2f };
        static double Distance(float[] a, float[] b)
        {
            double dot = 0, left = 0, right = 0;
            for (var i = 0; i < a.Length; i++) { dot += (double)a[i] * b[i]; left += (double)a[i] * a[i]; right += (double)b[i] * b[i]; }
            return 1 - dot / Math.Sqrt(left * right);
        }
        var oracle = vectors.OrderBy(p => Distance(query, p.Value)).ThenBy(p => p.Key).Take(100).ToArray();
        var actual = index.Search(query, 100, selected, CancellationToken.None);
        Assert.Equal(oracle.Select(p => p.Key), actual.Select(p => p.VectorId));
        for (var i = 0; i < actual.Count; i++) Assert.InRange(Math.Abs(actual[i].Distance - Distance(query, oracle[i].Value)), 0, .00001);
    }
}
