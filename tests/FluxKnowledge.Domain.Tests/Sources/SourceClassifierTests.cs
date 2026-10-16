using System.Text;
using FluxKnowledge.Application.Sources;
using Xunit;

namespace FluxKnowledge.Domain.Tests.Sources;

public sealed class SourceClassifierTests
{
    [Theory]
    [InlineData("empty.zip", "504b0506000000000000000000000000000000000000", "archive-zip-expansion-pending")]
    [InlineData("data.rar", "526172211a070100", "binary-format-parser-unavailable")]
    [InlineData("data.7z", "377abcaf271c0000", "binary-format-parser-unavailable")]
    [InlineData("data.db-wal", "", "database-sidecar-parser-unavailable")]
    [InlineData("data.db-shm", "00000000", "database-sidecar-parser-unavailable")]
    public void Recognised_binary_formats_are_capability_deferrals_instead_of_text_policy_errors(string path, string hex, string reason)
    {
        var bytes = Convert.FromHexString(hex);
        var result = SourceClassifier.Classify(path, bytes, bytes.Length);
        Assert.Equal(SourceClassification.DeferredCapability, result.Classification);
        Assert.Equal(reason, result.Reason);
        Assert.Null(result.Text);
    }

    [Theory]
    [InlineData("report.rdl")]
    [InlineData("settings.ini")]
    [InlineData("page.html")]
    [InlineData("records.ndjson")]
    [InlineData("captions.vtt")]
    [InlineData("shortcut.url")]
    [InlineData("query.sql")]
    [InlineData("script.py")]
    [InlineData("module.fs")]
    [InlineData("component.ts")]
    public void Default_policy_accepts_supported_workspace_text_as_inert_strict_utf8(string fileName)
    {
        var bytes = "Workspace text"u8.ToArray();
        var result = SourceClassifier.Classify(fileName, bytes, bytes.Length);
        Assert.Equal(SourceClassification.AcceptedUtf8Text, result.Classification);
        Assert.Equal("Workspace text", result.Text);
        Assert.Null(result.Reason);
        Assert.False(SourceClassifier.Classify(fileName, [0xc3, 0x28], 2).IsAccepted);
        Assert.False(SourceClassifier.Classify(fileName, "%PDF-1.7"u8, 8).IsAccepted);
    }

    [Fact]
    public void Default_text_limit_admits_twenty_mebibytes_and_keeps_a_finite_ceiling()
    {
        var bytes = new byte[20 * 1024 * 1024];
        Array.Fill(bytes, (byte)'a');
        var result = SourceClassifier.Classify("large.csv", bytes, bytes.Length);
        Assert.True(result.IsAccepted);
        Assert.Equal(bytes.Length, result.Text!.Length);
        Assert.False(SourceClassifier.Classify("oversized.csv", "a"u8, 32L * 1024 * 1024 + 1).IsAccepted);
        Assert.False(SourceClassifier.Classify("partial.csv", "a"u8, bytes.Length, hasFullBoundedBuffer: false).IsAccepted);
    }

    [Fact]
    public void Classify_accepts_only_complete_strict_utf8_csharp_after_binary_guards()
    {
        var bytes = "public class Example { }"u8.ToArray();

        var accepted = SourceClassifier.Classify("Example.cs", bytes, bytes.Length);
        var partial = SourceClassifier.Classify("Example.cs", bytes[..8], bytes.Length, hasFullBoundedBuffer: false);
        var invalid = SourceClassifier.Classify("Example.cs", [0xc3, 0x28], 2);
        var control = SourceClassifier.Classify("Example.cs", [0x00, 0x20], 2);
        var binary = SourceClassifier.Classify("Example.cs", "%PDF-1.7"u8, 8);

        Assert.Equal(SourceClassification.AcceptedUtf8Text, accepted.Classification);
        Assert.Equal(SourceClassification.DeferredPolicy, partial.Classification);
        Assert.Equal(SourceClassification.DeferredPolicy, invalid.Classification);
        Assert.Equal(SourceClassification.DeferredPolicy, control.Classification);
        Assert.Equal(SourceClassification.DeferredCapability, binary.Classification);
        Assert.Null(partial.Text);
        Assert.Null(invalid.Text);
        Assert.Null(control.Text);
        Assert.Null(binary.Text);
    }

    [Theory]
    [InlineData("example.py")]
    [InlineData("example.fs")]
    [InlineData("example.vb")]
    [InlineData("example.ts")]
    public void Explicitly_disabled_code_text_remains_deferred(string fileName)
    {
        var result = SourceClassifier.Classify(fileName, "code"u8, 4, indexSourceText: false);

        Assert.Equal(SourceClassification.DeferredPolicy, result.Classification);
        Assert.Null(result.Text);
    }

