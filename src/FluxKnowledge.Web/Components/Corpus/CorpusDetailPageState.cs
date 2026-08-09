using FluxKnowledge.Application.Contracts;
using FluxKnowledge.Application.Ports;

namespace FluxKnowledge.Web.Components.Corpus;

public sealed class CorpusDetailPageState(ICorpusProjectionReader reader)
{
    private long _loadGeneration;
    public CorpusEntryDetail? Detail { get; private set; }
    public string? Error { get; private set; }
    public async ValueTask LoadAsync(Guid pipelineRecordId, CancellationToken cancellationToken)
    {
        var generation = Interlocked.Increment(ref _loadGeneration);
        try
        {
            var detail = await reader.ReadDetailAsync(pipelineRecordId, cancellationToken).ConfigureAwait(false);
            if (generation != Volatile.Read(ref _loadGeneration)) return;
            Detail = detail;
            Error = null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception) when (generation == Volatile.Read(ref _loadGeneration)) { Error = "The Corpus entry could not be loaded."; }
    }
}
