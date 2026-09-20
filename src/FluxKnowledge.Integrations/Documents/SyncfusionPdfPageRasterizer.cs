using FluxKnowledge.Application.Documents;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Sources;
using Syncfusion.PdfToImageConverter;

namespace FluxKnowledge.Integrations.Documents;

/// <summary>
/// Local PDFium-backed rendering of retained PDF bytes. It never supplies a source path,
/// annotation action, URI or external content to the renderer.
/// </summary>
public sealed class SyncfusionPdfPageRasterizer(
    SyncfusionLicenceRegistration licenceRegistration) : IPdfPageRasterizer
{
    public const int MaximumPageCount = SyncfusionPdfDocumentExtractor.MaximumPageCount;
    public const int MaximumPagePngBytes = 16 * 1024 * 1024;
    public const int MaximumTotalPngBytes = 256 * 1024 * 1024;

    public async ValueTask<IReadOnlyList<PdfRasterizedPage>> RenderAsync(
        RetainedSourceBytes retained,
        CancellationToken cancellationToken,
        IReadOnlySet<int>? pageIndexes = null)
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

        licenceRegistration.EnsureRegistered();
        try
        {
            using var input = new MemoryStream(retained.Bytes, writable: false);
            using var converter = new PdfToImageConverter(input)
            {
                ScaleFactor = 2
            };
            if (converter.PageCount > MaximumPageCount)
            {
                throw new RetainedProcessorException("pdf-document-page-limit-exceeded");
            }

            var selectedPages = pageIndexes is null
                ? Enumerable.Range(0, converter.PageCount).ToArray()
                : pageIndexes.Order().ToArray();
            if (selectedPages.Any(pageIndex => pageIndex < 0 || pageIndex >= converter.PageCount))
            {
                throw new RetainedProcessorException("pdf-ocr-page-selection-invalid");
            }

            var pages = new List<PdfRasterizedPage>(selectedPages.Length);
            var totalBytes = 0;
            foreach (var pageIndex in selectedPages)
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var rendered = converter.Convert(pageIndex, keepTransparency: false, isSkipAnnotations: true);
                if (rendered.CanSeek) rendered.Position = 0;
                var png = await ReadBoundedAsync(rendered, cancellationToken).ConfigureAwait(false);
                totalBytes = checked(totalBytes + png.Length);
                if (totalBytes > MaximumTotalPngBytes)
                {
                    throw new RetainedProcessorException("pdf-ocr-raster-output-too-large");
                }

                pages.Add(new PdfRasterizedPage(pageIndex, png));
            }

            return pages.AsReadOnly();
        }
        catch (RetainedProcessorException)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OperationCanceledException and not OutOfMemoryException and not StackOverflowException)
        {
            throw new RetainedProcessorException("pdf-ocr-rasterization-failed");
        }
    }

    private static async ValueTask<byte[]> ReadBoundedAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var output = new MemoryStream();
        var buffer = new byte[64 * 1024];
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            if (output.Length > MaximumPagePngBytes - read)
            {
                throw new RetainedProcessorException("pdf-ocr-raster-page-output-too-large");
            }

            output.Write(buffer, 0, read);
        }

        return output.ToArray();
    }
}