    [Theory]
    [InlineData("legacy.doc")]
    [InlineData("legacy.xls")]
    [InlineData("legacy.ppt")]
    public void Compound_file_magic_designates_legacy_office_as_deferred_capability(string name)
    {
        var bytes = new byte[] { 0xd0, 0xcf, 0x11, 0xe0, 0xa1, 0xb1, 0x1a, 0xe1 };

        var result = SourceClassifier.Classify(name, bytes, bytes.Length);

        Assert.Equal(SourceClassification.DeferredCapability, result.Classification);
    }

    [Fact]
    public void Classify_marks_a_pdf_signature_with_a_text_extension_as_unknown()
    {
        var result = SourceClassifier.Classify("misleading.txt", "%PDF-1.7"u8.ToArray(), 8);

        Assert.Equal(SourceClassification.Unknown, result.Classification);
        Assert.Null(result.Text);
    }

    [Theory]
    [MemberData(nameof(BinarySignatureReasons))]
    public void Classify_records_a_precise_reason_for_each_binary_format_path(
        string fileName,
        byte[] bytes,
        string expectedReason)
    {
        var result = SourceClassifier.Classify(fileName, bytes, bytes.Length);

        Assert.Equal(SourceClassification.DeferredCapability, result.Classification);
        Assert.Equal(expectedReason, result.Reason);
    }

    public static TheoryData<string, byte[], string> BinarySignatureReasons() => new()
    {
        { "document.pdf", "%PDF-1.7"u8.ToArray(), "pdf-document-extraction-pending" },
        { "legacy.doc", new byte[] { 0xd0, 0xcf, 0x11, 0xe0, 0xa1, 0xb1, 0x1a, 0xe1 }, "legacy-office-binary-parser-unavailable" },
        { "diagram.vsdx", new byte[] { 0x50, 0x4b, 0x03, 0x04 }, "vsdx-structural-extraction-pending" },
        { "document.docx", new byte[] { 0x50, 0x4b, 0x03, 0x04 }, "ooxml-structural-extraction-pending" },
        { "photo.jpg", new byte[] { 0xff, 0xd8, 0xff, 0xe0 }, "media-metadata-extraction-pending" }
    };

    [Theory]
    [MemberData(nameof(SupportedMediaBinarySignatures))]
    public void Classify_defers_each_supported_media_signature_to_a_capability(string fileName, byte[] bytes)
    {
        var result = SourceClassifier.Classify(fileName, bytes, bytes.Length);

        Assert.Equal(SourceClassification.DeferredCapability, result.Classification);
        Assert.Null(result.Text);
    }

    [Theory]
    [MemberData(nameof(RecognisedUnsupportedMediaSignatures))]
    public void Classify_defers_recognised_unsupported_media_signatures_to_the_capability_path(string fileName, byte[] bytes)
    {
        var result = SourceClassifier.Classify(fileName, bytes, bytes.Length);

        Assert.Equal(SourceClassification.DeferredCapability, result.Classification);
        Assert.Null(result.Text);
    }

    public static TheoryData<string, byte[]> RecognisedUnsupportedMediaSignatures() => new()
    {
        { "audio.aac", new byte[] { 0xff, 0xf1, 0x50, 0x80, 0x00, 0xe0, 0xfc } },
        { "audio.wma", new byte[] { 0x30, 0x26, 0xb2, 0x75, 0x8e, 0x66, 0xcf, 0x11, 0xa6, 0xd9, 0x00, 0xaa, 0x00, 0x62, 0xce, 0x6c, 0x1e, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01, 0x02 } }
    };

    public static TheoryData<string, byte[]> SupportedMediaBinarySignatures() => new()
    {
        { "photo.jpg", new byte[] { 0xff, 0xd8, 0xff, 0xe0 } },
        { "photo.png", new byte[] { 0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a } },
        { "animation.gif", "GIF89a"u8.ToArray() },
        { "photo.bmp", "BM\0\0\0\0"u8.ToArray() },
        { "photo.tiff", new byte[] { (byte)'I', (byte)'I', 0x2a, 0x00 } },
        { "photo.webp", "RIFF\0\0\0\0WEBP"u8.ToArray() },
        { "audio.mp3", "ID3\x04\0\0"u8.ToArray() },
        { "audio.wav", "RIFF\0\0\0\0WAVE"u8.ToArray() },
        { "video.mov", new byte[] { 0x00, 0x00, 0x00, 0x18, (byte)'f', (byte)'t', (byte)'y', (byte)'p', (byte)'q', (byte)'t', (byte)' ', (byte)' ' } },
        { "video.mp4", new byte[] { 0x00, 0x00, 0x00, 0x18, (byte)'f', (byte)'t', (byte)'y', (byte)'p', (byte)'i', (byte)'s', (byte)'o', (byte)'m' } },
        { "video.m4v", new byte[] { 0x00, 0x00, 0x00, 0x18, (byte)'f', (byte)'t', (byte)'y', (byte)'p', (byte)'M', (byte)'4', (byte)'V', (byte)' ' } }
    };

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
        var result = SourceClassifier.Classify("large.txt", "a"u8.ToArray(), (16 * 1024 * 1024) + 1,
            maximumAcceptedTextBytes: 16 * 1024 * 1024);

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
