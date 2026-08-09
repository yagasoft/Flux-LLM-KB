using System.Text;
using FluxKnowledge.Application.Sources;
using Xunit;

namespace FluxKnowledge.Domain.Tests.Sources;

public sealed class SourceClassifierTests
{
    [Fact]
    public void Classify_marks_a_pdf_signature_with_a_text_extension_as_unknown()
    {
        var result = SourceClassifier.Classify("misleading.txt", "%PDF-1.7"u8.ToArray(), 8);

        Assert.Equal(SourceClassification.Unknown, result.Classification);
        Assert.Null(result.Text);
    }

    [Fact]
    public void Classify_accepts_utf8_text_with_a_bom()
    {
        var bytes = Encoding.UTF8.GetPreamble().Concat("café"u8.ToArray()).ToArray();

        var result = SourceClassifier.Classify("notes.md", bytes, bytes.Length);

        Assert.Equal(SourceClassification.AcceptedUtf8Text, result.Classification);
        Assert.Equal("café", result.Text);
    }

    [Fact]
    public void Classify_defers_text_larger_than_sixteen_mebibytes()
    {
        var result = SourceClassifier.Classify("large.txt", "a"u8.ToArray(), (16 * 1024 * 1024) + 1);

        Assert.Equal(SourceClassification.DeferredPolicy, result.Classification);
        Assert.Null(result.Text);
    }

    [Fact]
    public void Classify_accepts_the_exact_sixteen_mebibyte_boundary()
    {
        var bytes = new byte[16 * 1024 * 1024];
        Array.Fill(bytes, (byte)'a');

        var result = SourceClassifier.Classify("boundary.txt", bytes, bytes.Length);

        Assert.Equal(SourceClassification.AcceptedUtf8Text, result.Classification);
        Assert.NotNull(result.Text);
    }

    [Fact]
    public void Classify_defers_invalid_utf8_without_producing_a_text_projection()
    {
        var result = SourceClassifier.Classify("invalid.json", [0xc3, 0x28], 2);

        Assert.Equal(SourceClassification.DeferredPolicy, result.Classification);
        Assert.Null(result.Text);
    }

    [Fact]
    public void Classify_fails_closed_for_a_nul_containing_supported_extension()
    {
        var result = SourceClassifier.Classify("misleading.txt", "text\0payload"u8.ToArray(), 12);

        Assert.Equal(SourceClassification.DeferredPolicy, result.Classification);
        Assert.Null(result.Text);
    }

    [Fact]
    public void Classify_represents_an_unknown_extension_without_text_projection()
    {
        var result = SourceClassifier.Classify("opaque.payload", "text"u8.ToArray(), 4);

        Assert.Equal(SourceClassification.Unknown, result.Classification);
        Assert.Null(result.Text);
    }
}
