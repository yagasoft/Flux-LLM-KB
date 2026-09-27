using System.Collections.Concurrent;
using System.Diagnostics.Tracing;

namespace FluxKnowledge.Integration.Tests.Support;

internal sealed class HybridSearchTraceListener : EventListener
{
    private readonly ConcurrentQueue<TraceEntry> _events = new();
    internal IReadOnlyList<TraceEntry> Events => _events.ToArray();
    protected override void OnEventSourceCreated(EventSource eventSource)
    {
        if (eventSource.Name == "FluxKnowledge-HybridSearch") EnableEvents(eventSource, EventLevel.Informational);
    }
    protected override void OnEventWritten(EventWrittenEventArgs eventData) =>
        _events?.Enqueue(new(eventData.EventId, eventData.PayloadNames?.ToArray() ?? [], eventData.Payload?.ToArray() ?? []));
    internal sealed record TraceEntry(int Id, string[] Names, object?[] Values)
    {
        internal object? this[string name] => Values[Array.IndexOf(Names, name)];
    }
}
