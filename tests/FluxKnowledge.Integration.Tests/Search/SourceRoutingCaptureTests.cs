using FluxKnowledge.Application.Ports;
using FluxKnowledge.Infrastructure.SqlServer.Search;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Search;

public sealed class SourceRoutingCaptureTests
{
    [Theory]
    [InlineData("CRM", true)]
    [InlineData("QR", true)]
    [InlineData("FR160", true)]
    [InlineData("abc123", true)]
    [InlineData("رقم١٢٣", true)]
    [InlineData("abc", false)]
    [InlineData("Tarasol", false)]
    [InlineData("123", false)]
    [InlineData("A", false)]
    [InlineData("a1", true)]
    [InlineData("ÄÖ", true)]
    [InlineData("A\u0301", false)]
    [InlineData("A\u0301B", true)]
    [InlineData("𐐀𐐁", true)]
    [InlineData("🧠A", false)]
    [InlineData("ABCDEFGHIJKLMN012345678901234567890123456789", false)]
    public void Source_identifiers_use_whole_unicode_runes_and_literal_tag_rules(string token, bool expected) =>
        Assert.Equal(expected, SourcePathTerms.IsTag(token));

    [Fact]
    public void Complete_capture_preserves_every_canonical_key_and_selects_only_matching_source_tags()
    {
        var first = Key(); var second = Key(); var third = Key();
        var capture = new SourceRoutingCapture("crm FR160");
        capture.Add(first, @"E:\root\CRM\requirements.docx", @"E:\root");
        capture.Add(second, @"E:\root\FR160-specification.pdf", @"E:\root");
        capture.Add(third, @"E:\root\Other\requirements.docx", @"E:\root");
        var result = capture.Complete(true, default);
        Assert.True(result.AllKeys.SetEquals([first, second, third]));
        Assert.True(result.DirectedKeys.SetEquals([first, second]));
        Assert.Equal(SourceDirectedRetrievalStatus.Ready, result.Status);
        Assert.InRange(result.EstimatedBytes, 1, 64L * 1024 * 1024);
    }

    [Theory]
    [InlineData(@"E:\root\CRM\note.docx", @"E:\root")]
    [InlineData(@"E:\root\crm\note.docx", @"E:\root")]
    [InlineData(@"E:\root\Other\note.docx", @"E:\root")]
    public void Exact_duplicate_paths_are_safe_but_case_only_or_different_aliases_disable_optional_routing(string secondPath, string secondRoot)
    {
        var key = Key(); var extra = Key(); var capture = new SourceRoutingCapture("CRM");
        var original = @"E:\root\CRM\note.docx";
        capture.Add(key, original, @"E:\root"); capture.Add(key, secondPath, secondRoot);
        capture.Add(extra, @"E:\root\QR\letter.docx", @"E:\root");
        var result = capture.Complete(true, default);
        Assert.True(result.AllKeys.SetEquals([key, extra]));
        if (secondPath == original) Assert.True(result.DirectedKeys.SetEquals([key]));
        else { Assert.Empty(result.DirectedKeys); Assert.Equal(SourceDirectedRetrievalStatus.Unavailable, result.Status); }
    }

    [Theory]
    [InlineData(null, @"E:\root")]
    [InlineData(@"E:\root\CRM.docx", null)]
    [InlineData(@"E:\rootSibling\CRM.docx", @"E:\root")]
    [InlineData(@"E:\root\..\CRM.docx", @"E:\root")]
    [InlineData(@"CRM.docx", @"E:\root")]
    public void Invalid_or_missing_paths_remove_only_routing_while_complete_base_keys_survive(string? path, string? root)
    {
        var first = Key(); var second = Key(); var capture = new SourceRoutingCapture("CRM");
        capture.Add(first, @"E:\root\CRM\requirements.docx", @"E:\root");
        capture.Add(second, path, root);
        var result = capture.Complete(true, default);
        Assert.True(result.AllKeys.SetEquals([first, second]));
        Assert.Empty(result.DirectedKeys); Assert.Equal(SourceDirectedRetrievalStatus.Unavailable, result.Status);
    }

    [Fact]
    public void Routing_overflow_discards_the_entire_optional_selection_but_keeps_subsequent_base_keys()
    {
        var first = Key(); var second = Key(); var capture = new SourceRoutingCapture("CRM", maximumBytes: 2048);
        capture.Add(first, @"E:\root\CRM\requirements.docx", @"E:\root");
        capture.Add(second, @"E:\root\FR160.docx", @"E:\root");
        var result = capture.Complete(true, default);
        Assert.True(result.AllKeys.SetEquals([first, second]));
        Assert.Empty(result.DirectedKeys); Assert.Equal(SourceDirectedRetrievalStatus.Unavailable, result.Status);
        Assert.InRange(result.EstimatedBytes, 0, 2048);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Incomplete_or_cancelled_capture_never_returns_partial_base_keys(bool cancelled)
    {
        var capture = new SourceRoutingCapture("CRM"); capture.Add(Key(), @"E:\root\CRM.docx", @"E:\root");
        using var cancellation = new CancellationTokenSource(); if (cancelled) cancellation.Cancel();
        if (cancelled) Assert.ThrowsAny<OperationCanceledException>(() => capture.Complete(true, cancellation.Token));
        else Assert.Throws<InvalidOperationException>(() => capture.Complete(false, default));
        Assert.Throws<InvalidOperationException>(() => capture.Complete(true, default));
    }

    [Theory]
    [InlineData("QR", @"E:\root\QR-code.docx", true)]
    [InlineData("qr", @"E:\root\QR-code.docx", true)]
    [InlineData("QR", @"E:\root\QRS-code.docx", false)]
    [InlineData("FR160", @"E:\root\FR-160.docx", false)]
    [InlineData("API", @"E:\root\api.docx", false)]
    [InlineData("CAFE", @"E:\root\CAFÉ.docx", false)]
    public void Matching_uses_literal_whole_tags_without_substring_morphology_or_accent_rewrites(string query, string path, bool expected)
    {
        var key = Key(); var capture = new SourceRoutingCapture(query); capture.Add(key, path, @"E:\root");
        var result = capture.Complete(true, default);
        Assert.Equal(expected, result.DirectedKeys.Contains(key));
        Assert.True(result.AllKeys.SetEquals([key]));
    }

    private static CanonicalBindingKey Key() => new(Guid.NewGuid(), Guid.NewGuid(), 1);
}
