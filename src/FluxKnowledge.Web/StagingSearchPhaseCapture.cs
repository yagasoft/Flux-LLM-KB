using System.Diagnostics.Tracing;

namespace FluxKnowledge.Web;

/// <summary>One bounded staging observation, containing timings and correlation only.</summary>
internal sealed class StagingSearchPhaseCapture(int maximumEvents = 4096) : EventListener
{
    private readonly object _sync = new();
    private readonly Dictionary<(string Trace,string Span,string Phase,string Outcome),StagingSearchPhase> _events = [];
    private int _dropped;
    protected override void OnEventSourceCreated(EventSource source)
    { if (source.Name == "FluxKnowledge-HybridSearch") EnableEvents(source, EventLevel.Informational); }
    protected override void OnEventWritten(EventWrittenEventArgs item)
    {
        if (item.EventId != 6 || item.Payload is not { Count: >= 6 } p || _sync is null) return;
        lock (_sync)
        {
            var key=(p[0] as string ?? "",p[1] as string ?? "",p[3] as string ?? "",p[4] as string ?? "");
            var milliseconds=Convert.ToDouble(p[5],System.Globalization.CultureInfo.InvariantCulture);
            if(_events.TryGetValue(key,out var previous))
                _events[key]=previous with {Milliseconds=previous.Milliseconds+milliseconds,Count=previous.Count+1,MaximumMilliseconds=Math.Max(previous.MaximumMilliseconds,milliseconds)};
            else if (_events.Count >= maximumEvents) _dropped++;
            else _events.Add(key,new(key.Item1,key.Item2,key.Item3,key.Item4,milliseconds,
                new DateTimeOffset(DateTime.SpecifyKind(item.TimeStamp, DateTimeKind.Utc)),1,milliseconds));
        }
    }
    internal StagingSearchPhaseSnapshot Snapshot() { lock (_sync) return new(_events.Values.ToArray(), _dropped); }
}
internal sealed record StagingSearchPhase(string TraceId, string SpanId, string Phase, string Outcome, double Milliseconds, DateTimeOffset Utc,int Count,double MaximumMilliseconds);
internal sealed record StagingSearchPhaseSnapshot(StagingSearchPhase[] Events, int Dropped);
