using FluxKnowledge.Application.Ports;
using FluxKnowledge.Domain.Jobs;
using FluxKnowledge.Domain.Sources;

namespace FluxKnowledge.Application.Documents;

/// <summary>
/// Queues OCR for the explicitly uncovered pages of one retained PDF.  It owns no provider
/// selection and carries no source bytes, so document extraction keeps its existing lease and
/// source-identity boundary while the local GPU scheduler owns execution.
/// </summary>
public interface IDocumentOcrHandoff
{
    ValueTask<DocumentOcrHandoffResult> HandoffAsync(
        DocumentOcrHandoffRequest request,
        CancellationToken cancellationToken);
}

public sealed class DocumentOcrHandoffRequest
{
    public DocumentOcrHandoffRequest(
        ClaimedJob parentJob,
        SourceRevisionId retainedSourceRevisionId,
        string contentSha256,
        IReadOnlyList<int> pageIndexes)
    {
        ArgumentNullException.ThrowIfNull(parentJob);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentSha256);
        ArgumentNullException.ThrowIfNull(pageIndexes);
        if (pageIndexes.Count == 0 || pageIndexes.Any(static page => page < 0) ||
            pageIndexes.Distinct().Count() != pageIndexes.Count)
        {
            throw new ArgumentException("OCR hand-off requires distinct non-negative page indexes.", nameof(pageIndexes));
        }

        ParentJob = parentJob;
        RetainedSourceRevisionId = retainedSourceRevisionId;
        ContentSha256 = contentSha256;
        PageIndexes = pageIndexes.Order().ToArray();
    }

    public ClaimedJob ParentJob { get; }

    public SourceRevisionId RetainedSourceRevisionId { get; }

    public string ContentSha256 { get; }

    public IReadOnlyList<int> PageIndexes { get; }
}

public sealed record DocumentOcrHandoffResult(
    bool Scheduled,
    string ReasonCode,
    Guid? MiniTaskId)
{
    public static DocumentOcrHandoffResult Queued(Guid miniTaskId) =>
        miniTaskId == Guid.Empty
            ? throw new ArgumentException("An OCR hand-off requires a mini-task ID.", nameof(miniTaskId))
            : new(true, "document-ocr-queued", miniTaskId);

    public static DocumentOcrHandoffResult Refused(string reasonCode) =>
        new(false, string.IsNullOrWhiteSpace(reasonCode) ? "pdf-ocr-required" : reasonCode, null);
}

/// <summary>Safe composition default until the fixed local OCR executor is explicitly enabled.</summary>
public sealed class UnavailableDocumentOcrHandoff : IDocumentOcrHandoff
{
    public ValueTask<DocumentOcrHandoffResult> HandoffAsync(
        DocumentOcrHandoffRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(DocumentOcrHandoffResult.Refused("pdf-ocr-required"));
    }
}
