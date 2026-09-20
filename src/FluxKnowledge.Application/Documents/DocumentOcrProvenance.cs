using System.Text;
using System.Text.Json;

namespace FluxKnowledge.Application.Documents;

/// <summary>
/// Bounded local OCR output. The provider process supplies no source identity or filesystem
/// authority; the caller binds these blocks to retained pages before they can enter the pipeline.
/// </summary>
public sealed record DocumentOcrBlock(
    string Kind,
    int Left,
    int Top,
    int Width,
    int Height,
    string Text);

public sealed record DocumentOcrPageResult(
    int PageIndex,
    int OrientationDegrees,
    IReadOnlyList<DocumentOcrBlock> Blocks);

public sealed record DocumentOcrExecutionResult(
    bool Succeeded,
    string ReasonCode,
    IReadOnlyList<DocumentOcrPageResult> Pages);

public sealed record DocumentBlockProvenance(
    int StartOffset,
    int Length,
    string Method,
    string? Kind,
    int? Left,
    int? Top,
    int? Width,
    int? Height);

public sealed record DocumentPageProvenance(
    int PageIndex,
    int StartOffset,
    int Length,
    string Method,
    int? OrientationDegrees,
    IReadOnlyList<DocumentBlockProvenance> Blocks);

public sealed record DocumentProvenance(int Version, IReadOnlyList<DocumentPageProvenance> Pages);

public sealed record DocumentOcrMergeResult(string Text, string MetadataJson);

/// <summary>
/// Joins native page extraction with locally produced OCR only for pages explicitly identified
/// as uncovered. Its metadata holds offsets rather than duplicate document text, preserving the
/// original document identity while allowing later chunks to report their source page.
/// </summary>
public static class DocumentOcrProvenance
{
    public const int MetadataVersion = 1;
    public const int MaximumMetadataUtf8Bytes = 4 * 1024 * 1024;
    public const int MaximumTextUtf8Bytes = 16 * 1024 * 1024;
    public const int MaximumBlocksPerPage = 3_000;

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Records native PDF page offsets when the extractor supplied an exact page projection.
    /// Empty pages carry no searchable text and are intentionally omitted: retaining them would
    /// introduce separators that were not present in the extracted text.
    /// </summary>
    public static string? FromNative(DocumentExtractionResult nativeExtraction)
    {
        ArgumentNullException.ThrowIfNull(nativeExtraction);
        var nativePages = nativeExtraction.Pages;
        if (nativePages.Count == 0)
        {
            return null;
        }
        if (nativeExtraction.Text is null ||
            nativePages.Select(static page => page.PageIndex).Distinct().Count() != nativePages.Count ||
            nativePages.Any(static page => page.PageIndex < 0 || page.Text is null || page.RequiresOcr))
        {
            throw new InvalidOperationException("document-native-pages-invalid");
        }

        var text = new StringBuilder(nativeExtraction.Text.Length);
        var utf8Bytes = 0;
        var provenance = new List<DocumentPageProvenance>(nativePages.Count);
        foreach (var page in nativePages.OrderBy(static page => page.PageIndex))
        {
            if (page.Text.Length == 0)
            {
                continue;
            }

            if (text.Length > 0)
            {
                AppendBounded(text, Environment.NewLine, ref utf8Bytes);
            }
            var pageStart = text.Length;
            AppendBounded(text, page.Text, ref utf8Bytes);
            provenance.Add(new DocumentPageProvenance(
                page.PageIndex,
                pageStart,
                text.Length - pageStart,
                "native",
                null,
                []));
        }

        if (!string.Equals(text.ToString(), nativeExtraction.Text, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("document-native-page-text-mismatch");
        }
        if (provenance.Count == 0)
        {
            return null;
        }

        var metadata = JsonSerializer.Serialize(new DocumentProvenance(MetadataVersion, provenance), JsonOptions);
        EnsureStrictUtf8(metadata, MaximumMetadataUtf8Bytes, "document-ocr-metadata-too-large");
        return metadata;
    }

    public static DocumentOcrMergeResult Merge(
        DocumentExtractionResult nativeExtraction,
        DocumentOcrExecutionResult ocr)
    {
        ArgumentNullException.ThrowIfNull(nativeExtraction);
        ArgumentNullException.ThrowIfNull(ocr);
        if (!ocr.Succeeded)
        {
            throw new InvalidOperationException(RequireReason(ocr.ReasonCode));
        }

        var nativePages = nativeExtraction.Pages;
        if (nativePages.Count == 0 || nativePages.Select(static page => page.PageIndex).Distinct().Count() != nativePages.Count ||
            nativePages.Any(static page => page.PageIndex < 0 || page.Text is null))
        {
            throw new InvalidOperationException("document-ocr-native-pages-invalid");
        }

        var requiredPages = nativePages.Where(static page => page.RequiresOcr).Select(static page => page.PageIndex).ToHashSet();
        var ocrPages = ocr.Pages.ToDictionary(static page => page.PageIndex);
        if (ocrPages.Count != ocr.Pages.Count || !requiredPages.SetEquals(ocrPages.Keys))
        {
            throw new InvalidOperationException("document-ocr-page-result-mismatch");
        }

        var text = new StringBuilder();
        var utf8Bytes = 0;
        var provenance = new List<DocumentPageProvenance>(nativePages.Count);
        foreach (var page in nativePages.OrderBy(static page => page.PageIndex))
        {
            if (text.Length > 0)
            {
                AppendBounded(text, "\n\n", ref utf8Bytes);
            }

            var pageStart = text.Length;
            if (!page.RequiresOcr)
            {
                AppendBounded(text, page.Text, ref utf8Bytes);
                provenance.Add(new DocumentPageProvenance(
                    page.PageIndex,
                    pageStart,
                    text.Length - pageStart,
                    "native",
                    null,
                    []));
                continue;
            }

            var ocrPage = ocrPages[page.PageIndex];
            if (ocrPage.OrientationDegrees is not (0 or 90 or 180 or 270) || ocrPage.Blocks is null ||
                ocrPage.Blocks.Count > MaximumBlocksPerPage)
            {
                throw new InvalidOperationException("document-ocr-page-result-invalid");
            }

            var blocks = new List<DocumentBlockProvenance>(ocrPage.Blocks.Count);
            foreach (var block in ocrPage.Blocks)
            {
                if (block is null || !IsSupportedKind(block.Kind) || block.Text is null ||
                    block.Left < 0 || block.Top < 0 || block.Width <= 0 || block.Height <= 0)
                {
                    throw new InvalidOperationException("document-ocr-block-invalid");
                }

                if (blocks.Count > 0)
                {
                    AppendBounded(text, "\n", ref utf8Bytes);
                }

                var start = text.Length;
                AppendBounded(text, block.Text, ref utf8Bytes);
                blocks.Add(new DocumentBlockProvenance(
                    start,
                    text.Length - start,
                    "ocr",
                    block.Kind,
                    block.Left,
                    block.Top,
                    block.Width,
                    block.Height));
            }

            provenance.Add(new DocumentPageProvenance(
                page.PageIndex,
                pageStart,
                text.Length - pageStart,
                "ocr",
                ocrPage.OrientationDegrees,
                blocks));
        }

        var mergedText = text.ToString();
        EnsureStrictUtf8(mergedText, MaximumTextUtf8Bytes, "document-ocr-output-too-large");
        var metadata = JsonSerializer.Serialize(new DocumentProvenance(MetadataVersion, provenance), JsonOptions);
        EnsureStrictUtf8(metadata, MaximumMetadataUtf8Bytes, "document-ocr-metadata-too-large");
        return new DocumentOcrMergeResult(mergedText, metadata);
    }

    public static DocumentProvenance Parse(string metadataJson)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(metadataJson);
        EnsureStrictUtf8(metadataJson, MaximumMetadataUtf8Bytes, "document-ocr-metadata-too-large");
        var provenance = JsonSerializer.Deserialize<DocumentProvenance>(metadataJson, JsonOptions);
        if (provenance is null || provenance.Version != MetadataVersion || provenance.Pages is null ||
            provenance.Pages.Count == 0 || provenance.Pages.Select(static page => page.PageIndex).Distinct().Count() != provenance.Pages.Count ||
            provenance.Pages.Any(static page => page.PageIndex < 0 || page.StartOffset < 0 || page.Length < 0 ||
                page.Method is not ("native" or "ocr") || page.Blocks is null))
        {
            throw new InvalidOperationException("document-ocr-metadata-invalid");
        }

        return provenance;
    }

