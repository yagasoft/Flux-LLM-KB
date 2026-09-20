using FluxKnowledge.Application.Ports;

namespace FluxKnowledge.Application.Documents;

/// <summary>Extracts native PDF text from checksum-verified retained bytes only.</summary>
public interface IPdfDocumentExtractor
{
    ValueTask<DocumentExtractionResult> ExtractAsync(
        RetainedSourceBytes retained,
        CancellationToken cancellationToken);
}
