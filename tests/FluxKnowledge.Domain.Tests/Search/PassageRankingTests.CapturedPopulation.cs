using System.Text.Json;
using FluxKnowledge.Application.Search;
using FluxKnowledge.Testing;
using Xunit;

namespace FluxKnowledge.Domain.Tests.Search;

public sealed partial class PassageRankingTests
{
    [Fact]
    public void Captured_population_shape_reaches_the_answer_without_expanding_disclosure_or_model_limits()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepositoryFiles.Root(),
            "tests", "FluxKnowledge.Domain.Tests", "Search", "Fixtures", "disclosure-capacity-population.json")));
        var fixture = document.RootElement;
        var passages = fixture.GetProperty("groups").EnumerateArray().ToDictionary(
            row => row.GetProperty("id").GetInt64(), row => Passage(row.GetProperty("id").GetInt64(),
                (row.GetProperty("exactBodyMatch").GetBoolean() ? "question " : "") +
                "Synthetic input " + row.GetProperty("inputGroup").GetInt32()));
        long[] Ids(string name) => fixture.GetProperty(name).EnumerateArray().Select(value => value.GetInt64()).ToArray();
        var lexical = Ids("lexical").Select(id => passages[id]).ToArray();
        var dense = Ids("dense").Select(id => passages[id]).ToArray();
        var pool = Ids("pool").Select(id => passages[id]).ToArray();
        var target = fixture.GetProperty("target").GetInt64();

        Assert.DoesNotContain(target, Ids("oldPlanned"));
        Assert.DoesNotContain(target, Ids("oldInput"));
        var plan = PassageRanking.PlanExpansion("question", lexical, dense, pool);
        Assert.Equal(Ids("planned"), plan.DisclosureCandidates.Select(p => p.ChunkId));
        Assert.Equal(Ids("oldPlanned"), plan.DisclosureCandidates.Take(223).Select(p => p.ChunkId));
        Assert.Equal(250, plan.DisclosureCandidates.Count);
        Assert.Equal(target, plan.DisclosureCandidates[233].ChunkId);

        var approved = Ids("approved").ToHashSet();
        var ranked = plan.Complete(approved);
        Assert.Equal(Ids("input"), ranked.Select(p => p.Passage.ChunkId));
        Assert.Equal(81, ranked.Count);
        Assert.Equal(target, ranked[69].Passage.ChunkId);
        Assert.All(ranked, p => Assert.Contains(p.Passage.ChunkId, approved));
        var baseline = PassageRanking.Fuse("question", lexical.Where(p => approved.Contains(p.ChunkId)).ToArray(),
            dense.Where(p => approved.Contains(p.ChunkId)).ToArray());
        foreach (var (expected, actual) in baseline.Zip(ranked))
        {
            Assert.Equal(expected with { AlternativePassages = actual.AlternativePassages }, actual);
            Assert.Equal(expected.AlternativePassages.Select(p => p.ChunkId),
                actual.AlternativePassages.Select(p => p.ChunkId));
        }
    }
}
