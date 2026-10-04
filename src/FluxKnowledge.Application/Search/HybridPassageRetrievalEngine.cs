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

/// <summary>One complete passage pipeline; captured SQL/native ownership lives inside scheduled work.</summary>
public sealed class HybridPassageRetrievalEngine(ICorpusRetrievalReader reader, IHybridPassageCandidateReader candidates,
    ICorpusGenerationLeaseStore leases, ICorpusAnnLeaseFactory annFactory, IScheduledPassageInference inference,
    IGpuInteractiveOwnerProbe owner, ICorpusEvidenceCodec evidence, ILocalPrivateContentDisclosure disclosure,
    TimeProvider? timeProvider = null, TimeSpan? searchTimeout = null) : IHybridPassageRetrieval
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
        return RunBoundedAsync(query, limit, scope.Kind, scope.CanonicalCwd,
            _ => ValueTask.FromResult<ResolvedCorpusScope?>(scope), cancellationToken);
    }

    public ValueTask<CorpusSearchResponse> SearchAsync(CorpusSearchRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var query = request.Query?.Trim().Normalize(NormalizationForm.FormC);
        if (string.IsNullOrWhiteSpace(query) || query.Length > 2048 || request.Limit is < 1 or > 50 ||
            request.Scope is not ("all" or "root" or "workspace") ||
            request.Scope == "all" && (request.RootId.HasValue || request.Cwd is not null) ||
            request.Scope == "root" && (!request.RootId.HasValue || request.Cwd is not null) ||
            request.Scope == "workspace" && (request.RootId.HasValue || string.IsNullOrWhiteSpace(request.Cwd)))
            throw new NativeOperationException("invalid-request");
        return RunBoundedAsync(query, request.Limit, request.Scope, request.Cwd,
            ct => reader.ResolveScopeAsync(request.Scope, request.RootId, request.Cwd, ct), cancellationToken);
    }

    private async ValueTask<CorpusSearchResponse> RunBoundedAsync(string query, int limit, string scopeKind, string? cwd,
        Func<CancellationToken, ValueTask<ResolvedCorpusScope?>> resolveScope, CancellationToken callerToken)
    {
        callerToken.ThrowIfCancellationRequested();
        var traceId = Activity.Current?.TraceId.ToString() ?? string.Empty;
        var spanId = Activity.Current?.SpanId.ToString() ?? string.Empty;
        var searchId = Guid.NewGuid().ToString("N");
        var timer = Stopwatch.StartNew();
        CorpusSearchResponse? returned = null;
        var deadline = new CancellationTokenSource(_searchTimeout, _clock);
        var budget = CancellationTokenSource.CreateLinkedTokenSource(callerToken, deadline.Token);
        ResolvedCorpusScope? resolved = null;
        var work = SearchCoreAsync(query, limit, resolveScope, scope => resolved = scope, traceId, spanId, searchId, budget.Token).AsTask();
        try { return returned = await work.WaitAsync(_searchTimeout, _clock, callerToken).ConfigureAwait(false); }
        catch (Exception exception) when (!callerToken.IsCancellationRequested &&
            (exception is TimeoutException || exception is OperationCanceledException && deadline.IsCancellationRequested))
        {
            var response = new CorpusSearchResponse([], new(resolved?.Kind ?? scopeKind, resolved?.RootIds ?? [], resolved?.CanonicalCwd ?? cwd),
                "lexical", "timeout", null, ["semantic:timeout", "search-deadline-exceeded"]);
            if (!NativeV1EnvelopeProtector.CanDiscloseResult(JsonSerializer.SerializeToElement(response)))
                throw new NativeOperationException("content-withheld");
            return returned = response;
        }
        finally
        {
            if (HybridSearchDiagnostics.Log.IsEnabled())
                HybridSearchDiagnostics.Log.Completed(traceId, spanId, searchId, returned?.SemanticStatus ?? "refused",
                    returned is null ? string.Empty : string.Join(',', returned.Results.Select(hit => hit.ChunkId)), timer.Elapsed.TotalMilliseconds);
            // Stops further useful work, never disposes a callback's native/SQL lease.
            budget.Cancel();
            _ = ObserveAndDisposeAsync(work, budget, deadline);
        }
    }

    private static async Task ObserveAndDisposeAsync(Task work, CancellationTokenSource budget, CancellationTokenSource deadline)
    {
        try { await work.ConfigureAwait(false); }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException) { }
        finally { budget.Dispose(); deadline.Dispose(); }
    }

    private async ValueTask<CorpusSearchResponse> SearchCoreAsync(string query, int limit,
        Func<CancellationToken, ValueTask<ResolvedCorpusScope?>> resolveScope, Action<ResolvedCorpusScope> scopeResolved,
        string traceId, string spanId, string searchId,
        CancellationToken cancellationToken)
    {
        var scope = await resolveScope(cancellationToken).ConfigureAwait(false) ?? throw new NativeOperationException("scope-unavailable");
        scopeResolved(scope);
        cancellationToken.ThrowIfCancellationRequested();
        var readiness = await reader.GetLexicalReadinessAsync(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (!readiness.IndexPresent) throw new NativeOperationException("lexical-unavailable");
        var lexicalTask = candidates.ReadLexicalCandidatesAsync(query, scope, cancellationToken).AsTask();
        _ = ObserveLateLexicalAsync(lexicalTask);
        var status = "unavailable";
        try
        {
            return await inference.ExecuteAsync(async (embedding, reranker, workToken) =>
            {
                workToken.ThrowIfCancellationRequested();
                // Acquiring inside the callback keeps the lease alive if the caller times out
                // while native work continues. The callback alone owns its final release.
                var sqlLease = await leases.TryAcquireAsync(_instance, owner.Current, inference.EmbeddingProfile.ModelFingerprint,
                    inference.EmbeddingProfile.Dimensions, workToken).ConfigureAwait(false);
                if (sqlLease is null) throw new PassageRetrievalRefusalException("index-updating");
                await using ICorpusGenerationLease lease = scope.Kind == "all"
                    ? await annFactory.OpenAsync(sqlLease, workToken).ConfigureAwait(false) : sqlLease;
                if (!await lease.IsCurrentAsync(workToken).ConfigureAwait(false)) throw new PassageRetrievalRefusalException("index-updating");
                var vector = await embedding.CreateEmbeddingAsync(query, workToken).ConfigureAwait(false);
                workToken.ThrowIfCancellationRequested();
                if (vector.ModelFingerprint != inference.EmbeddingProfile.ModelFingerprint || vector.Values.Count != inference.EmbeddingProfile.Dimensions ||
                    vector.Values.Any(value => !float.IsFinite(value)) || Math.Abs(vector.Values.Sum(value => (double)value * value) - 1) > 0.001)
                    throw new PassageRetrievalRefusalException("unavailable");
                var dense = await candidates.ReadDenseCandidatesAsync(lease, scope, vector.Values, workToken).ConfigureAwait(false);
                if (dense.Status != "ready") throw new PassageRetrievalRefusalException(dense.Status);
                var lexical = await lexicalTask.ConfigureAwait(false);
                var terms = await LexicalTermsAsync(query, lexical, workToken).ConfigureAwait(false);
                var lexicalEligible = lexical.Where(value => Safe(value, scope) && LexicalMatch(value, query, terms)).ToArray();
                var denseEligible = dense.Candidates.Where(value => Safe(value, scope)).ToArray();
                var shortlist = PassageRanking.Fuse(query, lexicalEligible, denseEligible);
                if (HybridSearchDiagnostics.Log.IsEnabled())
                    HybridSearchDiagnostics.Log.Candidates(traceId, spanId, searchId,
                        string.Join(',', lexicalEligible.Select(value => value.ChunkId)),
                        string.Join(',', denseEligible.Select(value => value.ChunkId)),
                        string.Join(',', shortlist.Select(value => value.Passage.ChunkId)));
                var candidateCount = lexicalEligible.Concat(denseEligible).Select(value => value.ChunkId).Distinct().Count();
                var warnings = BaseWarnings(readiness, lexical.Count, dense.Candidates.Count);
                IReadOnlyList<RankedPassage> ranked = shortlist;
                if (shortlist.Count > 0)
                {
                    try
                    {
                        var result = await reranker.RerankAsync(query, shortlist.Select(value =>
                            new RerankPassage(value.Passage.ChunkId, SearchText(value.Passage))).ToArray(), workToken).ConfigureAwait(false);
                        try { ranked = PassageRanking.ApplyScores(shortlist, result, inference.RerankerFingerprint); }
                        catch (InvalidOperationException) { warnings.Add("rerank:invalid-output"); }
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
                    { warnings.Add("rerank:unavailable"); }
                }
                var response = await AssembleAsync(query, scope, limit, ranked, "ready", lease.Generation.Id,
                    warnings, candidateCount, workToken).ConfigureAwait(false);
                if (!await lease.IsCurrentAsync(workToken).ConfigureAwait(false)) throw new PassageRetrievalRefusalException("index-updating");
                return response;
            }, cancellationToken).ConfigureAwait(false);
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
        var fallback = await lexicalTask.ConfigureAwait(false);
        var fallbackTerms = await LexicalTermsAsync(query, fallback, cancellationToken).ConfigureAwait(false);
        var fallbackEligible = fallback.Where(value => Safe(value, scope) && LexicalMatch(value, query, fallbackTerms)).ToArray();
        var fallbackRanking = PassageRanking.Fuse(query, fallbackEligible, []);
        var fallbackWarnings = BaseWarnings(readiness, fallback.Count, 0);
        fallbackWarnings.Add("semantic:" + status);
        return await AssembleAsync(query, scope, limit, fallbackRanking, status, null,
            fallbackWarnings, fallbackEligible.Select(value => value.ChunkId).Distinct().Count(), cancellationToken).ConfigureAwait(false);
    }

    private static async Task ObserveLateLexicalAsync(Task work)
    {
        try { await work.ConfigureAwait(false); }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException) { }
    }

    private async ValueTask<CorpusSearchResponse> AssembleAsync(string query, ResolvedCorpusScope scope, int limit,
        IReadOnlyList<RankedPassage> shortlist, string semanticStatus, Guid? generation,
        List<string> warnings, int candidateCount, CancellationToken cancellationToken)
    {
        var hits = new List<CorpusSearchHit>(limit);
        var accepted = new List<EligiblePassageCandidate>(limit);
        var perDocument = new Dictionary<(Guid?, Guid), int>();
        foreach (var ranked in shortlist)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (hits.Count == limit) break;
            var passage = ranked.Passage;
            var document = PassageRanking.DocumentIdentity(passage);
            if (perDocument.GetValueOrDefault(document) >= 2 || PassageRanking.IsNearDuplicate(passage, accepted)) continue;
            var binding = new CorpusEvidenceBinding(2, passage.RootId, passage.OwnerSourceRevisionId, Hash(passage.SourceIdentity),
                passage.PipelineRecordId, passage.PipelineRecordRevision, passage.ArtifactId, passage.ArtifactHash,
                passage.ChunkId, passage.ChunkHash, passage.StartOffset, passage.Length, passage.CorpusEpoch);
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
            explanation.Add("fusion:rrf");
            if (ranked.RerankerScore.HasValue) explanation.Add("rerank:trained");
            explanation.AddRange(citation.Warnings);
            var hit = new CorpusSearchHit(evidence.Encode(binding), passage.SourceIdentity, passage.RootId, passage.OwnerSourceRevisionId,
                passage.PipelineRecordId, passage.PipelineRecordRevision, Path.GetFileName(passage.SourceIdentity), passage.ChunkId,
                passage.ChunkHash, passage.StartOffset, passage.Length, passage.Content, citation.Locations,
                passage.OriginKind == 3 ? "metadata" : citation.ExtractionMethod, explanation);
            if (!NativeV1EnvelopeProtector.CanDiscloseResult(JsonSerializer.SerializeToElement(hit))) continue;
            hits.Add(hit); accepted.Add(passage); perDocument[document] = perDocument.GetValueOrDefault(document) + 1;
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
