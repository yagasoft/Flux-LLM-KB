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
    public void Meaningful_spans_preserve_CRLF_text_elements()
    {
        var text = string.Concat(Enumerable.Repeat("Evidence\r\n", 100));
        var builder = new PassageBuilder(new ScalarTokenizer(), new PassagePolicy(32, 48, 65, 0, 0));
        var passages = builder.Build(text);

        Assert.True(passages.Count > 1);
        Assert.All(passages, passage =>
        {
            Assert.False(passage.StartOffset > 0 && text[passage.StartOffset - 1] == '\r');
            Assert.False(passage.StartOffset + passage.Length < text.Length && text[passage.StartOffset + passage.Length - 1] == '\r');
        });
        AssertSpans(text, passages);
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

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    [InlineData(" \t\r\n\u00a0\u2003")]
    public void Whitespace_only_documents_have_no_search_passages(string whitespace)
    {
        var text = string.Concat(Enumerable.Repeat(whitespace, 1100));
        Assert.Empty(new PassageBuilder(new WordTokenizer()).Build(text, contextHeader: "Document title"));
    }

    [Fact]
    public void Blank_segments_do_not_create_embedding_inputs_or_change_evidence_offsets()
    {
        const string first = "First evidence.";
        var blank = new string('\n', 1100) + "\t\u00a0\u2003";
        const string second = "Second evidence.";
        var text = blank + first + blank + second + blank;
        var firstStart = blank.Length;
        var secondStart = firstStart + first.Length + blank.Length;
        var boundaries = new[] { firstStart, firstStart + first.Length, secondStart, secondStart + second.Length };
        var builder = new PassageBuilder(new WordTokenizer());

        var passages = builder.Build(text, boundaries);

        Assert.Equal(new[] { first, second }, passages.Select(passage => passage.Content));
        Assert.Equal(new[] { firstStart, secondStart }, passages.Select(passage => passage.StartOffset));
        Assert.Equal(new[] { 0, 1 }, passages.Select(passage => passage.Ordinal));
        Assert.All(passages, passage =>
        {
            Assert.Equal(text.Substring(passage.StartOffset, passage.Length), passage.Content);
            Assert.Equal(Hash(passage.Content), passage.ContentHash);
            Assert.False(string.IsNullOrWhiteSpace(passage.SearchText));
        });
        Assert.Equal(passages, builder.Build(text, boundaries));
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
