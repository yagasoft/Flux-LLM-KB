using FluxKnowledge.Application.Contracts;
using FluxKnowledge.Application.IntegrationV1;
using FluxKnowledge.Application.Pipeline;
using FluxKnowledge.Application.Ports;
using System.Text;

namespace FluxKnowledge.Application.Search;

public sealed partial class HybridPassageRetrievalEngine
{
    /// <summary>Explicit staging-only quality experiment; not a serving deadline or model contract.</summary>
    internal async ValueTask<CandidateQualityResult> SearchCandidateQualityAsync(CorpusSearchRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var query = request.Query?.Trim().Normalize(NormalizationForm.FormC);
        if (string.IsNullOrWhiteSpace(query) || query.Length > 2048 || request.Limit is < 1 or > 50 ||
            request.MatchMode != "keyword" || request.Scope is not ("root" or "workspace") ||
            request.Scope == "root" && (!request.RootId.HasValue || request.Cwd is not null) ||
            request.Scope == "workspace" && (request.RootId.HasValue || string.IsNullOrWhiteSpace(request.Cwd)))
            throw new NativeOperationException("invalid-request");
        if (candidates is not ICorpusDenseCandidateWindowReader windowReader) throw new PassageRetrievalRefusalException("unavailable");
        // This method is only mapped by the explicitly enabled CPU staging diagnostic.
        // Normal search continues to use its existing 50-input and 25/20/3-second limits.
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(120), _clock);
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        var ct = budget.Token;
        var started = _clock.GetTimestamp();
        try
        {
            ct.ThrowIfCancellationRequested();
            var scope = await reader.ResolveScopeAsync(request.Scope, request.RootId, request.Cwd, ct)
                ?? throw new NativeOperationException("scope-unavailable");
            var readiness = await reader.GetLexicalReadinessAsync(ct);
            if (!readiness.IndexPresent) throw new NativeOperationException("lexical-unavailable");
            var sql = await leases.TryAcquireReadableAsync(_instance, owner.Current, inference.EmbeddingProfile.ModelFingerprint,
                inference.EmbeddingProfile.Dimensions, ct) ?? throw new PassageRetrievalRefusalException("index-updating");
            await using var lease = await annFactory.OpenAsync(sql, ct);
            if (lease.Generation.FormatVersion != 2) throw new PassageRetrievalRefusalException("unavailable");
            await RequireReadable();
            if (candidates is ICorpusAnnBindingPreparation preparation && !lease.IsPrepared(preparation))
                await WaitForPreparationAsync(lease, preparation, ct);
            var scopeAndLeaseMs = _clock.GetElapsedTime(started).TotalMilliseconds;
            var phaseStarted = _clock.GetTimestamp();
            var vector = await RunModelAsync((embedding, _, modelToken) => embedding.CreateEmbeddingAsync(query, modelToken), ct);
            if (vector.ModelFingerprint != inference.EmbeddingProfile.ModelFingerprint || vector.Values.Count != inference.EmbeddingProfile.Dimensions ||
                vector.Values.Any(value => !float.IsFinite(value)) || Math.Abs(vector.Values.Sum(value => (double)value * value) - 1) > .001)
                throw new PassageRetrievalRefusalException("unavailable");
            var embeddingMs = _clock.GetElapsedTime(phaseStarted).TotalMilliseconds;
            phaseStarted = _clock.GetTimestamp();
            var window = await windowReader.ReadDenseCandidateWindowAsync(lease, scope, vector.Values, ct);
            var denseMs = _clock.GetElapsedTime(phaseStarted).TotalMilliseconds;
            if (window.Status != "ready") throw new PassageRetrievalRefusalException(window.Status);
            if (window.BaselineRawMatches is < 0 or > 100 || window.PoolRawMatches is < 0 or > IAnnCandidatePool.MaximumResults)
                throw new InvalidOperationException("passage-expanded-channel-budget-exceeded");
            phaseStarted = _clock.GetTimestamp();
            var lexical = await candidates.ReadLexicalCandidatesAsync(query, scope, ct);
            ValidateExpandedPopulation(lexical, window.Baseline, window.Pool);
            var terms = await LexicalTermsAsync(query, lexical, ct);
            var lexicalMs = _clock.GetElapsedTime(phaseStarted).TotalMilliseconds;
            phaseStarted = _clock.GetTimestamp();
            var safeLexical = lexical.Where(p => Safe(p, scope) && LexicalMatch(p, query, terms)).ToArray();
            var safeBaseline = window.Baseline.Where(p => Safe(p, scope)).ToArray();
            var safePool = window.Pool.Where(p => Safe(p, scope)).ToArray();
            var plan = PassageRanking.PlanExpansion(query, safeLexical, safeBaseline, safePool);
            var approved = await CitableExpandedAsync(plan.DisclosureCandidates, scope, ct);
            var baseline = PassageRanking.Fuse(query, safeLexical.Where(p => approved.Contains(p.ChunkId)).ToArray(),
                safeBaseline.Where(p => approved.Contains(p.ChunkId)).ToArray());
            var shortlist = plan.Complete(approved);
            if (!baseline.Select(p => p.Passage.ChunkId).SequenceEqual(shortlist.Take(baseline.Count).Select(p => p.Passage.ChunkId)))
                throw new InvalidOperationException("passage-expanded-baseline-changed");
            var disclosureMs = _clock.GetElapsedTime(phaseStarted).TotalMilliseconds;
            var batchCounts = new List<int>();
            IReadOnlyList<RankedPassage> ranked = shortlist;
            phaseStarted = _clock.GetTimestamp();
            if (shortlist.Count > 0)
            {
                var result = await RunModelAsync(async (_, reranker, modelToken) =>
                {
                    var combined = new List<RerankScore>(shortlist.Count);
                    foreach (var batch in shortlist.Chunk(PassageRanking.ShortlistBudget))
                    {
                        modelToken.ThrowIfCancellationRequested();
                        var pairs = batch.Select(p => new RerankPassage(p.Passage.ChunkId, SearchText(p.Passage))).ToArray();
                        batchCounts.Add(pairs.Length);
                        var scores = await reranker.RerankAsync(query, pairs, modelToken);
                        modelToken.ThrowIfCancellationRequested();
                        if (scores.ModelFingerprint != inference.RerankerFingerprint || scores.Scores.Count != pairs.Length ||
                            !scores.Scores.Select(p => p.PassageId).SequenceEqual(pairs.Select(p => p.PassageId)) || scores.Scores.Any(p => !float.IsFinite(p.Logit)))
                            throw new InvalidOperationException("passage-reranker-result-invalid");
                        combined.AddRange(scores.Scores);
                    }
                    return new RerankResult(combined, inference.RerankerFingerprint);
                }, ct);
                ct.ThrowIfCancellationRequested();
                // Private diagnostic ordering preserves normal ApplyScores' 50-input guard.
                ranked = shortlist.Select((p, i) => (Passage: p with { RerankerScore = result.Scores[i].Logit }, Ordinal: i))
                    .OrderByDescending(p => p.Passage.ExactBodyMatch).ThenByDescending(p => p.Passage.RerankerScore)
                    .ThenBy(p => p.Ordinal).Select(p => p.Passage).ToArray();
            }
            var rankingMs = _clock.GetElapsedTime(phaseStarted).TotalMilliseconds;
            phaseStarted = _clock.GetTimestamp();
            await RequireReadable();
            var response = await AssembleAsync(query, scope, request.Limit, ranked, "ready", lease.Generation.Id,
                ["quality-diagnostic"], plan.DisclosureCandidates.Count, ct);
            await RequireReadable();
            var assemblyMs = _clock.GetElapsedTime(phaseStarted).TotalMilliseconds;
            return new(response, safeLexical.Concat(safeBaseline).Select(p => p.ChunkId).Distinct().ToArray(),
                plan.DisclosureCandidates.Select(p => p.ChunkId).ToArray(), baseline.Select(p => p.Passage.ChunkId).ToArray(),
                shortlist.Select(p => p.Passage.ChunkId).ToArray(), batchCounts, window.BaselineRawMatches, window.PoolRawMatches, inference.RerankerFingerprint,
                new(scopeAndLeaseMs, embeddingMs, denseMs, lexicalMs, disclosureMs, rankingMs, assemblyMs,
                    _clock.GetElapsedTime(started).TotalMilliseconds));

            async ValueTask RequireReadable()
            {
                ct.ThrowIfCancellationRequested();
                if (!await lease.IsReadableAsync(ct)) throw new PublicationSnapshotConflictException("The captured manifest is unreadable.");
            }
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        { throw new PassageRetrievalRefusalException("timeout"); }
    }

