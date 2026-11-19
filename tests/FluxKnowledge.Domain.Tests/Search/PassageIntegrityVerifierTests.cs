using System.Security.Cryptography;
using System.Text;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Search;
using Xunit;

namespace FluxKnowledge.Domain.Tests.Search;

public sealed class PassageIntegrityVerifierTests
{
    [Fact]
    public void Unchanged_passages_reuse_integrity_validation_across_fresh_candidate_instances()
    {
        var verifier = new PassageIntegrityVerifier();
        var passage = Passage("body", "header");
        Assert.True(verifier.MatchesBody(passage));
        Assert.True(verifier.MatchesSearchInput(passage));
        var copy = passage with { Content = new string(passage.Content.ToCharArray()), ContextHeader = new string(passage.ContextHeader.ToCharArray()) };
        Assert.True(verifier.MatchesBody(copy));
        Assert.True(verifier.MatchesSearchInput(copy));
        Assert.Equal(1, verifier.Computations);
    }

    [Fact]
    public void Changed_body_or_header_is_validated_again_and_cannot_borrow_an_older_digest()
    {
        var verifier = new PassageIntegrityVerifier();
        var passage = Passage("body", "header");
        Assert.True(verifier.MatchesSearchInput(passage));
        Assert.False(verifier.MatchesSearchInput(passage with { Content = "bodX" }));
        Assert.False(verifier.MatchesSearchInput(passage with { ContextHeader = "headeX" }));
        Assert.Equal(3, verifier.Computations);
    }

    [Fact]
    public void A_changed_declared_hash_is_compared_with_the_previously_validated_unchanged_text()
    {
        var verifier = new PassageIntegrityVerifier();
        var passage = Passage("body", "header");
        Assert.True(verifier.MatchesBody(passage));
        Assert.False(verifier.MatchesBody(passage with { ChunkHash = new string('0', 64) }));
        Assert.Equal(1, verifier.Computations);
    }

    private static EligiblePassageCandidate Passage(string body, string header) =>
        new(null, null, Guid.NewGuid(), 1, Guid.NewGuid(), new string('a', 64), 1, Hash(body), 0, body.Length,
            body, "synthetic", ContextHeader: header, SearchInputHash: Hash(header + "\n" + body));
    private static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
