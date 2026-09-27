using System.Security.Cryptography;
using System.Text;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Search;
using Xunit;

namespace FluxKnowledge.Domain.Tests.Search;

public sealed class PassageRankingTests
{
    [Fact]
    public void Fusion_preserves_one_passage_identity_and_both_channel_ranks()
    {
        var shared = Passage(8, "Shared body");
        var ranked = PassageRanking.Fuse("query", [shared, Passage(4, "Lexical")], [Passage(2, "Dense"), shared]);
        Assert.Equal(8, ranked[0].Passage.ChunkId);
        Assert.Equal(1, ranked[0].LexicalRank);
        Assert.Equal(2, ranked[0].SemanticRank);
        Assert.Equal(1D / 61 + 1D / 62, ranked[0].FusionScore, 10);
        Assert.Equal(3, ranked.Count);
    }

    [Fact]
    public void Exact_body_tier_survives_fusion_and_adversarial_reranker_scores()
    {
        var shortlist = PassageRanking.Fuse("ID-0042", [Passage(8, "Device ID-0042")], [Passage(2, "Another device", "ID-0042")]);
        var ranked = PassageRanking.ApplyScores(shortlist, new([new(8, -100), new(2, 100)], "ranker"), "ranker");
        Assert.Equal(new long[] { 8, 2 }, ranked.Select(value => value.Passage.ChunkId));
        Assert.True(ranked[0].ExactBodyMatch);
        Assert.False(ranked[1].ExactBodyMatch);
    }

    [Fact]
    public void Contradictory_payload_for_same_passage_is_refused_instead_of_substituted()
    {
        var first = Passage(8, "First");
        Assert.Throws<InvalidOperationException>(() => PassageRanking.Fuse("query", [first], [first with { Content = "Changed" }]));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Incomplete_foreign_duplicate_or_nonfinite_scores_refuse_the_entire_batch(int fault)
    {
        var shortlist = PassageRanking.Fuse("query", [Passage(8, "Eight"), Passage(2, "Two")], []);
        IReadOnlyList<RerankScore> scores = fault switch
        {
            0 => [new(8, 1)],
            1 => [new(8, 1), new(9, 2)],
            2 => [new(8, 1), new(8, 2)],
            _ => [new(8, float.NaN), new(2, 2)]
        };
        Assert.Throws<InvalidOperationException>(() => PassageRanking.ApplyScores(shortlist, new(scores, "ranker"), "ranker"));
        Assert.All(shortlist, value => Assert.Null(value.RerankerScore));
    }

    [Fact]
    public void Shortlist_is_bounded_after_union_and_deterministic_ties()
    {
        var ranked = PassageRanking.Fuse("query", Enumerable.Range(1, 100).Select(i => Passage(i, "body")).ToArray(),
            Enumerable.Range(101, 100).Select(i => Passage(i, "body")).ToArray());
        Assert.Equal(50, ranked.Count);
        Assert.Equal(new long[] { 1, 101, 2, 102 }, ranked.Take(4).Select(value => value.Passage.ChunkId));
    }

    [Theory]
    [InlineData(79, false)]
    [InlineData(80, true)]
    public void Duplicate_threshold_uses_intersection_of_the_shorter_span(int intersection, bool suppressed)
    {
        var first = Passage(1, new string('a', 200)) with { StartOffset = 0 };
        var second = Passage(2, new string('a', 100)) with
            { PipelineRecordId = first.PipelineRecordId, ArtifactId = first.ArtifactId, StartOffset = 200 - intersection };
        Assert.Equal(suppressed, PassageRanking.IsNearDuplicate(second, [first]));
        Assert.False(PassageRanking.IsNearDuplicate(second with { PipelineRecordId = Guid.NewGuid() }, [first]));
    }

    [Fact]
    public void Logical_publication_identity_applies_across_separate_branch_records()
    {
        var owner = Guid.NewGuid();
        var first = Passage(1, new string('a', 100)) with { OwnerSourceRevisionId = owner };
        var second = Passage(2, new string('a', 100)) with { OwnerSourceRevisionId = owner, ArtifactId = first.ArtifactId };
        Assert.Equal(PassageRanking.DocumentIdentity(first), PassageRanking.DocumentIdentity(second));
        Assert.True(PassageRanking.IsNearDuplicate(second, [first]));
        Assert.False(PassageRanking.IsNearDuplicate(second with { ArtifactId = Guid.NewGuid() }, [first]));
    }

    private static EligiblePassageCandidate Passage(long id, string text, string header = "") =>
        new(null, null, Guid.NewGuid(), 1, Guid.NewGuid(), new string('a', 64), id, Hash(text), 0, text.Length,
            text, "public.txt", ContextHeader: header, PassagePolicyFingerprint: new string('b', 64),
            SearchInputHash: Hash(header.Length == 0 ? text : header + "\n" + text));

    private static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}
