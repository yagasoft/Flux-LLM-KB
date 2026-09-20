using FluxKnowledge.Application.Ports;

namespace FluxKnowledge.Application.Documents;

/// <summary>Extracts interpreted Visio content from verified retained VSDX bytes only.</summary>
public interface IVisioDocumentExtractor
{
    /// <summary>Returns a stable safe reason when this interactive-machine capability is unavailable.</summary>
    string? GetUnavailableReason();

    ValueTask<VisioDocumentResult> ExtractAsync(
        RetainedSourceBytes retained,
        CancellationToken cancellationToken);
}

/// <summary>Bounded Visio extraction output, including local execution evidence.</summary>
public sealed record VisioDocumentResult(
    DocumentExtractionResult Extraction,
    string MetadataJson,
    int ShapeCount,
    int ConnectionCount,
    long ElapsedMilliseconds,
    long PrivateBytes);
