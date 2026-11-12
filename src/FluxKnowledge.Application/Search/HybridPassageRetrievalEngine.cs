using System.Security.Cryptography;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using FluxKnowledge.Application.Contracts;
using FluxKnowledge.Application.Gpu;
using FluxKnowledge.Application.IntegrationV1;
using FluxKnowledge.Application.Pipeline;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Visibility;

namespace FluxKnowledge.Application.Search;

/// <summary>One complete passage pipeline; an observed semantic task retains SQL/native ownership through model settlement.</summary>
public sealed partial class HybridPassageRetrievalEngine(ICorpusRetrievalReader reader, IHybridPassageCandidateReader candidates,
    ICorpusGenerationLeaseStore leases, ICorpusAnnLeaseFactory annFactory, IScheduledPassageInference inference,
    IGpuInteractiveOwnerProbe owner, ICorpusEvidenceCodec evidence, ILocalPrivateContentDisclosure disclosure,
    TimeProvider? timeProvider = null, TimeSpan? searchTimeout = null,
    ICorpusAnnPreparationSignal? preparationSignal = null) : IHybridPassageRetrieval
{
    private readonly Guid _instance = Guid.NewGuid();
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;
    private readonly TimeSpan _searchTimeout = ValidatedTimeout(searchTimeout);

    private static TimeSpan ValidatedTimeout(TimeSpan? timeout)
    {
        var value = timeout ?? TimeSpan.FromSeconds(10);
        if (value < TimeSpan.FromSeconds(10) || value > TimeSpan.FromSeconds(60))
            throw new ArgumentOutOfRangeException(nameof(timeout));
        return value;
    }

    public ValueTask<CorpusSearchResponse> SearchAsync(string query, ResolvedCorpusScope scope, int limit,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(query) || query.Length > 2048 || limit is < 1 or > 50)
            throw new NativeOperationException("invalid-request");
        return RunBoundedAsync(query, limit, "keyword",
            _ => ValueTask.FromResult<ResolvedCorpusScope?>(scope), cancellationToken);
    }

    public ValueTask<CorpusSearchResponse> SearchAsync(CorpusSearchRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var query = request.MatchMode == "literal" ? request.Query : request.Query?.Trim().Normalize(NormalizationForm.FormC);
        if (string.IsNullOrWhiteSpace(query) || query.Length > 2048 || request.Limit is < 1 or > 50 ||
            request.MatchMode is not ("keyword" or "literal") ||
            request.Scope is not ("all" or "root" or "workspace") ||
            request.Scope == "all" && (request.RootId.HasValue || request.Cwd is not null) ||
            request.Scope == "root" && (!request.RootId.HasValue || request.Cwd is not null) ||
            request.Scope == "workspace" && (request.RootId.HasValue || string.IsNullOrWhiteSpace(request.Cwd)))
            throw new NativeOperationException("invalid-request");
        return RunBoundedAsync(query, request.Limit, request.MatchMode,
            ct => reader.ResolveScopeAsync(request.Scope, request.RootId, request.Cwd, ct), cancellationToken);
    }

    private async ValueTask<CorpusSearchResponse> RunBoundedAsync(string query, int limit, string matchMode,
        Func<CancellationToken, ValueTask<ResolvedCorpusScope?>> resolveScope, CancellationToken callerToken)
    {
        callerToken.ThrowIfCancellationRequested();
        var traceId = Activity.Current?.TraceId.ToString() ?? string.Empty;
        var spanId = Activity.Current?.SpanId.ToString() ?? string.Empty;
        var searchId = Guid.NewGuid().ToString("N");
        var timer = Stopwatch.StartNew();
        var currentPhase = "scope";
        void ReportPhase(string phase, string outcome)
        {
            if (outcome == "begin") Volatile.Write(ref currentPhase, phase);
            if (HybridSearchDiagnostics.Log.IsEnabled())
                HybridSearchDiagnostics.Log.Phase(traceId, spanId, searchId, phase, outcome, timer.Elapsed.TotalMilliseconds);
        }
        CorpusSearchResponse? returned = null;
        var deadline = new CancellationTokenSource(_searchTimeout, _clock);
        var budget = CancellationTokenSource.CreateLinkedTokenSource(callerToken, deadline.Token);
        var semanticDeadline = new CancellationTokenSource(_searchTimeout - TimeSpan.FromSeconds(5), _clock);
        var semanticBudget = CancellationTokenSource.CreateLinkedTokenSource(budget.Token, semanticDeadline.Token);
        Task<CorpusSearchResponse>? semanticWork = null;
        var work = SearchCoreAsync(query, limit, matchMode, resolveScope, task => Volatile.Write(ref semanticWork, task),
            traceId, spanId, searchId, ReportPhase, budget.Token, semanticBudget.Token).AsTask();
        try { return returned = await work.WaitAsync(_searchTimeout, _clock, callerToken).ConfigureAwait(false); }
        catch (Exception exception) when (!callerToken.IsCancellationRequested &&
            (exception is TimeoutException || exception is OperationCanceledException && deadline.IsCancellationRequested))
        {
            ReportPhase(Volatile.Read(ref currentPhase), "deadline");
            throw new PassageRetrievalRefusalException("timeout");
        }
        finally
        {
            if (HybridSearchDiagnostics.Log.IsEnabled())
                HybridSearchDiagnostics.Log.Completed(traceId, spanId, searchId, returned?.SemanticStatus ?? "refused",
                    returned is null ? string.Empty : string.Join(',', returned.Results.Select(hit => hit.ChunkId)), timer.Elapsed.TotalMilliseconds);
            // Stops further useful work, never disposes a callback's native/SQL lease.
            budget.Cancel();
            semanticBudget.Cancel();
            _ = ObserveAndDisposeAsync(work, () => Volatile.Read(ref semanticWork), budget, deadline, semanticBudget, semanticDeadline);
        }
    }

    private static async Task ObserveAndDisposeAsync(Task work, Func<Task?> semanticWork,
        params CancellationTokenSource[] sources)
    {
        try
        {
            await ObserveLateLexicalAsync(work).ConfigureAwait(false);
            if (semanticWork() is { } pending) await ObserveLateLexicalAsync(pending).ConfigureAwait(false);
        }
        finally { foreach (var source in sources) source.Dispose(); }
    }

    private async ValueTask<CorpusSearchResponse> SearchCoreAsync(string query, int limit, string matchMode,
        Func<CancellationToken, ValueTask<ResolvedCorpusScope?>> resolveScope, Action<Task<CorpusSearchResponse>> semanticStarted,
        string traceId, string spanId, string searchId, Action<string, string> reportPhase,
        CancellationToken cancellationToken, CancellationToken semanticToken)
    {
        reportPhase("scope", "begin");
        var scope = await resolveScope(cancellationToken).ConfigureAwait(false) ?? throw new NativeOperationException("scope-unavailable");
        reportPhase("scope", "completed");
        cancellationToken.ThrowIfCancellationRequested();
        if (matchMode == "literal")
        {
            reportPhase("literal-selection", "begin");
            var literal = await candidates.ReadLiteralCandidatesAsync(query, scope, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (literal.Count > PassageRanking.ChannelBudget) throw new InvalidOperationException("passage-channel-budget-exceeded");
            var eligible = literal.Where(passage => Safe(passage, scope) && passage.Content.Contains(query, StringComparison.Ordinal))
                .OrderBy(passage => passage.ChunkId).ToArray();
            var citable = await CitableAsync(eligible, scope, cancellationToken).ConfigureAwait(false);
            var ordered = eligible.Where(passage => citable.Contains(passage.ChunkId))
                .Select(passage => new RankedPassage(passage, 0, null, null, true)).ToArray();
            reportPhase("literal-selection", "completed");
            var warnings = new List<string>();
            if (literal.Count == PassageRanking.ChannelBudget) warnings.Add("candidate-budget-reached");
            return await AssembleAsync(query, scope, limit, ordered, "not-requested", null, warnings,
                eligible.Length, cancellationToken, literal: true).ConfigureAwait(false);
        }
        reportPhase("lexical-readiness", "begin");
        var readiness = await reader.GetLexicalReadinessAsync(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (!readiness.IndexPresent) throw new NativeOperationException("lexical-unavailable");
        reportPhase("lexical-readiness", "completed");
        var lexicalTask = candidates.ReadLexicalCandidatesAsync(query, scope, cancellationToken).AsTask();
        _ = ObserveLateLexicalAsync(lexicalTask);
        var status = "unavailable";
        try
        {
            var semanticWork = SearchSemanticAsync();
            semanticStarted(semanticWork);
            var completed = await semanticWork.WaitAsync(semanticToken).ConfigureAwait(false);
            reportPhase("inference-settled", "completed");
            return completed;

            async Task<CorpusSearchResponse> SearchSemanticAsync()
            {
                var workToken = semanticToken;
                workToken.ThrowIfCancellationRequested();
                // This observed task owns corpus resources through actual callback settlement.
                // Model lanes are borrowed only for model work, never SQL or citation waits.
                reportPhase("generation-lease", "begin");
                var sqlLease = await leases.TryAcquireReadableAsync(_instance, owner.Current, inference.EmbeddingProfile.ModelFingerprint,
                    inference.EmbeddingProfile.Dimensions, workToken).ConfigureAwait(false);
                if (sqlLease is null)
                {
                    reportPhase("generation-lease", "refused");
                    throw new PassageRetrievalRefusalException("index-updating");
                }
                if (scope.Kind != "all" && sqlLease.Generation.FormatVersion != 2)
                {
                    await sqlLease.DisposeAsync().ConfigureAwait(false);
                    throw new PassageRetrievalRefusalException("unavailable");
                }
                await using var lease = await annFactory.OpenAsync(sqlLease, workToken).ConfigureAwait(false);
                if (!await lease.IsReadableAsync(workToken).ConfigureAwait(false))
                {
                    reportPhase("generation-current-before-embedding", "refused");
                    throw new PassageRetrievalRefusalException("index-updating");
                }
                if (candidates is ICorpusAnnBindingPreparation preparation && !lease.IsPrepared(preparation))
                {
                    reportPhase("ann-preparation-grace", "begin");
                    await WaitForPreparationAsync(lease, preparation, workToken).ConfigureAwait(false);
                    reportPhase("ann-preparation-grace", "completed");
                }
                reportPhase("generation-lease", "completed");
                reportPhase("inference-admission", "begin");
                var vector = await RunModelAsync((embedding, _, modelToken) =>
                {
                    reportPhase("inference-admission", "completed");
                    reportPhase("query-embedding", "begin");
                    return embedding.CreateEmbeddingAsync(query, modelToken);
                }, workToken).ConfigureAwait(false);
                workToken.ThrowIfCancellationRequested();
                if (vector.ModelFingerprint != inference.EmbeddingProfile.ModelFingerprint || vector.Values.Count != inference.EmbeddingProfile.Dimensions ||
                    vector.Values.Any(value => !float.IsFinite(value)) || Math.Abs(vector.Values.Sum(value => (double)value * value) - 1) > 0.001)
                    throw new PassageRetrievalRefusalException("unavailable");
                reportPhase("query-embedding", "completed");
                reportPhase("dense-selection", "begin");
                var dense = await candidates.ReadDenseCandidatesAsync(lease, scope, query, vector.Values, workToken).ConfigureAwait(false);
                if (dense.Status != "ready")
                {
                    reportPhase("dense-selection", "refused");
                    throw new PassageRetrievalRefusalException(dense.Status);
                }
                reportPhase("dense-selection", "completed");
                reportPhase("lexical-selection", "begin");
                var lexical = await lexicalTask.WaitAsync(workToken).ConfigureAwait(false);
                var terms = await LexicalTermsAsync(query, lexical, workToken).ConfigureAwait(false);
                IReadOnlyList<EligiblePassageCandidate> directed = dense.SourceDirectedStatus == SourceDirectedRetrievalStatus.Ready
                    ? dense.SourceDirectedCandidates : [];
                if (directed.Count > 0) PassageRanking.ValidateCandidatePopulation(lexical, dense.Candidates, directed);
                var lexicalEligible = lexical.Where(value => Safe(value, scope) && LexicalMatch(value, query, terms)).ToArray();
                var denseEligible = dense.Candidates.Where(value => Safe(value, scope)).ToArray();
                var directedEligible = directed.Where(value => Safe(value, scope)).ToArray();
                var rawCandidateCount = lexicalEligible.Concat(denseEligible).Concat(directedEligible).Select(value => value.ChunkId).Distinct().Count();
                var validationCandidates = directedEligible.Length == 0
                    ? lexicalEligible.Concat(denseEligible).DistinctBy(value => value.ChunkId).ToArray()
                    : PassageRanking.SelectForDisclosure(query, lexicalEligible, denseEligible, directedEligible);
                var candidateCount = validationCandidates.Count;
                reportPhase("lexical-selection", "completed");
                reportPhase("citable-selection", "begin");
                var citable = await CitableAsync(validationCandidates, scope, workToken).ConfigureAwait(false);
                lexicalEligible = lexicalEligible.Where(value => citable.Contains(value.ChunkId)).ToArray();
                denseEligible = denseEligible.Where(value => citable.Contains(value.ChunkId)).ToArray();
                directedEligible = directedEligible.Where(value => citable.Contains(value.ChunkId)).ToArray();
                reportPhase("citable-selection", "completed");
                var shortlist = PassageRanking.Fuse(query, lexicalEligible, denseEligible, directedEligible);
                if (HybridSearchDiagnostics.Log.IsEnabled())
                    HybridSearchDiagnostics.Log.Candidates(traceId, spanId, searchId,
                        string.Join(',', lexicalEligible.Select(value => value.ChunkId)),
                        string.Join(',', denseEligible.Select(value => value.ChunkId)),
                        string.Join(',', shortlist.Select(value => value.Passage.ChunkId)));
                if (HybridSearchDiagnostics.Log.IsEnabled())
                    HybridSearchDiagnostics.Log.InputGroups(traceId, spanId, searchId, rawCandidateCount,
                        PassageRanking.DistinctInputCount(lexicalEligible.Concat(denseEligible).Concat(directedEligible)), shortlist.Count, shortlist.Count);
                if (HybridSearchDiagnostics.Log.IsEnabled() && dense.SourceDirectedStatus != SourceDirectedRetrievalStatus.NotRequested)
                    HybridSearchDiagnostics.Log.SourceDirected(traceId, spanId, searchId, dense.SourceDirectedStatus.ToString(),
                        string.Join(',', directedEligible.Select(value => value.ChunkId)));
                var warnings = BaseWarnings(readiness, lexical.Count, dense.Candidates.Count);
                if (dense.SourceDirectedStatus == SourceDirectedRetrievalStatus.Timeout) warnings.Add("source-routing:timeout");
                else if (dense.SourceDirectedStatus == SourceDirectedRetrievalStatus.Unavailable) warnings.Add("source-routing:unavailable");
                IReadOnlyList<RankedPassage> ranked = shortlist;
                if (shortlist.Count > 0)
                {
                    reportPhase("rerank", "begin");
                    using var rankDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(3), _clock);
                    using var rankBudget = CancellationTokenSource.CreateLinkedTokenSource(workToken, rankDeadline.Token);
                    using var rankDeadlineObservation = rankDeadline.Token.Register(() => reportPhase("rerank-budget", "deadline"));
                    try
                    {
                        // Await actual native settlement: a timeout must not return an
                        // executing shard or its SQL/native ownership to another request.
                        reportPhase("rerank-admission", "begin");
                        var result = await RunModelAsync((_, reranker, modelToken) =>
                        {
                            reportPhase("rerank-admission", "completed");
                            return reranker.RerankAsync(query, shortlist.Select(value =>
                                new RerankPassage(value.Passage.ChunkId, SearchText(value.Passage))).ToArray(), modelToken);
                        }, rankBudget.Token).ConfigureAwait(false);
                        workToken.ThrowIfCancellationRequested();
                        if (rankDeadline.IsCancellationRequested) warnings.Add("rerank:timeout");
                        else
                        {
                            try { ranked = PassageRanking.ApplyScores(shortlist, result, inference.RerankerFingerprint); }
                            catch (InvalidOperationException) { warnings.Add("rerank:invalid-output"); }
                        }
                    }
                    catch (OperationCanceledException) when (rankDeadline.IsCancellationRequested && !workToken.IsCancellationRequested)
                    { warnings.Add("rerank:timeout"); }
                    catch (OperationCanceledException) { throw; }
                    catch (PassageInferenceOwnershipException) { throw; }
                    catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
                    {
                        workToken.ThrowIfCancellationRequested();
                        warnings.Add(rankDeadline.IsCancellationRequested ? "rerank:timeout" : "rerank:unavailable");
                    }
                    finally { reportPhase("rerank", "settled"); }
                    reportPhase("rerank", "completed");
                }
                reportPhase("cited-readback", "begin");
                var response = await AssembleAsync(query, scope, limit, ranked, "ready", lease.Generation.Id,
                    warnings, candidateCount, workToken).ConfigureAwait(false);
                if (!await lease.IsReadableAsync(workToken).ConfigureAwait(false))
                {
                    reportPhase("generation-current-before-return", "refused");
                    throw new PassageRetrievalRefusalException("index-updating");
                }
                reportPhase("cited-readback", "completed");
                return response;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (PassageRetrievalRefusalException refusal) { status = refusal.Status; }
        catch (PublicationSnapshotConflictException) { status = "index-updating"; }
        catch (TimeoutException) { status = "timeout"; }
        catch (OperationCanceledException) { status = "timeout"; }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            status = exception.Message switch
            {
                "interactive-queue-full" or "interactive-admission-limit" => "busy",
                "interactive-queue-expired" or "interactive-request-expired" or "bge-request-cancelled" => "timeout",
                "bge-input-too-long" => "query-too-long",
                "scope-capacity-exceeded" or "index-updating" or "rebuilding" => exception.Message,
                _ => "unavailable"
            };
        }
        if (status == "warming") preparationSignal?.RequestPreparation();
        reportPhase("lexical-fallback", "begin");
        var fallback = await lexicalTask.ConfigureAwait(false);
        var fallbackTerms = await LexicalTermsAsync(query, fallback, cancellationToken).ConfigureAwait(false);
        var fallbackEligible = fallback.Where(value => Safe(value, scope) && LexicalMatch(value, query, fallbackTerms)).ToArray();
        var fallbackCandidateCount = fallbackEligible.Select(value => value.ChunkId).Distinct().Count();
        var fallbackCitable = await CitableAsync(fallbackEligible, scope, cancellationToken).ConfigureAwait(false);
        fallbackEligible = fallbackEligible.Where(value => fallbackCitable.Contains(value.ChunkId)).ToArray();
        var fallbackRanking = PassageRanking.Fuse(query, fallbackEligible, []);
        var fallbackWarnings = BaseWarnings(readiness, fallback.Count, 0);
        fallbackWarnings.Add("semantic:" + status);
        return await AssembleAsync(query, scope, limit, fallbackRanking, status, null,
            fallbackWarnings, fallbackCandidateCount, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<T> RunModelAsync<T>(
        Func<IEmbeddingProvider, IPassageReranker, CancellationToken, ValueTask<T>> work, CancellationToken cancellationToken,
        IScheduledPassageInference? selectedInference = null)
    {
        var gate = new object();
        var settled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = false;
        var closed = false;
        PassageInferenceOwnershipException? ownershipFailure = null;
        try
        {
            return await (selectedInference ?? inference).ExecuteAsync(async (embedding, reranker, modelToken) =>
            {
                lock (gate)
                {
                    // Some schedulers return cancellation before their queued callback runs.
                    // Such a callback must never touch models or captured corpus resources.
                    if (closed || entered) throw new OperationCanceledException("model-admission-closed", cancellationToken);
                    entered = true;
                }
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    modelToken.ThrowIfCancellationRequested();
                    var result = await work(embedding, reranker, modelToken).ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                    modelToken.ThrowIfCancellationRequested();
                    return result;
                }
                catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
                {
                    ownershipFailure = UnconfirmedOwnership(exception);
                    throw;
                }
                finally { settled.TrySetResult(); }
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (UnconfirmedOwnership(exception) is not null)
        { throw UnconfirmedOwnership(exception)!; }
        finally
        {
            Task? completion;
            lock (gate) { closed = true; completion = entered ? settled.Task : null; }
            // Caller waits remain bounded outside this owner. Cancellation cannot dispose
            // SQL/native ownership while a callback that actually entered is still running.
            if (completion is not null) await completion.ConfigureAwait(false);
            // An executor can report cancellation before a later callback cleanup failure.
            // Preserve that stronger outcome instead of accepting fused results as ready.
            if (ownershipFailure is not null) throw ownershipFailure;
        }
    }

    private static PassageInferenceOwnershipException? UnconfirmedOwnership(Exception exception) =>
        exception is PassageInferenceOwnershipException ownership ? ownership :
        exception.Message is "interactive-execution-or-release-unconfirmed" or "interactive-native-release-unconfirmed"
            ? new PassageInferenceOwnershipException(exception) : null;

    private static async Task ObserveLateLexicalAsync(Task work)
    {
        try { await work.ConfigureAwait(false); }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException) { }
    }

    private async ValueTask WaitForPreparationAsync(ICorpusAnnLease lease, ICorpusAnnBindingPreparation metadata,
        CancellationToken cancellationToken)
    {
        var started = _clock.GetTimestamp();
        var grace = TimeSpan.FromMilliseconds(250);
        if (preparationSignal is not null)
        {
            preparationSignal.RequestPreparation();
            while (!lease.IsPrepared(metadata))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var failure = preparationSignal.FailureFor(lease.Generation.Id);
                if (failure is not null and not "warming") throw new PassageRetrievalRefusalException(failure);
                var remaining = grace - _clock.GetElapsedTime(started);
                if (remaining <= TimeSpan.Zero) break;
                await Task.Delay(remaining < TimeSpan.FromMilliseconds(25) ? remaining : TimeSpan.FromMilliseconds(25),
                    _clock, cancellationToken).ConfigureAwait(false);
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (!lease.IsPrepared(metadata))
            throw new PassageRetrievalRefusalException(preparationSignal?.FailureFor(lease.Generation.Id) ?? "warming");
        if (!await lease.IsReadableAsync(cancellationToken).ConfigureAwait(false))
            throw new PassageRetrievalRefusalException("index-updating");
    }

    private ValueTask<IReadOnlySet<long>> CitableAsync(IReadOnlyList<EligiblePassageCandidate> passages,
        ResolvedCorpusScope scope, CancellationToken cancellationToken) =>
        CitableBatchAsync(passages, scope, false, cancellationToken);

    private ValueTask<IReadOnlySet<long>> CitableExpandedAsync(IReadOnlyList<EligiblePassageCandidate> passages,
        ResolvedCorpusScope scope, CancellationToken cancellationToken) =>
        CitableBatchAsync(passages, scope, true, cancellationToken);

    private async ValueTask<IReadOnlySet<long>> CitableBatchAsync(IReadOnlyList<EligiblePassageCandidate> passages,
        ResolvedCorpusScope scope, bool expanded, CancellationToken cancellationToken)
    {
        var bindings = passages.Select(BindingFor).ToArray();
        var current = expanded
            ? await reader.ReadExpandedForDisclosureAsync(bindings, cancellationToken).ConfigureAwait(false)
            : await reader.ReadForDisclosureAsync(bindings, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return passages.Where(passage => current.TryGetValue(passage.ChunkId, out var read) &&
                Safe(read.Candidate, scope) && PassageRanking.SamePassage(read.Candidate, passage) &&
                read.Text == passage.Content && read.StartOffset == passage.StartOffset && read.DisclosureText is not null &&
                !disclosure.EvaluateCodeGuard(read.DisclosureText, LocalDisclosureKind.RetainedDetail,
                    read.GuardProof, read.StartOffset, read.Text.Length).Withheld &&
                NativeV1EnvelopeProtector.CanDiscloseResult(JsonSerializer.SerializeToElement(read.DisclosureText)))
            .Select(passage => passage.ChunkId).ToHashSet();
    }

    private static CorpusEvidenceBinding BindingFor(EligiblePassageCandidate passage) =>
        new(2, passage.RootId, passage.OwnerSourceRevisionId, Hash(passage.SourceIdentity),
            passage.PipelineRecordId, passage.PipelineRecordRevision, passage.ArtifactId, passage.ArtifactHash,
            passage.ChunkId, passage.ChunkHash, passage.StartOffset, passage.Length, passage.CorpusEpoch);

    private async ValueTask<CorpusSearchResponse> AssembleAsync(string query, ResolvedCorpusScope scope, int limit,
        IReadOnlyList<RankedPassage> shortlist, string semanticStatus, Guid? generation,
        List<string> warnings, int candidateCount, CancellationToken cancellationToken, bool literal = false)
    {
        var hits = new List<CorpusSearchHit>(limit);
        var bindings = new List<CorpusEvidenceBinding>(limit);
        var accepted = new List<EligiblePassageCandidate>(limit);
        var perDocument = new Dictionary<(Guid?, Guid), int>();
        foreach (var ranked in shortlist)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (hits.Count == limit) break;
            foreach (var passage in ranked.AlternativePassages.Prepend(ranked.Passage))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!Safe(passage, scope) || passage.ChunkId != ranked.Passage.ChunkId &&
                    !PassageRanking.SameSearchInput(ranked.Passage, passage)) continue;
                var document = PassageRanking.DocumentIdentity(passage);
                if (perDocument.GetValueOrDefault(document) >= 2 || PassageRanking.IsNearDuplicate(passage, accepted)) continue;
                var binding = BindingFor(passage);
                var current = await reader.ReadAsync(binding, 0, cancellationToken).ConfigureAwait(false);
                if (current is null || !Safe(current.Candidate, scope) ||
                    !PassageRanking.SamePassage(current.Candidate, passage) ||
                    current.Text != passage.Content || current.StartOffset != passage.StartOffset || current.DisclosureText is null ||
                    disclosure.EvaluateCodeGuard(current.DisclosureText, LocalDisclosureKind.RetainedDetail,
                        current.GuardProof, current.StartOffset, current.Text.Length).Withheld ||
                    !NativeV1EnvelopeProtector.CanDiscloseResult(JsonSerializer.SerializeToElement(current.DisclosureText))) continue;
                var citation = CorpusCitationMapper.Map(current.DocumentMetadataJson, passage.StartOffset, passage.Length, passage.SourceIdentity);
                var explanation = new List<string>();
                if (ranked.ExactBodyMatch) explanation.Add("exact:ordinal");
                if (ranked.LexicalRank.HasValue) explanation.Add(passage.Content.Contains(query, StringComparison.Ordinal)
                    ? "lexical:body" : "lexical:full-text");
                if (ranked.SemanticRank.HasValue) explanation.Add("semantic:dense");
                if (ranked.SourceDirectedRank.HasValue) explanation.Add("semantic:source-directed");
                if (!literal) explanation.Add("fusion:rrf");
                if (ranked.RerankerScore.HasValue) explanation.Add("rerank:trained");
                explanation.AddRange(citation.Warnings);
                var hit = new CorpusSearchHit(evidence.Encode(binding), passage.SourceIdentity, passage.RootId, passage.OwnerSourceRevisionId,
                    passage.PipelineRecordId, passage.PipelineRecordRevision, Path.GetFileName(passage.SourceIdentity), passage.ChunkId,
                    passage.ChunkHash, passage.StartOffset, passage.Length, passage.Content, citation.Locations,
                    passage.OriginKind == 3 ? "metadata" : citation.ExtractionMethod, explanation);
                if (!NativeV1EnvelopeProtector.CanDiscloseResult(JsonSerializer.SerializeToElement(hit))) continue;
                hits.Add(hit); bindings.Add(binding); accepted.Add(passage); perDocument[document] = perDocument.GetValueOrDefault(document) + 1;
                break;
            }
        }
        if (bindings.Count > 0)
        {
            var valid = await reader.ValidateCurrentBindingsAsync(bindings, cancellationToken).ConfigureAwait(false);
            if (hits.RemoveAll(hit => !valid.Contains(hit.ChunkId)) > 0) warnings.Add("publication-changed");
            if (hits.Count == 0) throw new PassageRetrievalRefusalException("index-updating");
        }
        if (hits.Count < limit && shortlist.Count > 0) warnings.Add("shortlist-depleted");
        var response = new CorpusSearchResponse(hits, new(scope.Kind, scope.RootIds, scope.CanonicalCwd),
            semanticStatus == "ready" ? "hybrid" : "lexical", semanticStatus, generation, warnings) { CandidateCount = candidateCount };
        if (!NativeV1EnvelopeProtector.CanDiscloseResult(JsonSerializer.SerializeToElement(response)))
            throw new NativeOperationException("content-withheld");
        return response;
    }

    private bool Safe(EligiblePassageCandidate passage, ResolvedCorpusScope scope) =>
        passage.ChunkId > 0 && passage.CorpusEpoch != Guid.Empty && passage.Length is >= 1 and <= 1024 &&
        passage.Content.Length == passage.Length && passage.StartOffset >= 0 && Hash(passage.Content) == passage.ChunkHash &&
        passage.PassagePolicyFingerprint.Length == 64 && Hash(SearchText(passage)) == passage.SearchInputHash &&
        (scope.Kind == "all" || passage.RootId.HasValue && scope.RootIds.Contains(passage.RootId.Value)) &&
        (scope.Kind != "workspace" || scope.CanonicalCwd is { } cwd &&
            (passage.SourceIdentity.Equals(cwd, StringComparison.OrdinalIgnoreCase) ||
             passage.SourceIdentity.StartsWith(cwd.TrimEnd('\\') + '\\', StringComparison.OrdinalIgnoreCase))) &&
        !disclosure.EvaluateCode(SearchText(passage), LocalDisclosureKind.RetainedDetail, passage.DisclosureProof,
            passage.ContextHeader.Length == 0 ? 0 : passage.ContextHeader.Length + 1).Withheld &&
        !disclosure.Evaluate(passage.ContextHeader, LocalDisclosureKind.CorpusMetadata).Withheld &&
        !disclosure.Evaluate(passage.SourceIdentity, LocalDisclosureKind.CorpusMetadata).Withheld &&
        !disclosure.Evaluate(Path.GetFileName(passage.SourceIdentity), LocalDisclosureKind.CorpusMetadata).Withheld &&
        NativeV1EnvelopeProtector.CanDiscloseResult(JsonSerializer.SerializeToElement(SearchText(passage)));

    private ValueTask<IReadOnlyList<string>> LexicalTermsAsync(string query, IReadOnlyList<EligiblePassageCandidate> lexical,
        CancellationToken cancellationToken) => lexical.Any(value => value.FullTextRank > 0)
        ? reader.GetLexicalTermsAsync(query, cancellationToken) : ValueTask.FromResult<IReadOnlyList<string>>([]);

    private static bool LexicalMatch(EligiblePassageCandidate passage, string query, IReadOnlyList<string> terms) =>
        passage.Content.Contains(query, StringComparison.Ordinal) || passage.FullTextRank > 0 &&
        (CorpusRetrievalService.FindLexicalAnchor(passage.Content, terms) >= 0 ||
         CorpusRetrievalService.FindLexicalAnchor(passage.ContextHeader, terms) >= 0);

    private static List<string> BaseWarnings(CorpusLexicalReadiness readiness, int lexicalCount, int denseCount)
    {
        var warnings = new List<string>();
        if (!readiness.PopulationComplete) warnings.Add("full-text-populating");
        if (lexicalCount >= 100 || denseCount >= 100) warnings.Add("candidate-budget-reached");
        return warnings;
    }
    private static string SearchText(EligiblePassageCandidate passage) => passage.ContextHeader.Length == 0
        ? passage.Content : passage.ContextHeader + "\n" + passage.Content;
    private static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
