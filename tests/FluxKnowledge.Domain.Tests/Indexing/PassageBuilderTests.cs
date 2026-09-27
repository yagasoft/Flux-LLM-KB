using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluxKnowledge.Application.Documents;
using FluxKnowledge.Application.Indexing;
using Xunit;

namespace FluxKnowledge.Domain.Tests.Indexing;

public sealed class PassageBuilderTests
{
    [Fact]
    public void Keeps_complete_sentences_and_overlaps_a_bounded_complete_sentence()
    {
        var builder = new PassageBuilder(new WordTokenizer(), new PassagePolicy(12, 16, 160, 4, 80));
        const string sentence = "A complete useful sentence. ";
        var text = string.Concat(Enumerable.Repeat(sentence, 10));

        var passages = builder.Build(text);

        Assert.True(passages.Count > 1);
        Assert.EndsWith("sentence. ", passages[0].Content, StringComparison.Ordinal);
        Assert.True(passages[1].StartOffset < passages[0].StartOffset + passages[0].Length);
        Assert.True(passages[1].StartOffset > passages[0].StartOffset);
        AssertSpans(text, passages);
    }

    [Fact]
    public void Overlap_cannot_emit_passages_which_only_repeat_an_already_covered_paragraph()
    {
        const string paragraph = "First sentence. Second sentence.\n\n";
        var text = paragraph + new string('x', 2000);
        var passages = new PassageBuilder(new WordTokenizer()).Build(text);
        AssertSpans(text, passages);
        Assert.InRange(passages.Count, 3, 4);
    }

    [Fact]
    public void Preserves_paragraph_and_explicit_shape_boundaries()
    {
        var builder = new PassageBuilder(new WordTokenizer());
        const string first = "First shape has its own meaning.\n";
        const string second = "Second shape belongs to a different process.";

        var passages = builder.Build(first + second, [first.Length]);

        Assert.Equal(new[] { first, second }, passages.Select(p => p.Content));
        AssertSpans(first + second, passages);
    }

    [Fact]
    public void Uses_retained_Visio_shapes_without_joining_unrelated_adjacent_text()
    {
        const string first = "First process.\n";
        const string second = "Different process.";
        var text = first + second;
        var metadata = JsonSerializer.Serialize(new DocumentProvenance(1,
            [new DocumentPageProvenance(0, 0, text.Length, "visio", null,
                [new DocumentBlockProvenance(0, first.Length, "visio", "shape", null, null, null, null, 1),
                 new DocumentBlockProvenance(first.Length, second.Length, "visio", "shape", null, null, null, null, 2)])]),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var builder = new PassageBuilder(new WordTokenizer());
        Assert.Equal(new[] { first, second }, builder.BuildDocument(text, metadata).Select(p => p.Content));
        Assert.Throws<InvalidOperationException>(() => builder.BuildDocument(text[..^1], metadata));
    }

    [Fact]
    public void Counts_using_the_selected_tokenizer_and_binds_policy_and_search_input()
    {
        var builder = new PassageBuilder(new ScalarTokenizer(), new PassagePolicy(32, 48, 200, 8, 32));
        var text = string.Concat(Enumerable.Repeat("short sentence. ", 20));
        var passages = builder.Build(text, contextHeader: "Document heading");

        Assert.All(passages, passage =>
        {
            Assert.InRange(new ScalarTokenizer().CountTokens(passage.Content), 1, 48);
            Assert.InRange(passage.Length, 1, 200);
            Assert.Equal("Document heading", passage.ContextHeader);
            Assert.Equal(passage.ContextHeader + "\n" + passage.Content, passage.SearchText);
            Assert.Equal(Hash(passage.SearchText), passage.SearchInputHash);
            Assert.Equal(builder.PolicyFingerprint, passage.PassagePolicyFingerprint);
        });
        Assert.Equal(passages, builder.Build(text, contextHeader: "Document heading"));
        Assert.NotEqual(builder.PolicyFingerprint, new PassageBuilder(new WordTokenizer(),
            new PassagePolicy(32, 48, 200, 8, 32)).PolicyFingerprint);
        AssertSpans(text, passages);
    }

    [Theory]
    [InlineData("😀")]
    [InlineData("e\u0301")]
    [InlineData("\r\n")]
    [InlineData("👨‍👩‍👧‍👦")]
    [InlineData("🇬🇧")]
    public void Oversized_indivisible_text_progresses_without_splitting_text_elements(string element)
    {
        var builder = new PassageBuilder(new ScalarTokenizer(), new PassagePolicy(32, 48, 65, 0, 0));
        var text = string.Concat(Enumerable.Repeat(element, 300));
        var passages = builder.Build(text);

        Assert.True(passages.Count > 1);
        Assert.All(passages, p => Assert.Equal(0, p.Length % element.Length));
        Assert.Equal(text, string.Concat(passages.Select(p => p.Content)));
        AssertSpans(text, passages);
    }

    [Theory]
    [InlineData("👨‍👩‍👧‍👦", 2)]
    [InlineData("🇬🇧", 2)]
    public void Rejects_a_retained_boundary_inside_an_extended_grapheme(string text, int boundary)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new PassageBuilder(new WordTokenizer()).Build(text, [boundary]));
    }

    [Fact]
    public void Rechecks_preferred_boundaries_when_prefix_token_counts_are_not_monotonic()
    {
        const string text = "One. Two. Three. Four. Five.";
        var builder = new PassageBuilder(new NonMonotonicTokenizer(), new PassagePolicy(8, 12, 40, 0, 0));
        Assert.All(builder.Build(text), p => Assert.InRange(new NonMonotonicTokenizer().CountTokens(p.Content), 1, 12));
    }

    [Fact]
    public void Header_is_bounded_and_empty_text_has_no_passages()
    {
        var builder = new PassageBuilder(new WordTokenizer());
        var header = string.Join(' ', Enumerable.Repeat("title", 100));
        var passage = Assert.Single(builder.Build("Evidence body.", contextHeader: header));

        Assert.InRange(new WordTokenizer().CountTokens(passage.ContextHeader), 1, 32);
        Assert.Empty(builder.Build(string.Empty));
        Assert.Throws<ArgumentOutOfRangeException>(() => builder.Build("text", [5]));
    }

    private static void AssertSpans(string text, IReadOnlyList<FluxKnowledge.Application.Ports.CanonicalTextChunk> passages)
    {
        var coveredUntil = 0;
        foreach (var passage in passages)
        {
            Assert.Equal(text.Substring(passage.StartOffset, passage.Length), passage.Content);
            Assert.Equal(Hash(passage.Content), passage.ContentHash);
            Assert.True(passage.StartOffset <= coveredUntil);
            Assert.True(passage.StartOffset + passage.Length > coveredUntil);
            coveredUntil = passage.StartOffset + passage.Length;
        }
        Assert.Equal(text.Length, coveredUntil);
    }

    private static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    internal sealed class WordTokenizer : IPassageTokenizer
    {
        public string Fingerprint => "synthetic-word-v1";
        public int CountTokens(string text) => text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
    }

    private sealed class ScalarTokenizer : IPassageTokenizer
    {
        public string Fingerprint => "synthetic-scalar-v1";
        public int CountTokens(string text) => text.EnumerateRunes().Count();
    }

    private sealed class NonMonotonicTokenizer : IPassageTokenizer
    {
        public string Fingerprint => "synthetic-non-monotonic-v1";
        public int CountTokens(string text) => text.EndsWith("One. ", StringComparison.Ordinal) ? 100 : text.Length / 2 + 1;
    }
}
