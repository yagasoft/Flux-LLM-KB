using FluxKnowledge.Application.IntegrationV1;
using Xunit;

namespace FluxKnowledge.Domain.Tests.IntegrationV1;

public sealed class CodexPromptContextPolicyTests
{
    [Theory]
    [InlineData("What's next?")]
    [InlineData("Retrieval")]
    [InlineData("PLEASE CONTINUE NOW")]
    [InlineData("API")]
    public void Vague_or_excluded_words_do_not_become_queries(string prompt) =>
        Assert.False(CodexPromptContextPolicy.Analyse(prompt).Eligible);

    [Theory]
    [InlineData("INV_0042", "The invoice INV_0042 was approved.", true)]
    [InlineData("INV_0042", "The invoice XINV_0042X was approved.", false)]
    [InlineData("ABCD", "This cites ABCD exactly.", true)]
    [InlineData("ABCD", "This cites XABCDX only.", false)]
    [InlineData("retention policy", "The retention policy is 30 days.", true)]
    [InlineData("retention policy", "The policy title refers to storage only.", false)]
    [InlineData("café menu", "The cafe\u0301 menu has changed.", true)]
    public void Match_uses_original_case_identifier_detection_and_whole_normalised_body_tokens(
        string prompt, string body, bool expected) =>
        Assert.Equal(expected, CodexPromptContextPolicy.MatchesBody(CodexPromptContextPolicy.Analyse(prompt), body));

    [Fact]
    public void Capitalisation_does_not_promote_stop_words_to_identifiers() =>
        Assert.False(CodexPromptContextPolicy.Analyse("NEXT").Eligible);

    [Fact]
    public void Identifier_detection_checks_original_case_before_case_insensitive_deduplication() =>
        Assert.True(CodexPromptContextPolicy.Analyse("abcd ABCD").Eligible);

    [Fact]
    public void Matching_requires_half_of_distinct_meaningful_terms_with_a_minimum_of_two()
    {
        var query = CodexPromptContextPolicy.Analyse("alpha beta gamma delta epsilon");
        Assert.False(CodexPromptContextPolicy.MatchesBody(query, "alpha beta only"));
        Assert.True(CodexPromptContextPolicy.MatchesBody(query, "alpha beta gamma present"));
    }

    [Fact]
    public void Equal_bodies_are_deduplicated_after_whitespace_and_FormC_normalisation() =>
        Assert.Equal(CodexPromptContextPolicy.NormaliseBodyIdentity("cafe\u0301  menu\nchanged"),
            CodexPromptContextPolicy.NormaliseBodyIdentity("café menu changed"));
}
