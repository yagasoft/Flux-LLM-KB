using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Search;
using Xunit;

namespace FluxKnowledge.Domain.Tests.Search;

public sealed partial class PassageRankingTests
{
    [Fact]
    public void Three_channels_select_at_most_two_hundred_concrete_bindings_before_disclosure()
    {
        var lexical = Enumerable.Range(1, 100).Select(i => Passage(i, "Lexical " + i)).ToArray();
        var dense = Enumerable.Range(1001, 100).Select(i => Passage(i, "Dense " + i)).ToArray();
        var source = Enumerable.Range(2001, 100).Select(i => Passage(i, "Directed " + i)).ToArray();
        var selected = PassageRanking.SelectForDisclosure("question", lexical, dense, source);
        Assert.Equal(200, selected.Count);
        Assert.Equal(200, selected.Select(p => p.ChunkId).Distinct().Count());
        Assert.Contains(selected, p => p.ChunkId == 1);
        Assert.Contains(selected, p => p.ChunkId == 1001);
        Assert.Contains(selected, p => p.ChunkId == 2001);
        Assert.DoesNotContain(selected, p => p.ChunkId == 100);
        Assert.DoesNotContain(selected, p => p.ChunkId == 1100);
        Assert.DoesNotContain(selected, p => p.ChunkId == 2100);
    }

    [Fact]
    public void Copies_cannot_consume_validation_capacity_before_higher_priority_distinct_inputs()
    {
        var copies = Enumerable.Range(1, 100).Select(i => Passage(i, "Repeated input")).ToArray();
        var dense = Enumerable.Range(1001, 100).Select(i => Passage(i, "Dense " + i)).ToArray();
        var source = Enumerable.Range(2001, 100).Select(i => Passage(i, "Directed " + i)).ToArray();
        var selected = PassageRanking.SelectForDisclosure("question", copies, dense, source);
        Assert.Equal(200, selected.Count);
        Assert.Single(selected, p => p.Content == "Repeated input");
        Assert.Equal(200, selected.Select(p => p.SearchInputHash).Distinct().Count());
    }

    [Fact]
    public void Repeated_inputs_vote_once_per_channel_and_keep_independent_bindings()
    {
        var a = Passage(1, "Repeated input"); var b = Passage(2, "Repeated input"); var c = Passage(3, "Repeated input");
        var result = Assert.Single(PassageRanking.Fuse("question", [a, b], [b], [b, c]));
        Assert.Equal(3D / 61, result.FusionScore, 10);
        Assert.Equal(1, result.LexicalRank); Assert.Equal(1, result.SemanticRank); Assert.Equal(1, result.SourceDirectedRank);
        Assert.Equal(new long[] { 2, 3 }, result.AlternativePassages.Select(p => p.ChunkId));
        Assert.Equal(3, PassageRanking.SelectForDisclosure("question", [a, b], [b], [b, c]).Count);
    }

    [Fact]
    public void A_conflict_beyond_the_validation_cutoff_refuses_before_deduplication_or_truncation()
    {
        var lexical = Enumerable.Range(1, 100).Select(i => Passage(i, "Lexical " + i)).ToArray();
        var dense = Enumerable.Range(1001, 100).Select(i => Passage(i, "Dense " + i)).ToArray();
        var source = Enumerable.Range(2001, 99).Select(i => Passage(i, "Directed " + i))
            .Append(lexical[^1] with { Content = "Contradictory canonical data" }).ToArray();
        var error = Assert.Throws<InvalidOperationException>(() => PassageRanking.SelectForDisclosure("question", lexical, dense, source));
        Assert.Equal("passage-candidate-identity-conflict", error.Message);
    }

    [Fact]
    public void Surviving_alternative_replaces_an_unvalidated_representative_after_fresh_disclosure()
    {
        var a = Passage(1, "Repeated input"); var b = Passage(2, "Repeated input"); var c = Passage(3, "Other input");
        var source = Passage(4, "Repeated input");
        var selected = PassageRanking.SelectForDisclosure("question", [a, b], [c], [source]);
        Assert.Equal(4, selected.Count);
        // Each surviving binding was independently approved; binding 1 was withdrawn.
        var surviving = new HashSet<long> { 2, 3, 4 };
        var ranked = PassageRanking.Fuse("question", new[] { a, b }.Where(p => surviving.Contains(p.ChunkId)).ToArray(),
            [c], [source]);
        var copy = Assert.Single(ranked, r => r.Passage.Content == "Repeated input");
        Assert.Equal(2, copy.Passage.ChunkId);
        Assert.Equal(4, Assert.Single(copy.AlternativePassages).ChunkId);
        Assert.DoesNotContain(ranked, r => r.Passage.ChunkId == 1 || r.AlternativePassages.Any(p => p.ChunkId == 1));
    }

    [Fact]
    public void Absent_source_channel_preserves_the_existing_two_channel_scores_and_order()
    {
        var lexical = new[] { Passage(1, "Lexical"), Passage(2, "Shared") };
        var dense = new[] { Passage(3, "Dense"), Passage(4, "Shared") };
        var old = PassageRanking.Fuse("question", lexical, dense);
        var current = PassageRanking.Fuse("question", lexical, dense, []);
        Assert.Equal(old.Select(p => (p.Passage.ChunkId, p.FusionScore, p.LexicalRank, p.SemanticRank)),
            current.Select(p => (p.Passage.ChunkId, p.FusionScore, p.LexicalRank, p.SemanticRank)));
        Assert.Equal(old.SelectMany(p => p.AlternativePassages).Select(p => p.ChunkId), current.SelectMany(p => p.AlternativePassages).Select(p => p.ChunkId));
        Assert.All(current, p => Assert.Null(p.SourceDirectedRank));
    }

    [Fact]
    public void Third_channel_cannot_exceed_its_own_hundred_binding_budget()
    {
        var source = Enumerable.Range(1, 101).Select(i => Passage(i, "Directed " + i)).ToArray();
        Assert.Throws<InvalidOperationException>(() => PassageRanking.SelectForDisclosure("question", [], [], source));
        Assert.Throws<InvalidOperationException>(() => PassageRanking.Fuse("question", [], [], source));
    }
}
