using FluxKnowledge.Application.Contracts;
using FluxKnowledge.Application.Ports;

namespace FluxKnowledge.Application.Search;

/// <summary>Routes scoped keyword search through expanded ranking and preserves literal/all-scope behaviour.</summary>
public sealed class ExpandedPassageRetrieval(HybridPassageRetrievalEngine engine, IExpandedPassageRanker ranker,
    IScheduledPassageInference queryEmbedding, Func<DateTimeOffset> requestArrival, Action ownershipUnconfirmed) : IHybridPassageRetrieval
{
    public async ValueTask<CorpusSearchResponse> SearchAsync(CorpusSearchRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request); cancellationToken.ThrowIfCancellationRequested();
        if (request.MatchMode != "keyword" || request.Scope is not ("root" or "workspace"))
            return await engine.SearchAsync(request, cancellationToken).ConfigureAwait(false);
        var arrival = requestArrival();
        try
        {
            return (await engine.SearchExpandedAsync(request, ranker, arrival, cancellationToken,
                queryEmbedding: queryEmbedding).ConfigureAwait(false)).Result;
        }
        catch (PassageInferenceOwnershipException error)
        {
            try { ownershipUnconfirmed(); }
            catch (Exception stopFailure) when (stopFailure is not OutOfMemoryException and not StackOverflowException)
            { error.Data["HostStopFailure"] = stopFailure; }
            throw;
        }
    }
}
