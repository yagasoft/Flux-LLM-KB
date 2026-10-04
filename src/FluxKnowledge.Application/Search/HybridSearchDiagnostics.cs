using System.Diagnostics.Tracing;

namespace FluxKnowledge.Application.Search;

/// <summary>Opt-in EventPipe measurements; no queries, passages, paths or vectors.</summary>
[EventSource(Name = "FluxKnowledge-HybridSearch")]
public sealed class HybridSearchDiagnostics : EventSource
{
    public static readonly HybridSearchDiagnostics Log = new();
    private HybridSearchDiagnostics() { }

    [Event(1, Level = EventLevel.Informational)]
    public void Candidates(string traceId, string spanId, string searchId, string lexicalIds, string denseIds, string shortlistIds) =>
        Emit(1, traceId, spanId, searchId, lexicalIds, denseIds, shortlistIds);

    [Event(2, Level = EventLevel.Informational)]
    public void Completed(string traceId, string spanId, string searchId, string semanticStatus, string resultIds, double elapsedMs) =>
        Emit(2, traceId, spanId, searchId, semanticStatus, resultIds, elapsedMs);

    [Event(3, Level = EventLevel.Informational)]
    public void ModelPhase(string batchId, string model, string phase, double elapsedMs, string outcome) =>
        Emit(3, batchId, model, phase, elapsedMs, outcome);

    [Event(4, Level = EventLevel.Informational)]
    public void NativeSearch(string traceId, string spanId, string batchId) => Emit(4, traceId, spanId, batchId);

    [Event(5, Level = EventLevel.Informational)]
    public void ScopedDensePage(string traceId, string spanId, int rows, int retainedCandidates, long payloadBytes) =>
        Emit(5, traceId, spanId, rows, retainedCandidates, payloadBytes);

    [NonEvent]
    private void Emit(int eventId, params object[] values)
    {
        if (!IsEnabled()) return;
        try { WriteEvent(eventId, values); }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            // Diagnostics must not change results or native release/recovery.
        }
    }
}
