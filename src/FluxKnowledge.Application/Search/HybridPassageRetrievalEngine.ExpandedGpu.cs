using System.Diagnostics;
using System.Text;
using FluxKnowledge.Application.Contracts;
using FluxKnowledge.Application.IntegrationV1;
using FluxKnowledge.Application.Pipeline;
using FluxKnowledge.Application.Ports;

namespace FluxKnowledge.Application.Search;

public sealed partial class HybridPassageRetrievalEngine
{
    /// <summary>Expanded scoped ranking with independent lexical work and bounded native cohort ownership.</summary>
    internal async ValueTask<CandidateQualityResult> SearchExpandedAsync(CorpusSearchRequest request,
        IExpandedPassageRanker ranker, DateTimeOffset apiArrivalUtc, CancellationToken caller,
        Action<ExpandedPipelineDiagnostic>? observe = null, IScheduledPassageInference? queryEmbedding = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        var embeddingInference = queryEmbedding ?? inference;
        var query = request.Query?.Trim().Normalize(NormalizationForm.FormC);
        if (string.IsNullOrWhiteSpace(query) || query.Length > 2048 || request.Limit is < 1 or > 50 ||
            request.MatchMode != "keyword" || request.Scope is not ("root" or "workspace") ||
            request.Scope == "root" && (!request.RootId.HasValue || request.Cwd is not null) ||
            request.Scope == "workspace" && (request.RootId.HasValue || string.IsNullOrWhiteSpace(request.Cwd)))
            throw new NativeOperationException("invalid-request");
        if (candidates is not ICorpusDenseCandidateWindowReader windowReader) throw new PassageRetrievalRefusalException("unavailable");
        using var outer = new CancellationTokenSource(TimeSpan.FromSeconds(25), _clock);
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(caller, outer.Token);
        using var semanticDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(20), _clock);
        using var semanticBudget = CancellationTokenSource.CreateLinkedTokenSource(budget.Token, semanticDeadline.Token);
        var ct = budget.Token;
        var semantic = semanticBudget.Token;
        var started = _clock.GetTimestamp();
        double scopeMs=0,embeddingMs=0,denseMs=0,lexicalMs=0,disclosureMs=0,rankingMs=0,assemblyMs=0;
        double? payloadReadyMs=null;
        Guid? clientId=null,cohortId=null,capturedGeneration=null;
        IReadOnlyList<long>? payloadIds=null;
        var currentPhase="scope";var failureReason="unavailable";
        var phaseStarted=started;var scopeComplete=false;
        var diagnosticOutcome="refused";
        Task<IReadOnlyList<EligiblePassageCandidate>>? lexicalTask = null;
        try
        {
            var scope = await reader.ResolveScopeAsync(request.Scope, request.RootId, request.Cwd, ct).ConfigureAwait(false)
                ?? throw new NativeOperationException("scope-unavailable");
            var readiness = await reader.GetLexicalReadinessAsync(ct).ConfigureAwait(false);
            if (!readiness.IndexPresent) throw new NativeOperationException("lexical-unavailable");
            lexicalTask = ReadLexicalTrackedAsync();
            var status = "unavailable";
            try
            {
                var result = await SemanticAsync().ConfigureAwait(false);
                diagnosticOutcome=result.Result.SemanticStatus;
                return result with { FullPipelineMilliseconds = _clock.GetElapsedTime(started).TotalMilliseconds,
                    ApiArrivalUtc=apiArrivalUtc,PipelineCompletedUtc=_clock.GetUtcNow() };
            }
            catch (PassageInferenceOwnershipException) { throw; }
            catch (Exception failure) when (ExpandedOwnershipUnconfirmed(failure))
            { throw new PassageInferenceOwnershipException(failure); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (PassageRetrievalRefusalException refusal)
            {
                status=refusal.Status;
                failureReason=refusal.Data["staging.rank.phase"] as string is "payload-readiness" or "payload-admission-expired" ? (string)refusal.Data["staging.rank.phase"]! : status;
            }
            catch (PublicationSnapshotConflictException) { status = "index-updating"; }
            catch (TimeoutException) { status = "timeout"; }
            catch (OperationCanceledException) { status = "timeout"; }
            catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
            { status = exception.Message == "bge-input-too-long" ? "query-too-long" : "unavailable"; }
            var fallback = await lexicalTask.ConfigureAwait(false);
            if(failureReason=="unavailable")failureReason=status;
            if(!scopeComplete) scopeMs=_clock.GetElapsedTime(started).TotalMilliseconds;
            var failedPhase=currentPhase;
            var fallbackTerms = await LexicalTermsAsync(query, fallback, ct).ConfigureAwait(false);
            var eligible = fallback.Where(p => Safe(p, scope) && LexicalMatch(p, query, fallbackTerms)).ToArray();
            var approved = await CitableAsync(eligible, scope, ct).ConfigureAwait(false);
            var warnings = BaseWarnings(readiness, fallback.Count, 0); warnings.Add("semantic:" + status);
            var response = await MeasureAsync("lexical-fallback-readback",() => AssembleAsync(query, scope, request.Limit,
                PassageRanking.Fuse(query, eligible.Where(p => approved.Contains(p.ChunkId)).ToArray(), []),
                status, null, warnings, eligible.Length, ct),ms => assemblyMs+=ms).ConfigureAwait(false);
            diagnosticOutcome=status;
            return new(response, [], [], [], [], [], 0, 0, ranker.RerankerFingerprint,
                new(scopeMs,embeddingMs,denseMs,lexicalMs,disclosureMs,rankingMs,assemblyMs,_clock.GetElapsedTime(started).TotalMilliseconds))
                { EmbeddingFingerprint = embeddingInference.EmbeddingProfile.ModelFingerprint, FullPipelineMilliseconds = _clock.GetElapsedTime(started).TotalMilliseconds,
                    ClientId=clientId,CohortId=cohortId,CapturedGeneration=capturedGeneration,PayloadReadyMilliseconds=payloadReadyMs,
                    PayloadInputIds=payloadIds,FailurePhase=failedPhase,FailureReason=failureReason,
                    ApiArrivalUtc=apiArrivalUtc,PipelineCompletedUtc=_clock.GetUtcNow() };

            async Task<IReadOnlyList<EligiblePassageCandidate>> ReadLexicalTrackedAsync()
            {
                var begin=_clock.GetTimestamp();var outcome="failed";
                try{var result=await candidates.ReadLexicalCandidatesAsync(query,scope,ct).ConfigureAwait(false);outcome="completed";return result;}
                catch(OperationCanceledException){outcome="cancelled";throw;}
                finally{lexicalMs=_clock.GetElapsedTime(begin).TotalMilliseconds;EmitPhase("lexical-retrieval",outcome,lexicalMs);}
            }

            async Task<CandidateQualityResult> SemanticAsync()
            {
                currentPhase="generation-lease";
                var sql = await leases.TryAcquireReadableAsync(_instance, owner.Current, embeddingInference.EmbeddingProfile.ModelFingerprint,
                    embeddingInference.EmbeddingProfile.Dimensions, semantic).ConfigureAwait(false)
                    ?? throw new PassageRetrievalRefusalException("index-updating");
                var lease = await annFactory.OpenAsync(sql, semantic).ConfigureAwait(false);
                capturedGeneration=lease.Generation.Id;
                IExpandedPassageRankTicket? ticket = null;
                Exception? bodyFailure = null;
                try
                {
                if (lease.Generation.FormatVersion != 2) throw new PassageRetrievalRefusalException("unavailable");
                await RequireReadable().ConfigureAwait(false);
                if (candidates is ICorpusAnnBindingPreparation preparation && !lease.IsPrepared(preparation))
                    await WaitForPreparationAsync(lease, preparation, semantic).ConfigureAwait(false);
                // Early reservation starts the observed load. No SQL reader/lease or CPU lane is captured by it.
                ticket = ranker.Reserve(apiArrivalUtc, Activity.Current?.TraceId.ToString() ?? string.Empty,
                    Activity.Current?.SpanId.ToString() ?? string.Empty, semantic);
                clientId=ticket.ClientId;cohortId=ticket.CohortId;
                scopeMs = _clock.GetElapsedTime(started).TotalMilliseconds;scopeComplete=true;
                var phase = _clock.GetTimestamp();
                var vector = await MeasureAsync("query-embedding",() => RunModelAsync((embedding, _, modelToken) => embedding.CreateEmbeddingAsync(query, modelToken), semantic, embeddingInference),ms => embeddingMs=ms).ConfigureAwait(false);
                if (vector.ModelFingerprint != embeddingInference.EmbeddingProfile.ModelFingerprint || vector.Values.Count != embeddingInference.EmbeddingProfile.Dimensions ||
                    vector.Values.Any(v => !float.IsFinite(v)) || Math.Abs(vector.Values.Sum(v => (double)v*v)-1) > .001)
                    throw new PassageRetrievalRefusalException("unavailable");
                phase = _clock.GetTimestamp();
                var window = await MeasureAsync("dense-selection",() => windowReader.ReadDenseCandidateWindowAsync(lease,scope,vector.Values,semantic),ms => denseMs=ms).ConfigureAwait(false);
                if (window.Status != "ready") throw new PassageRetrievalRefusalException(window.Status);
                if (window.BaselineRawMatches is < 0 or > 100 || window.PoolRawMatches is < 0 or > IAnnCandidatePool.MaximumResults)
                    throw new InvalidOperationException("passage-expanded-channel-budget-exceeded");
                phase = _clock.GetTimestamp();
                currentPhase="lexical-wait";
                var lexical = await MeasureAsync<IReadOnlyList<EligiblePassageCandidate>>("lexical-wait",async()=>await lexicalTask.WaitAsync(semantic).ConfigureAwait(false),_=>{}).ConfigureAwait(false);
                ValidateExpandedPopulation(lexical,window.Baseline,window.Pool);
                var terms = await MeasureAsync("lexical-terms",()=>LexicalTermsAsync(query,lexical,semantic),_=>{}).ConfigureAwait(false);
                phase = _clock.GetTimestamp();
                phaseStarted=phase;currentPhase="disclosure-preflight";
                var safeLexical = lexical.Where(p => Safe(p,scope) && LexicalMatch(p,query,terms)).ToArray();
                var baseline = window.Baseline.Where(p => Safe(p,scope)).ToArray();
                var plan = PassageRanking.PlanExpansion(query,safeLexical,baseline,window.Pool.Where(p => Safe(p,scope)).ToArray());
                var citable = await MeasureAsync("disclosure-preflight",()=>CitableExpandedAsync(plan.DisclosureCandidates,scope,semantic),_=>{}).ConfigureAwait(false);
                var shortlist = plan.Complete(citable);
                var original = PassageRanking.Fuse(query,safeLexical.Where(p => citable.Contains(p.ChunkId)).ToArray(),
                    baseline.Where(p => citable.Contains(p.ChunkId)).ToArray());
                if (!original.Select(p => p.Passage.ChunkId).SequenceEqual(shortlist.Take(original.Count).Select(p => p.Passage.ChunkId)))
                    throw new InvalidOperationException("passage-expanded-baseline-changed");
                disclosureMs = _clock.GetElapsedTime(phase).TotalMilliseconds;
                phase = _clock.GetTimestamp();
                IReadOnlyList<RankedPassage> ranked = shortlist;
                if (shortlist.Count > 0)
                {
                    var inputs = shortlist.Select(p => new RerankPassage(p.Passage.ChunkId,SearchText(p.Passage))).ToArray();
                    payloadIds=inputs.Select(p => p.PassageId).ToArray();payloadReadyMs=_clock.GetElapsedTime(started).TotalMilliseconds;
                    EmitPhase("payload-ready","completed",payloadReadyMs.Value);
                    var scores = await MeasureAsync("rerank-wait",() => ticket.SubmitAsync(query,inputs),ms => rankingMs=ms).ConfigureAwait(false);
                    semantic.ThrowIfCancellationRequested();
                    if (scores.ModelFingerprint != ranker.RerankerFingerprint || scores.Scores.Count != inputs.Length ||
                        !scores.Scores.Select(s => s.PassageId).SequenceEqual(inputs.Select(p => p.PassageId)) ||
                        scores.Scores.Any(s => !float.IsFinite(s.Logit))) throw new InvalidOperationException("staging-rank-output-invalid");
                    ranked = shortlist.Select((p,i) => (Passage: p with { RerankerScore = scores.Scores[i].Logit }, Ordinal:i))
                        .OrderByDescending(p => p.Passage.ExactBodyMatch).ThenByDescending(p => p.Passage.RerankerScore)
                        .ThenBy(p => p.Ordinal).Select(p => p.Passage).ToArray();
                }
                var response = await MeasureAsync<CorpusSearchResponse>("cited-readback",async () =>
                {
                    await RequireReadable().ConfigureAwait(false);
                    var assembled=await AssembleAsync(query,scope,request.Limit,ranked,"ready",lease.Generation.Id,
                        [],plan.DisclosureCandidates.Count,semantic).ConfigureAwait(false);
                    await RequireReadable().ConfigureAwait(false);
                    return assembled;
                },ms => assemblyMs=ms).ConfigureAwait(false);
                return new(response,safeLexical.Concat(baseline).Select(p => p.ChunkId).Distinct().ToArray(),
                    plan.DisclosureCandidates.Select(p => p.ChunkId).ToArray(),original.Select(p => p.Passage.ChunkId).ToArray(),
                    shortlist.Select(p => p.Passage.ChunkId).ToArray(),shortlist.Chunk(50).Select(p => p.Length).ToArray(),
                    window.BaselineRawMatches,window.PoolRawMatches,ranker.RerankerFingerprint,
                    new(scopeMs,embeddingMs,denseMs,lexicalMs,disclosureMs,rankingMs,
                        assemblyMs,_clock.GetElapsedTime(started).TotalMilliseconds))
                    { ClientId=ticket.ClientId,CohortId=ticket.CohortId,EmbeddingFingerprint=embeddingInference.EmbeddingProfile.ModelFingerprint,
                        PayloadReadyMilliseconds=payloadReadyMs,PayloadInputIds=payloadIds,CapturedGeneration=capturedGeneration };

                async ValueTask RequireReadable()
                {
                    semantic.ThrowIfCancellationRequested();
                    if (!await lease.IsReadableAsync(semantic).ConfigureAwait(false))
                        throw new PublicationSnapshotConflictException("The captured manifest is unreadable.");
                }
                }
                catch (Exception failure)
                {
                    if(currentPhase=="disclosure-preflight")disclosureMs=_clock.GetElapsedTime(phaseStarted).TotalMilliseconds;
                    if(currentPhase=="cited-readback")assemblyMs=_clock.GetElapsedTime(phaseStarted).TotalMilliseconds;
                    bodyFailure = failure; throw;
                }
                finally
                {
                    List<Exception> cleanup = [];
                    if (ticket is not null)
                        try { await ticket.DisposeAsync().ConfigureAwait(false); }
                        catch (Exception failure) { cleanup.Add(failure); }
                    try { await lease.DisposeAsync().ConfigureAwait(false); }
                    catch (Exception failure) { cleanup.Add(failure); }
                    if (cleanup.Count > 0)
                    {
                        if (bodyFailure is not null) cleanup.Insert(0,bodyFailure);
                        var combined = new AggregateException(cleanup);
                        if (ExpandedOwnershipUnconfirmed(combined)) throw new PassageInferenceOwnershipException(combined);
                        throw combined;
                    }
                }
            }
        }
        catch (OperationCanceledException) when (outer.IsCancellationRequested && !caller.IsCancellationRequested)
        { diagnosticOutcome="timeout";failureReason="outer-deadline";throw new PassageRetrievalRefusalException("timeout"); }
        catch(Exception failure)
        {
            diagnosticOutcome=failure is PassageRetrievalRefusalException refusal?refusal.Status:ExpandedOwnershipUnconfirmed(failure)?"ownership-unconfirmed":"failed";
            throw;
        }
        finally
        {
            await budget.CancelAsync().ConfigureAwait(false);
            // Every started lexical operation remains owned even when semantic admission or retrieval fails.
            if (lexicalTask is not null) await ObserveLateLexicalAsync(lexicalTask).ConfigureAwait(false);
            if(observe is not null)
            {
                try
                {
                    observe(new(new(scopeMs,embeddingMs,denseMs,lexicalMs,disclosureMs,rankingMs,assemblyMs,
                        _clock.GetElapsedTime(started).TotalMilliseconds),capturedGeneration,clientId,cohortId,currentPhase,
                        failureReason,diagnosticOutcome,payloadReadyMs,payloadIds));
                }
                catch(Exception observationFailure) when (observationFailure is not OutOfMemoryException and not StackOverflowException)
                { /* A diagnostic sink must never replace an ownership or query failure. */ }
            }
        }

        async ValueTask<T> MeasureAsync<T>(string name,Func<ValueTask<T>> action,Action<double> record)
        {
            currentPhase=name;phaseStarted=_clock.GetTimestamp(); var began=phaseStarted; var outcome="failed";
            try{var result=await action().ConfigureAwait(false);outcome="completed";return result;}
            catch(OperationCanceledException){outcome="cancelled";throw;}
            finally
            {
                var elapsed=_clock.GetElapsedTime(began).TotalMilliseconds;record(elapsed);
                EmitPhase(name,outcome,elapsed);
            }
        }
        void EmitPhase(string name,string outcome,double elapsed)
        {
            if(!HybridSearchDiagnostics.Log.IsEnabled())return;
            var correlation=HybridSearchDiagnostics.CurrentCorrelation;
            HybridSearchDiagnostics.Log.Phase(correlation.TraceId,correlation.SpanId,clientId?.ToString("N")??string.Empty,name,outcome,elapsed);
        }
    }

    private static bool ExpandedOwnershipUnconfirmed(Exception failure) =>
        UnconfirmedOwnership(failure) is not null ||
        failure is AggregateException aggregate && aggregate.InnerExceptions.Any(ExpandedOwnershipUnconfirmed) ||
        failure.InnerException is { } inner && ExpandedOwnershipUnconfirmed(inner);
}

internal sealed record ExpandedPipelineDiagnostic(CandidateQualityTimings Timings,Guid? CapturedGeneration,Guid? ClientId,
    Guid? CohortId,string Phase,string FailureReason,string Outcome,double? PayloadReadyMilliseconds,IReadOnlyList<long>? PayloadInputIds);
