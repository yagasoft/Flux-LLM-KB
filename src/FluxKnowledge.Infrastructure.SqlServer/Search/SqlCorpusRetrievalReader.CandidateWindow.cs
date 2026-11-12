using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Pipeline;
using FluxKnowledge.Application.Search;
using System.Diagnostics;

namespace FluxKnowledge.Infrastructure.SqlServer.Search;

public sealed partial class SqlCorpusRetrievalReader : ICorpusDenseCandidateWindowReader
{
    public async ValueTask<DenseCandidateWindow> ReadDenseCandidateWindowAsync(ICorpusAnnLease lease,
        ResolvedCorpusScope scope, IReadOnlyList<float> query, CancellationToken cancellationToken)
    {
        var generation = ValidateDenseRequest(lease, scope, query);
        if (generation.FormatVersion != 2 || scope.Kind is not ("root" or "workspace") || lease is not IAnnCandidatePool expanded)
            throw new PassageRetrievalRefusalException("unavailable");
        if (!lease.IsPrepared(this)) throw new PassageRetrievalRefusalException("warming");
        await RequireReadable();
        var live = await Observe("quality-live-bindings", () => ReadLiveBindingKeysAsync(lease, scope, cancellationToken));
        var selection = new CachedScopeSelection(this, generation, live);
        // Preserve the actual old call. A larger approximate request need not share its prefix.
        var baselineMatches = await Observe("quality-ann-baseline", () => lease.SearchAsync(query, 100, scope, selection, cancellationToken));
        ValidateMatches(baselineMatches, 100);
        await RequireReadable();
        var poolMatches = await Observe("quality-ann-pool", () => expanded.SearchCandidatePoolAsync(query, IAnnCandidatePool.MaximumResults, scope, selection, cancellationToken));
        ValidateMatches(poolMatches, IAnnCandidatePool.MaximumResults);
        cancellationToken.ThrowIfCancellationRequested();
        await RequireReadable();
        var baseline = await HydrateIndexedMatchesAsync(lease, scope, baselineMatches, "quality-baseline", cancellationToken);
        var pool = await HydrateIndexedMatchesAsync(lease, scope, poolMatches, "quality-pool", cancellationToken, candidatePool: true);
        if (baseline.Status != "ready" || pool.Status != "ready") throw new PublicationSnapshotConflictException("The captured candidate window became unreadable.");
        PassageRanking.ValidateExpandedPopulation([], baseline.Candidates, pool.Candidates);
        await RequireReadable();
        return new("ready", baseline.Candidates, pool.Candidates)
        { BaselineRawMatches = baselineMatches.Count, PoolRawMatches = poolMatches.Count };

        async ValueTask RequireReadable() => _ = await Observe("quality-readability", async () =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!await lease.IsReadableAsync(cancellationToken)) throw new PublicationSnapshotConflictException("The captured manifest is unreadable.");
            return true;
        });

        static async ValueTask<T> Observe<T>(string phase, Func<ValueTask<T>> work)
        {
            var started = Stopwatch.GetTimestamp(); var outcome = "failed";
            try { var result = await work().ConfigureAwait(false); outcome = "completed"; return result; }
            catch (OperationCanceledException) { outcome = "cancelled"; throw; }
            finally
            {
                if (HybridSearchDiagnostics.Log.IsEnabled())
                {
                    var correlation = HybridSearchDiagnostics.CurrentCorrelation;
                    HybridSearchDiagnostics.Log.Phase(correlation.TraceId, correlation.SpanId, string.Empty, phase,
                        outcome, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                }
            }
        }
    }

    private static void ValidateMatches(IReadOnlyList<AnnMatch> matches, int maximum)
    {
        if (matches.Count > maximum || matches.Any(p => p.VectorId <= 0 || !float.IsFinite(p.Distance)) ||
            matches.Select(p => p.VectorId).Distinct().Count() != matches.Count)
            throw new InvalidOperationException("corpus-dense-output-invalid");
    }
}
