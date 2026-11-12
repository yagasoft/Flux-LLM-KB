using System.Diagnostics.Tracing;

namespace FluxKnowledge.Web;

/// <summary>Bounded native intervals, with no input/output text or vectors.</summary>
internal sealed class StagingIndexNativeEvidence(bool gpu=false) : EventListener
{
    private readonly object _sync=new();
    private readonly HashSet<string> _owned=[];
    private readonly Dictionary<string,(DateTimeOffset Started,string Model)> _active=[];
    private readonly List<StagingIndexNativeInterval> _settled=[];
    private bool _invalid;
    internal void Register(string id)
    {
        lock(_sync)
        {
            if(_owned.Count>=2048 || !_owned.Add(id))throw new InvalidOperationException("index-native-proof-bound");
        }
    }
    protected override void OnEventSourceCreated(EventSource source)
    {
        if(source.Name=="FluxKnowledge-HybridSearch")EnableEvents(source,EventLevel.Verbose);
    }
    protected override void OnEventWritten(EventWrittenEventArgs e)
    {
        if(e.EventName!="ModelPhase" || e.Payload is null || e.Payload.Count<5 || _sync is null)return;
        var id=e.Payload[0] as string;
        var model=e.Payload[1] as string;
        if(id is null || (gpu ? model is not ("gpu-embedding" or "gpu-reranker") : model!="cpu-embedding") || e.Payload[2] as string!="native-run")return;
        lock(_sync)
        {
            if(!gpu && !_owned.Contains(id))return;
            var key=gpu?model+":"+id:id;
            var now=new DateTimeOffset(DateTime.SpecifyKind(e.TimeStamp,DateTimeKind.Utc));
            if(e.Payload[4] as string=="begin")
            {if(gpu && _active.Count!=0)_invalid=true;if(!_active.TryAdd(key,(now,model!)))_invalid=true;}
            else if(e.Payload[4] as string=="settled")
            {
                if(!_active.Remove(key,out var start) || _settled.Count>=2048){_invalid=true;return;}
                _settled.Add(new(id,start.Started,now,Convert.ToDouble(e.Payload[3],System.Globalization.CultureInfo.InvariantCulture),start.Model));
            }
        }
    }
    internal object Snapshot()
    {
        lock(_sync)return new {registered=_owned.Count,settled=_settled.Count,active=_active.Count,invalid=_invalid,intervals=_settled.ToArray()};
    }
}
internal sealed record StagingIndexNativeInterval(string Id,DateTimeOffset StartedUtc,DateTimeOffset SettledUtc,double Milliseconds,string Model="cpu-embedding");
