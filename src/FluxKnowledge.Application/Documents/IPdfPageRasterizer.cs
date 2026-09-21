using FluxKnowledge.Application.Ports;

namespace FluxKnowledge.Application.Documents;

/// <summary>Renders checksum-verified retained PDF or single-image bytes into bounded page PNGs for local OCR.</summary>
public interface IPdfPageRasterizer
{
    ValueTask<IReadOnlyList<PdfRasterizedPage>> RenderAsync(
        RetainedSourceBytes retained,
        CancellationToken cancellationToken,
        IReadOnlySet<int>? pageIndexes = null);
}

public sealed record PdfRasterizedPage(
    int PageIndex,
    byte[] PngBytes,
    int SourceOrientationDegrees = 0,
    int? SourceWidth = null,
    int? SourceHeight = null,
    string? SourceTransform = null);
