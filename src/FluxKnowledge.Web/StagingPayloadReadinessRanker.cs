using FluxKnowledge.Application.Ports;

namespace FluxKnowledge.Web;

/// <summary>Private timing observation: captures a verified submission, refuses scoring and never creates GPU work.</summary>
internal sealed class StagingPayloadReadinessRanker(TimeProvider clock) : IExpandedPassageRanker
{
    private readonly object _sync=new();
    private readonly List<StagingPayloadSubmission> _observations=[];
    private int _reserved;
    public string RerankerFingerprint => "ranking-not-requested";
    internal StagingPayloadSubmission[] Snapshot(){lock(_sync)return _observations.ToArray();}

    public IExpandedPassageRankTicket Reserve(DateTimeOffset arrival,string traceId,string spanId,CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if(Interlocked.Exchange(ref _reserved,1)!=0)throw new PassageRetrievalRefusalException("busy");
        if(clock.GetUtcNow()>=arrival+TimeSpan.FromSeconds(6))
        {
            Record(arrival,[],true,"reservation-expired");
            throw Refusal("payload-admission-expired");
        }
        return new Ticket(this,arrival,ct);
    }

    private void Record(DateTimeOffset arrival,IReadOnlyList<long> ids,bool late,string stage)
    {
        lock(_sync)_observations.Add(new((clock.GetUtcNow()-arrival).TotalMilliseconds,ids.ToArray(),late,stage,false,false));
    }
    private static PassageRetrievalRefusalException Refusal(string phase)
    {
        var failure=new PassageRetrievalRefusalException(phase=="diagnostic-ranking-not-requested"?"unavailable":"timeout");
        failure.Data["staging.rank.phase"]=phase;return failure;
    }

    private sealed class Ticket(StagingPayloadReadinessRanker capture,DateTimeOffset arrival,CancellationToken ct) : IExpandedPassageRankTicket
    {
        private int _submitted;
        public Guid ClientId {get;}=Guid.NewGuid();
        public Guid CohortId {get;}=Guid.NewGuid();
        public ValueTask<RerankResult> SubmitAsync(string query,IReadOnlyList<RerankPassage> passages)
        {
            if(Interlocked.Exchange(ref _submitted,1)!=0)throw new InvalidOperationException("diagnostic-payload-already-submitted");
            if(string.IsNullOrWhiteSpace(query)||query.Length>2048||passages.Count is <1 or >100||
                passages.Any(p => p is null||p.PassageId<=0||string.IsNullOrWhiteSpace(p.SearchText)||p.SearchText.Length>16384)||
                passages.Select(p => p.PassageId).Distinct().Count()!=passages.Count)throw new ArgumentException("diagnostic-payload-invalid");
            var late=capture.Now>=arrival+TimeSpan.FromSeconds(6);
            // Record the attempt even when it is late or its caller has just cancelled.
            capture.Record(arrival,passages.Select(p => p.PassageId).ToArray(),late,"submit");
            ct.ThrowIfCancellationRequested();
            return ValueTask.FromException<RerankResult>(Refusal(late?"payload-readiness":"diagnostic-ranking-not-requested"));
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private DateTimeOffset Now => clock.GetUtcNow();
}

internal sealed record StagingPayloadSubmission(double SubmittedMilliseconds,IReadOnlyList<long> PassageIds,
    bool AfterReadinessCutoff,string Stage,bool GpuExecuted,bool ScoresReturned);
