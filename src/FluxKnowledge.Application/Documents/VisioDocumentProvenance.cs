using System.Text;
using System.Text.Json;
using FluxKnowledge.Application.Sources;

namespace FluxKnowledge.Application.Documents;

/// <summary>Read-only interpreted Visio facts, kept as offsets rather than duplicate source content.</summary>
public sealed record VisioShapeEvidence(
    int ShapeId,
    int? ParentShapeId,
    int? MasterId,
    string? MasterShapeId,
    string Text,
    IReadOnlyDictionary<string, string>? Data = null,
    int? ConnectorFromShapeId = null,
    int? ConnectorToShapeId = null,
    string? BeginArrow = null,
    string? EndArrow = null);

public sealed record VisioPageEvidence(
    int PageId,
    bool IsBackground,
    IReadOnlyList<VisioShapeEvidence> Shapes,
    int ConnectionCount = 0);

public static class VisioDocumentProvenance
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static (DocumentExtractionResult Extraction, string MetadataJson, int ShapeCount, int ConnectionCount) Create(
        IReadOnlyList<VisioPageEvidence> pages)
    {
        ArgumentNullException.ThrowIfNull(pages);
        if (pages.Count == 0 || pages.Count > VisioDocumentPreflight.MaximumPageCount ||
            pages.Select(static page => page.PageId).Distinct().Count() != pages.Count ||
            pages.Any(static page => page is null || page.PageId < 0 || page.Shapes is null || page.Shapes.Count > VisioDocumentPreflight.MaximumShapesPerPage))
        {
            throw new RetainedProcessorException("visio-document-page-invalid");
        }

        var text = new StringBuilder();
        var textBytes = 0;
        var provenance = new List<DocumentPageProvenance>(pages.Count);
        var extractedPages = new List<DocumentExtractedPage>(pages.Count);
        var shapeCount = 0;
        var connectionCount = 0;
        for (var pageIndex = 0; pageIndex < pages.Count; pageIndex++)
        {
            var page = pages[pageIndex];
            if (text.Length > 0)
            {
                Append(text, Environment.NewLine, ref textBytes, "visio-document-output-too-large");
            }
            var pageStart = text.Length;
            var blocks = new List<DocumentBlockProvenance>(page.Shapes.Count);
            var pageText = new StringBuilder();
            foreach (var shape in page.Shapes)
            {
                if (shape is null || shape.ShapeId < 0 || shape.Text is null ||
                    (shape.ParentShapeId is < 0) || (shape.MasterId is < 0))
                {
                    throw new RetainedProcessorException("visio-document-shape-invalid");
                }
                if (pageText.Length > 0)
                {
                    Append(text, Environment.NewLine, ref textBytes, "visio-document-output-too-large");
                    pageText.Append(Environment.NewLine);
                }

                var blockStart = text.Length;
                Append(text, shape.Text, ref textBytes, "visio-document-output-too-large");
                pageText.Append(shape.Text);
                blocks.Add(new DocumentBlockProvenance(
                    blockStart,
                    text.Length - blockStart,
                    "visio",
                    "shape",
                    null,
                    null,
                    null,
                    null,
                    shape.ShapeId,
                    shape.ParentShapeId,
                    shape.MasterId,
                    shape.MasterShapeId,
                    shape.Data,
                    shape.ConnectorFromShapeId,
                    shape.ConnectorToShapeId,
                    shape.BeginArrow,
                    shape.EndArrow));
                shapeCount++;
            }

            provenance.Add(new DocumentPageProvenance(
                pageIndex,
                pageStart,
                text.Length - pageStart,
                "visio",
                null,
                blocks,
                page.PageId,
                page.IsBackground));
            extractedPages.Add(new DocumentExtractedPage(pageIndex, pageText.ToString(), RequiresOcr: false));
            connectionCount = checked(connectionCount + page.ConnectionCount);
        }

        var resultText = text.ToString();
        var metadata = JsonSerializer.Serialize(new DocumentProvenance(DocumentOcrProvenance.MetadataVersion, provenance), JsonOptions);
        EnsureBoundedUtf8(metadata, VisioDocumentPreflight.MaximumMetadataUtf8Bytes, "visio-document-metadata-too-large");
        return (new DocumentExtractionResult(resultText, IsComplete: true, [], extractedPages), metadata, shapeCount, connectionCount);
    }

    private static void Append(StringBuilder target, string value, ref int byteCount, string reason)
    {
        try
        {
            var bytes = StrictUtf8.GetByteCount(value);
            if (bytes > VisioDocumentPreflight.MaximumExtractedUtf8Bytes - byteCount)
            {
                throw new RetainedProcessorException(reason);
            }
            target.Append(value);
            byteCount += bytes;
        }
        catch (EncoderFallbackException exception)
        {
            throw new RetainedProcessorException("visio-document-text-not-utf8", innerException: exception);
        }
    }

    private static void EnsureBoundedUtf8(string value, int maximumBytes, string reason)
    {
        try
        {
            if (StrictUtf8.GetByteCount(value) > maximumBytes)
            {
                throw new RetainedProcessorException(reason);
            }
        }
        catch (EncoderFallbackException exception)
        {
            throw new RetainedProcessorException("visio-document-metadata-not-utf8", innerException: exception);
        }
    }
}
