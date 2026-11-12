using System.Diagnostics;
using System.Diagnostics.Tracing;

namespace FluxKnowledge.Application.Search;

/// <summary>Opt-in EventPipe measurements; no queries, passages, paths or vectors.</summary>
[EventSource(Name = "FluxKnowledge-HybridSearch")]
public sealed class HybridSearchDiagnostics : EventSource
{
    public static readonly HybridSearchDiagnostics Log = new();
    private static readonly AsyncLocal<(string TraceId, string SpanId)?> RequestCorrelation = new();
    private HybridSearchDiagnostics() { }

    // Null inherits ambient attribution; an empty pair suppresses an unrelated scheduler trace.
    public static (string TraceId, string SpanId) CurrentCorrelation => RequestCorrelation.Value ??
        (Activity.Current?.TraceId.ToString() ?? string.Empty, Activity.Current?.SpanId.ToString() ?? string.Empty);

    [NonEvent]
    public static async ValueTask<T> WithRequestCorrelationAsync<T>(string traceId, string spanId, Func<ValueTask<T>> work)
    {
        var previous = RequestCorrelation.Value;
        RequestCorrelation.Value = (traceId, spanId);
        try { return await work().ConfigureAwait(false); }
        finally { RequestCorrelation.Value = previous; }
    }

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

    [Event(6, Level = EventLevel.Informational)]
    public void Phase(string traceId, string spanId, string searchId, string phase, string outcome, double elapsedMs) =>
        Emit(6, traceId, spanId, searchId, phase, outcome, elapsedMs);

    [Event(7, Level = EventLevel.Informational)]
    public void LeasePhase(string traceId, string spanId, string leaseAttemptId, string phase, string outcome, double elapsedMs) =>
        Emit(7, traceId, spanId, leaseAttemptId, phase, outcome, elapsedMs);

    [Event(8, Level = EventLevel.Informational)]
    public void CheckpointRead(string probeId, int draftCount, long rows, double elapsedMs, string outcome) =>
        Emit(8, probeId, draftCount, rows, elapsedMs, outcome);

    [Event(9, Level = EventLevel.Informational)]
    public void ScopedAnnSegment(string traceId, string spanId, string segmentId, int eligibleKeys, int matches,
        double selectionMs, double searchMs) => Emit(9, traceId, spanId, segmentId, eligibleKeys, matches, selectionMs, searchMs);

    [Event(10, Level = EventLevel.Informational)]
    public void SegmentView(string traceId, string spanId, string segmentId, long fileBytes, bool reused, double elapsedMs) =>
        Emit(10, traceId, spanId, segmentId, fileBytes, reused, elapsedMs);

    [Event(11, Level = EventLevel.Informational)]
    public void InputGroups(string traceId, string spanId, string searchId, int rawCandidates, int distinctInputs,
        int shortlistedInputs, int rerankerPairs) => Emit(11, traceId, spanId, searchId, rawCandidates, distinctInputs, shortlistedInputs, rerankerPairs);

    [Event(12, Level = EventLevel.Informational)]
    public void SourceDirected(string traceId, string spanId, string searchId, string status, string candidateIds) =>
        Emit(12, traceId, spanId, searchId, status, candidateIds);

    /// <summary>Shared prepared-view validation/wait, measured once per batch; per-view reuse reports only bookkeeping.</summary>
    [Event(13, Level = EventLevel.Informational)]
    public void PreparedBorrowBatch(string traceId, string spanId, int segments, double elapsedMs) =>
        Emit(13, traceId, spanId, segments, elapsedMs);

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
