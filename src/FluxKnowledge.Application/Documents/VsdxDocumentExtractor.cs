using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Sources;

namespace FluxKnowledge.Application.Documents;

/// <summary>
/// Bounded VSDX text extraction for an already verified retained document input.
/// It never exposes package members or a source path.
/// </summary>
public sealed class VsdxDocumentExtractor
{
    public async ValueTask<DocumentExtractionResult> ExtractAsync(
        RetainedSourceBytes retained,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(retained);
        return await OoxmlStructuralTextProcessor
            .ExtractVsdxAsync(retained.Bytes, cancellationToken)
            .ConfigureAwait(false);
    }
}
