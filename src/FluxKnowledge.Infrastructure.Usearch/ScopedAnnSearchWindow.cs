using FluxKnowledge.Application.Ports;

namespace FluxKnowledge.Infrastructure.Usearch;

/// <summary>At most two synchronous native callbacks; actual settlement precedes view release.</summary>
internal static class ScopedAnnSearchWindow
{
    internal static async Task<IReadOnlyList<AnnMatch>[]> RunAsync(Func<ValueTask<IReadOnlyList<AnnMatch>>> first,
        Func<ValueTask<IReadOnlyList<AnnMatch>>>? second=null)
    {
        // Never cancel this join or queue every segment eagerly. Each callback observes the request token itself.
        var tasks=second is null
            ? new[]{Task.Run(async()=>await first().ConfigureAwait(false))}
            : new[]{Task.Run(async()=>await first().ConfigureAwait(false)),Task.Run(async()=>await second().ConfigureAwait(false))};
        var completed=Task.WhenAll(tasks);
        try{return await completed.ConfigureAwait(false);}
        catch
        {
            if(completed.Exception is {InnerExceptions.Count:>1} failures)throw failures;
            throw;
        }
    }
}
