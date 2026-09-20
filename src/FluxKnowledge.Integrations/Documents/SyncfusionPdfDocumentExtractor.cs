using System.Text;
using FluxKnowledge.Application.Documents;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Sources;
using SkiaSharp;
using Syncfusion.Pdf;
using Syncfusion.Pdf.Parsing;
using Syncfusion.PdfToImageConverter;

namespace FluxKnowledge.Integrations.Documents;

/// <summary>
/// Native-only PDF text extraction. It opens no path, URL, attachment, action or
/// password: the only input is verified app-owned retained bytes.
/// </summary>
public sealed class SyncfusionPdfDocumentExtractor : IPdfDocumentExtractor
{
    public const int MaximumPageCount = 500;
    public const int MaximumExtractedUtf8Bytes = 16 * 1024 * 1024;

    private readonly SyncfusionLicenceRegistration _licenceRegistration;
    private readonly Func<int, bool>? _visibleInkOverride;

    public SyncfusionPdfDocumentExtractor(SyncfusionLicenceRegistration licenceRegistration)
    {
        _licenceRegistration = licenceRegistration;
    }

    internal SyncfusionPdfDocumentExtractor(
        SyncfusionLicenceRegistration licenceRegistration,
        Func<int, bool> visibleInkOverride)
    {
        _licenceRegistration = licenceRegistration;
        _visibleInkOverride = visibleInkOverride;
    }

    public ValueTask<DocumentExtractionResult> ExtractAsync(
        RetainedSourceBytes retained,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(retained);
        cancellationToken.ThrowIfCancellationRequested();
        if (retained.ByteLength > PdfDocumentProcessor.MaximumInputBytes || retained.Bytes.Length != retained.ByteLength)
        {
            throw new RetainedProcessorException("pdf-document-input-too-large");
        }
        if (!retained.Bytes.AsSpan().StartsWith("%PDF-"u8))
        {
            throw new RetainedProcessorException("pdf-document-container-invalid");
        }

        _licenceRegistration.EnsureRegistered();
        MemoryStream? visualInput = null;
        PdfToImageConverter? visualConverter = null;
        try
        {
            using var document = new PdfLoadedDocument(retained.Bytes);
            if (document.IsEncrypted)
            {
                throw new RetainedProcessorException("pdf-document-encrypted");
            }
            if (document.PageCount > MaximumPageCount)
            {
                throw new RetainedProcessorException("pdf-document-page-limit-exceeded");
            }

            cancellationToken.ThrowIfCancellationRequested();
            var text = new StringBuilder();
            var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
            var extractedUtf8Bytes = 0;
            var hasUncoveredPage = false;
            var pages = new List<DocumentExtractedPage>(document.PageCount);
            for (var pageIndex = 0; pageIndex < document.PageCount; pageIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var page = document.Pages[pageIndex];
                var pageText = page.ExtractText();
                var requiresOcr = string.IsNullOrWhiteSpace(pageText) &&
                    (!page.IsBlank || (_visibleInkOverride?.Invoke(pageIndex) ?? HasVisibleInk(GetVisualConverter(), pageIndex)));
                if (!string.IsNullOrWhiteSpace(pageText))
                {
                    var addition = text.Length == 0 ? pageText : Environment.NewLine + pageText;
                    try
                    {
                        extractedUtf8Bytes = checked(extractedUtf8Bytes + utf8.GetByteCount(addition));
                        if (extractedUtf8Bytes > MaximumExtractedUtf8Bytes)
                        {
                            throw new RetainedProcessorException("pdf-document-output-too-large");
                        }
                    }
                    catch (EncoderFallbackException)
                    {
                        throw new RetainedProcessorException("pdf-document-text-not-utf8");
                    }
                    text.Append(addition);
                }
                else if (requiresOcr)
                {
                    hasUncoveredPage = true;
                }
                pages.Add(new DocumentExtractedPage(pageIndex, pageText ?? string.Empty, requiresOcr));
            }
            var extractedText = text.ToString();
            if (hasUncoveredPage)
            {
                return ValueTask.FromResult(new DocumentExtractionResult(extractedText, IsComplete: false, ["pdf-ocr-required"], pages));
            }
            if (string.IsNullOrWhiteSpace(extractedText))
            {
                return ValueTask.FromResult(new DocumentExtractionResult(string.Empty, IsComplete: false, ["pdf-no-extractable-text"], pages));
            }

            return ValueTask.FromResult(new DocumentExtractionResult(extractedText, IsComplete: true, [], pages));

            PdfToImageConverter GetVisualConverter()
            {
                if (visualConverter is not null)
                {
                    return visualConverter;
                }

                visualInput = new MemoryStream(retained.Bytes, writable: false);
                visualConverter = new PdfToImageConverter(visualInput)
                {
                    ScaleFactor = 0.2F
                };
                return visualConverter;
            }
        }
        catch (RetainedProcessorException)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OperationCanceledException and not OutOfMemoryException and not StackOverflowException)
        {
            throw new RetainedProcessorException("pdf-document-container-invalid");
        }
        finally
        {
            visualConverter?.Dispose();
            visualInput?.Dispose();
        }
    }

    private static bool HasVisibleInk(PdfToImageConverter converter, int pageIndex)
    {
        using var rendered = converter.Convert(
            pageIndex,
            keepTransparency: false,
            isSkipAnnotations: true);
        if (rendered.CanSeek)
        {
            rendered.Position = 0;
        }
        using var bitmap = SKBitmap.Decode(rendered)
            ?? throw new RetainedProcessorException("pdf-document-container-invalid");

        for (var y = 0; y < bitmap.Height; y++)
        {
            for (var x = 0; x < bitmap.Width; x++)
            {
                var pixel = bitmap.GetPixel(x, y);
                if (pixel.Red != byte.MaxValue || pixel.Green != byte.MaxValue || pixel.Blue != byte.MaxValue)
                {
                    return true;
                }
            }
        }

        return false;
    }
}