    public static string NormaliseTextAndMetadata(
        string text,
        string metadataJson,
        out string normalisedMetadataJson)
    {
        ArgumentNullException.ThrowIfNull(text);
        var provenance = Parse(metadataJson);
        var ordered = provenance.Pages.OrderBy(static page => page.PageIndex).ToArray();
        var normalised = new StringBuilder(text.Length);
        var normalisedPages = new List<DocumentPageProvenance>(ordered.Length);
        foreach (var page in ordered)
        {
            if (page.StartOffset > text.Length || page.Length > text.Length - page.StartOffset)
            {
                throw new InvalidOperationException("document-ocr-metadata-offset-invalid");
            }

            if (normalised.Length > 0)
            {
                normalised.Append('\n').Append('\n');
            }

            var pageStart = normalised.Length;
            var pageText = Normalise(text.Substring(page.StartOffset, page.Length));
            normalised.Append(pageText);
            normalisedPages.Add(page with { StartOffset = pageStart, Length = pageText.Length });
        }

        var normalisedText = normalised.ToString();
        EnsureStrictUtf8(normalisedText, MaximumTextUtf8Bytes, "document-ocr-output-too-large");
        normalisedMetadataJson = JsonSerializer.Serialize(new DocumentProvenance(MetadataVersion, normalisedPages), JsonOptions);
        EnsureStrictUtf8(normalisedMetadataJson, MaximumMetadataUtf8Bytes, "document-ocr-metadata-too-large");
        return normalisedText;
    }

    private static bool IsSupportedKind(string kind) => kind is "text" or "table" or "title" or "header" or "footer" or "figure";

    private static void AppendBounded(StringBuilder builder, string value, ref int utf8Bytes)
    {
        var valueBytes = GetStrictUtf8ByteCount(value);
        if (valueBytes > MaximumTextUtf8Bytes - utf8Bytes)
        {
            throw new InvalidOperationException("document-ocr-output-too-large");
        }

        builder.Append(value);
        utf8Bytes += valueBytes;
    }

    private static void EnsureStrictUtf8(string value, int maximumBytes, string tooLargeReason)
    {
        if (GetStrictUtf8ByteCount(value) > maximumBytes)
        {
            throw new InvalidOperationException(tooLargeReason);
        }
    }

    private static int GetStrictUtf8ByteCount(string value)
    {
        try
        {
            return StrictUtf8.GetByteCount(value);
        }
        catch (EncoderFallbackException exception)
        {
            throw new InvalidOperationException("document-ocr-text-invalid", exception);
        }
    }

    private static string RequireReason(string reasonCode) =>
        string.IsNullOrWhiteSpace(reasonCode) || reasonCode.Length > 128
            ? "document-ocr-result-invalid"
            : reasonCode;

    private static string Normalise(string value) => value
        .Replace("\r\n", "\n", StringComparison.Ordinal)
        .Replace('\r', '\n')
        .Normalize(NormalizationForm.FormKC);
}
