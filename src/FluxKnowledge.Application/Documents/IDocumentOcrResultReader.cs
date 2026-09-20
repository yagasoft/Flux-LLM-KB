using FluxKnowledge.Application.Ports;

namespace FluxKnowledge.Application.Documents;

/// <summary>
/// Reads a locally completed, source-bound OCR result before the original Extract job resumes.
/// Implementations must return no result for a different job, content hash or non-terminal OCR
/// request; this boundary never reads a provider or a model.
/// </summary>
public interface IDocumentOcrResultReader
{
    ValueTask<DocumentOcrExecutionResult?> ReadCompletedAsync(
        ClaimedJob parentJob,
        string contentSha256,
        CancellationToken cancellationToken);
}

public sealed class EmptyDocumentOcrResultReader : IDocumentOcrResultReader
{
    public ValueTask<DocumentOcrExecutionResult?> ReadCompletedAsync(
        ClaimedJob parentJob,
        string contentSha256,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(parentJob);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentSha256);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<DocumentOcrExecutionResult?>(null);
    }
}
