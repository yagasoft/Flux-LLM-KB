using FluxKnowledge.Application.Ports;

namespace FluxKnowledge.Application.Documents;

/// <summary>
/// Runs the fixed local document OCR implementation for explicitly selected retained PDF pages.
/// Implementations receive bytes only and cannot choose a source, provider, or acquisition path.
/// </summary>
public interface IDocumentOcrExecutor
{
    ValueTask<DocumentOcrExecutionResult> ExecuteAsync(
        RetainedSourceBytes retained,
        IReadOnlyList<int> pageIndexes,
        CancellationToken cancellationToken);
}
