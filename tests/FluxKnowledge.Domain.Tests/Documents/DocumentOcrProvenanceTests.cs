using FluxKnowledge.Application.Documents;
using Xunit;

namespace FluxKnowledge.Domain.Tests.Documents;

public sealed class DocumentOcrProvenanceTests
{
    [Fact]
    public void FromNative_keeps_only_text_bearing_pages_with_offsets_matching_the_extracted_pdf_text()
    {
        var extracted = string.Join(Environment.NewLine, ["First native page", "Second native page"]);
        var metadata = DocumentOcrProvenance.FromNative(new DocumentExtractionResult(
            extracted,
            IsComplete: true,
            [],
            [
                new DocumentExtractedPage(0, "First native page", RequiresOcr: false),
                new DocumentExtractedPage(1, string.Empty, RequiresOcr: false),
                new DocumentExtractedPage(2, "Second native page", RequiresOcr: false)
            ]));

        var provenance = DocumentOcrProvenance.Parse(Assert.IsType<string>(metadata));
        Assert.Collection(
            provenance.Pages.OrderBy(static page => page.PageIndex),
            page =>
            {
                Assert.Equal(0, page.PageIndex);
                Assert.Equal(0, page.StartOffset);
                Assert.Equal("First native page".Length, page.Length);
                Assert.Equal("native", page.Method);
            },
            page =>
            {
                Assert.Equal(2, page.PageIndex);
                Assert.Equal("First native page".Length + Environment.NewLine.Length, page.StartOffset);
                Assert.Equal("Second native page".Length, page.Length);
                Assert.Equal("native", page.Method);
            });

        var normalised = DocumentOcrProvenance.NormaliseTextAndMetadata(extracted, metadata!, out var normalisedMetadata);
        Assert.Equal("First native page\n\nSecond native page", normalised);
        Assert.Equal(2, DocumentOcrProvenance.Parse(normalisedMetadata).Pages.Count);
    }

    [Fact]
    public void Merge_uses_ocr_only_for_uncovered_pages_and_preserves_page_provenance()
    {
        var native = new DocumentExtractionResult(
            "Native first page",
            IsComplete: false,
            ["pdf-ocr-required"],
            [
                new DocumentExtractedPage(0, "Native first page", RequiresOcr: false),
                new DocumentExtractedPage(1, string.Empty, RequiresOcr: true)
            ]);
        var ocr = new DocumentOcrExecutionResult(
            true,
            "document-ocr-complete",
            [new DocumentOcrPageResult(
                1,
                90,
                [
                    new DocumentOcrBlock("title", 10, 20, 100, 30, "Scanned title"),
                    new DocumentOcrBlock("table", 10, 60, 300, 120, "Item\tAmount\nA-17\t500")
                ])]);

        var result = DocumentOcrProvenance.Merge(native, ocr);
        var provenance = DocumentOcrProvenance.Parse(result.MetadataJson);

        Assert.Equal("Native first page\n\nScanned title\nItem\tAmount\nA-17\t500", result.Text);
        Assert.Collection(
            provenance.Pages.OrderBy(static page => page.PageIndex),
            page =>
            {
                Assert.Equal(0, page.PageIndex);
                Assert.Equal("native", page.Method);
                Assert.Null(page.OrientationDegrees);
                Assert.Empty(page.Blocks);
            },
            page =>
            {
                Assert.Equal(1, page.PageIndex);
                Assert.Equal("ocr", page.Method);
                Assert.Equal(90, page.OrientationDegrees);
                var table = Assert.Single(page.Blocks, static block => block.Kind == "table");
                Assert.Equal("ocr", table.Method);
                Assert.Equal(300, table.Width);
            });
    }

    [Fact]
    public void Merge_rejects_a_provider_page_that_was_not_explicitly_requested()
    {
        var native = new DocumentExtractionResult(
            "native",
            IsComplete: false,
            ["pdf-ocr-required"],
            [new DocumentExtractedPage(0, "native", RequiresOcr: false)]);
        var ocr = new DocumentOcrExecutionResult(
            true,
            "document-ocr-complete",
            [new DocumentOcrPageResult(0, 0, [])]);

        var failure = Assert.Throws<InvalidOperationException>(() => DocumentOcrProvenance.Merge(native, ocr));

        Assert.Equal("document-ocr-page-result-mismatch", failure.Message);
    }

    [Fact]
    public void Normalisation_recomputes_page_offsets_after_form_kc_changes_length()
    {
        var native = new DocumentExtractionResult(
            "cafe\u0301",
            IsComplete: false,
            ["pdf-ocr-required"],
            [
                new DocumentExtractedPage(0, "cafe\u0301", RequiresOcr: false),
                new DocumentExtractedPage(1, string.Empty, RequiresOcr: true)
            ]);
        var merged = DocumentOcrProvenance.Merge(
            native,
            new DocumentOcrExecutionResult(
                true,
                "document-ocr-complete",
                [new DocumentOcrPageResult(1, 0, [new DocumentOcrBlock("text", 1, 1, 10, 10, "second")])]));

        var normalised = DocumentOcrProvenance.NormaliseTextAndMetadata(merged.Text, merged.MetadataJson, out var metadata);
        var provenance = DocumentOcrProvenance.Parse(metadata);

        Assert.Equal("café\n\nsecond", normalised);
        Assert.Collection(
            provenance.Pages.OrderBy(static page => page.PageIndex),
            page => Assert.Equal(4, page.Length),
            page => Assert.Equal(6, page.StartOffset));
    }
}
