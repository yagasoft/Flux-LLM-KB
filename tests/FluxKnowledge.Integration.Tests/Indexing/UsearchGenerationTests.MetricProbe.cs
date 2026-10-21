using System.Security.Cryptography;
using Cloud.Unum.USearch;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Infrastructure.Usearch;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Indexing;

public sealed partial class UsearchGenerationTests
{
    [Fact]
    public void Metric_probe_does_not_allocate_results_for_the_whole_corpus()
    {
        var sample = MetricProbeVector(1, [1, 2, 3, 4]);
        using (var warm = new USearchIndex(MetricKind.Cos, ScalarKind.Float32, 4, 0, 0, 0, false))
        {
            warm.Add(1, new float[] { 1, 2, 3, 4 });
            UsearchGenerationValidator.VerifyCosineMetric(warm, [sample], 1);
        }
        using var index = new USearchIndex(MetricKind.Cos, ScalarKind.Float32, 4, 0, 0, 0, false);
        for (ulong id = 1; id <= 4096; id++) index.Add(id, new float[] { 1, 2, 3, 4 });
        var before = GC.GetAllocatedBytesForCurrentThread();
        UsearchGenerationValidator.VerifyCosineMetric(index, [sample], 4096);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.InRange(allocated, 0, 64 * 1024);
    }

    [Fact]
    public void Metric_probe_accepts_tied_results_without_requiring_the_seed_id_in_the_nearest_results()
    {
        using var index = new USearchIndex(MetricKind.Cos, ScalarKind.Float32, 4, 0, 0, 0, false);
        for (ulong id = 1; id <= 128; id++) index.Add(id, new float[] { 1, 2, 3, 4 });
        index.Search(new float[] { 0.5F, 1, 1.5F, 2 }, 8, out var nearest, out _);
        var seed = Enumerable.Range(1, 128).Select(id => (ulong)id).First(id => !nearest.Contains(id));
        UsearchGenerationValidator.VerifyCosineMetric(index, [MetricProbeVector((long)seed, [1, 2, 3, 4])], 8);
    }

    [Fact]
    public void Metric_probe_accepts_a_valid_negative_vector_when_the_offset_query_would_be_zero()
    {
        using var index = new USearchIndex(MetricKind.Cos, ScalarKind.Float32, 4, 0, 0, 0, false);
        index.Add(1, new float[] { -0.5F, -0.5F, -0.5F, -0.5F });
        UsearchGenerationValidator.VerifyCosineMetric(index, [MetricProbeVector(1, [-0.5F, -0.5F, -0.5F, -0.5F])]);
    }

    private static CanonicalVector MetricProbeVector(long id, float[] values)
    {
        var bytes = new byte[values.Length * sizeof(float)];
        Buffer.BlockCopy(values, 0, bytes, 0, bytes.Length);
        return new(id, id, "synthetic-metric-profile", values.Length, bytes, new string('a', 64),
            Convert.ToHexStringLower(SHA256.HashData(bytes)), 1);
    }
}