    private static void ValidateExpandedPopulation(IReadOnlyList<EligiblePassageCandidate> lexical,
        IReadOnlyList<EligiblePassageCandidate> baseline, IReadOnlyList<EligiblePassageCandidate> pool) =>
        PassageRanking.ValidateExpandedPopulation(lexical, baseline, pool);
}

internal sealed record CandidateQualityResult(CorpusSearchResponse Result, IReadOnlyList<long> BaselineBindingIds,
    IReadOnlyList<long> DisclosureBindingIds, IReadOnlyList<long> BaselineModelInputIds, IReadOnlyList<long> ModelInputIds,
    IReadOnlyList<int> ModelBatchCounts, int BaselineRawMatches, int PoolRawMatches, string RerankerFingerprint, CandidateQualityTimings Timings)
{
    public bool ServingQualified => false;
    public Guid? ClientId { get; init; }
    public Guid? CohortId { get; init; }
    public string? EmbeddingFingerprint { get; init; }
    public double? FullPipelineMilliseconds { get; init; }
    public double? PayloadReadyMilliseconds { get; init; }
    public IReadOnlyList<long>? PayloadInputIds { get; init; }
    public Guid? CapturedGeneration { get; init; }
    public string? FailurePhase { get; init; }
    public string? FailureReason { get; init; }
    public DateTimeOffset? ApiArrivalUtc { get; init; }
    public DateTimeOffset? PipelineCompletedUtc { get; init; }
}

internal sealed record CandidateQualityTimings(double ScopeAndLeaseMilliseconds, double EmbeddingMilliseconds,
    double DenseSelectionMilliseconds, double LexicalMilliseconds, double DisclosurePreflightMilliseconds,
    double RankingMilliseconds, double AssemblyMilliseconds, double BeforeLeaseDisposalMilliseconds);
