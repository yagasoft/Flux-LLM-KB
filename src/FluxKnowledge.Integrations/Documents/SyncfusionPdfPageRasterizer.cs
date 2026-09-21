using FluxKnowledge.Application.Documents;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Sources;
using SkiaSharp;
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
            return RenderImage(retained, pageIndexes, cancellationToken);
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

    private static IReadOnlyList<PdfRasterizedPage> RenderImage(
        RetainedSourceBytes retained,
        IReadOnlySet<int>? pageIndexes,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (retained.ByteLength > ImageDocumentInputProcessor.MaximumInputBytes ||
            pageIndexes is not null && (pageIndexes.Count != 1 || !pageIndexes.Contains(0)))
            throw new RetainedProcessorException("image-ocr-page-selection-invalid");
        var bytes = retained.Bytes.AsSpan();
        if (!bytes.StartsWith(new byte[] { 0xff, 0xd8, 0xff }) &&
            !bytes.StartsWith(new byte[] { 0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a }))
            throw new RetainedProcessorException("image-document-container-invalid");
        try
        {
            using var input = new MemoryStream(retained.Bytes, writable: false);
            using var codec = SKCodec.Create(input) ?? throw new RetainedProcessorException("image-document-container-invalid");
            if (codec.FrameCount > 1)
                throw new RetainedProcessorException("image-document-multiframe-unsupported");
            var info = codec.Info;
            if (info.Width <= 0 || info.Height <= 0 || info.Width > 6000 || info.Height > 6000 ||
                (long)info.Width * info.Height > 25_000_000)
                throw new RetainedProcessorException("image-document-dimensions-exceeded");

            using var decoded = new SKBitmap(new SKImageInfo(info.Width, info.Height, SKColorType.Bgra8888, SKAlphaType.Premul));
            if (codec.GetPixels(decoded.Info, decoded.GetPixels()) != SKCodecResult.Success)
                throw new RetainedProcessorException("image-document-decode-failed");

            var (sourceOrientationDegrees, sourceTransform, swapsAxes) = codec.EncodedOrigin switch
            {
                SKEncodedOrigin.TopLeft => (0, "identity", false),
                SKEncodedOrigin.RightTop => (90, "rotate-90", true),
                SKEncodedOrigin.BottomRight => (180, "rotate-180", false),
                SKEncodedOrigin.LeftBottom => (270, "rotate-270", true),
                _ => throw new RetainedProcessorException("image-document-orientation-unsupported")
            };
            using var oriented = new SKBitmap(
                swapsAxes ? info.Height : info.Width,
                swapsAxes ? info.Width : info.Height,
                SKColorType.Bgra8888,
                SKAlphaType.Premul);
            using (var canvas = new SKCanvas(oriented))
            {
                ApplyOrientation(canvas, codec.EncodedOrigin, info.Width, info.Height);
                canvas.DrawBitmap(decoded, 0, 0);
                canvas.Flush();
            }
            using var image = SKImage.FromBitmap(oriented);
            using var encoded = image.Encode(SKEncodedImageFormat.Png, 100) ??
                throw new RetainedProcessorException("image-ocr-rasterization-failed");
            var png = encoded.ToArray();
            if (png.Length == 0 || png.Length > MaximumPagePngBytes)
                throw new RetainedProcessorException("image-ocr-raster-page-output-too-large");
            return [new PdfRasterizedPage(0, png, sourceOrientationDegrees, info.Width, info.Height, sourceTransform)];
        }
        catch (RetainedProcessorException)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OperationCanceledException and not OutOfMemoryException and not StackOverflowException)
        {
            throw new RetainedProcessorException("image-ocr-rasterization-failed");
        }
    }

    private static void ApplyOrientation(SKCanvas canvas, SKEncodedOrigin origin, int width, int height)
    {
        switch (origin)
        {
            case SKEncodedOrigin.BottomRight:
                canvas.Translate(width, height); canvas.RotateDegrees(180); break;
            case SKEncodedOrigin.RightTop:
                canvas.Translate(height, 0); canvas.RotateDegrees(90); break;
            case SKEncodedOrigin.LeftBottom:
                canvas.Translate(0, width); canvas.RotateDegrees(-90); break;
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
