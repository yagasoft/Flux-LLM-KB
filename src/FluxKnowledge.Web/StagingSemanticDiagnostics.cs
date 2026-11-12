using System.Text;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Contracts;
using FluxKnowledge.Application.IntegrationV1;
using FluxKnowledge.Application.Pipeline;
using FluxKnowledge.Application.Search;
using FluxKnowledge.Web.NativeV1;
using System.Text.Json;

namespace FluxKnowledge.Web;

/// <summary>Only mapped by the explicit real-model staging executable, for an independent vector oracle.</summary>
internal static class StagingSemanticDiagnostics
{
    internal sealed record QueryRequest(string? Query, bool ExactInput = false);
    internal sealed record RerankRequest(string? Query, IReadOnlyList<RerankPassage>? Passages);

    internal static async Task<IResult> PayloadReadinessAsync(JsonElement input,HybridPassageRetrievalEngine engine,
        DateTimeOffset arrival,TimeProvider clock,CancellationToken caller)
    {
        var capture=new StagingPayloadReadinessRanker(clock);
        ExpandedPipelineDiagnostic? latest=null;
        try
        {
            var request=(CorpusSearchRequest)new NativeV1RequestMapper().MapQuery("corpus.search",input);
            var result=await engine.SearchExpandedAsync(request,capture,arrival,caller,snapshot => latest=snapshot);
            return Results.Ok(new {diagnostic="payload-readiness-only",gpuExecuted=false,rankingRequested=false,attempts=capture.Snapshot(),diagnostics=latest,result});
        }
        catch(NativeOperationException){return Results.BadRequest();}
        catch(PassageRetrievalRefusalException refusal)
        {return Results.Json(new {diagnostic="payload-readiness-only",gpuExecuted=false,rankingRequested=false,attempts=capture.Snapshot(),diagnostics=latest,semanticStatus=refusal.Status},statusCode:503);}
    }

    internal static async Task<IResult> ExpandedGpuAsync(JsonElement input, HybridPassageRetrievalEngine engine,
        IExpandedPassageRanker ranker, DateTimeOffset arrival, IHostApplicationLifetime lifetime, CancellationToken caller)
    {
        try
        {
            var request = (CorpusSearchRequest)new NativeV1RequestMapper().MapQuery("corpus.search",input);
            return Results.Ok(await engine.SearchExpandedAsync(request,ranker,arrival,caller));
        }
        catch (NativeOperationException) { return Results.BadRequest(); }
        catch (PassageRetrievalRefusalException refusal)
        { return Results.Json(new { semanticStatus=refusal.Status,retryable=refusal.Retryable,servingQualified=false },statusCode:503); }
        catch (PublicationSnapshotConflictException)
        { return Results.Json(new { semanticStatus="index-updating",retryable=true,servingQualified=false },statusCode:503); }
        catch (PassageInferenceOwnershipException)
        {
            lifetime.StopApplication();
            return Results.Json(new { semanticStatus="ownership-unconfirmed",retryable=false,servingQualified=false },statusCode:503);
        }
    }

    internal static async Task<IResult> CandidateQualityAsync(JsonElement input, HybridPassageRetrievalEngine engine,
        CancellationToken caller)
    {
        try
        {
            var request = (CorpusSearchRequest)new NativeV1RequestMapper().MapQuery("corpus.search", input);
            if (request.Scope is not ("root" or "workspace") || request.MatchMode != "keyword")
                return Results.BadRequest();
            return Results.Ok(await engine.SearchCandidateQualityAsync(request, caller));
        }
        catch (NativeOperationException)
        { return Results.BadRequest(); }
        catch (PassageRetrievalRefusalException refusal)
        {
            caller.ThrowIfCancellationRequested();
            return Results.Json(new { semanticStatus = refusal.Status, retryable = refusal.Retryable, servingQualified = false }, statusCode: 503);
        }
        catch (PublicationSnapshotConflictException)
        {
            caller.ThrowIfCancellationRequested();
            return Results.Json(new { semanticStatus = "index-updating", retryable = true, servingQualified = false }, statusCode: 503);
        }
    }

    // Diagnostic completion timing only; the serving search/rerank deadlines are unchanged.
    internal static async Task<IResult> RerankAsync(RerankRequest request, IScheduledPassageInference inference, CancellationToken caller)
    {
        if (string.IsNullOrWhiteSpace(request.Query) || request.Query.Length > 2048 ||
            request.Passages is not { Count: >= 1 and <= 50 } passages ||
            passages.Any(p => p is null || p.PassageId <= 0 || string.IsNullOrWhiteSpace(p.SearchText) || p.SearchText.Length > 16384) ||
            passages.Select(p => p.PassageId).Distinct().Count() != passages.Count) return Results.BadRequest();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(caller);
        deadline.CancelAfter(TimeSpan.FromSeconds(60));
        try
        {
            var result = await inference.ExecuteAsync((_, reranker, ct) => reranker.RerankAsync(request.Query, passages, ct), deadline.Token);
            deadline.Token.ThrowIfCancellationRequested();
            return Results.Ok(new { result.ModelFingerprint, result.Scores });
        }
        catch (PassageRetrievalRefusalException refusal)
        { return Results.Json(new { semanticStatus = refusal.Status }, statusCode: 503); }
        catch (OperationCanceledException) when (!caller.IsCancellationRequested)
        { return Results.Json(new { semanticStatus = "timeout" }, statusCode: 503); }
    }

    internal static async Task<IResult> EmbedAsync(QueryRequest request, IScheduledPassageInference inference, CancellationToken caller)
    {
        var query = request.ExactInput ? request.Query : request.Query?.Trim().Normalize(NormalizationForm.FormC);
        if (string.IsNullOrWhiteSpace(query) || query.Length > 2048) return Results.BadRequest();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(caller);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            var vector = await inference.ExecuteAsync((embedding, _, ct) => embedding.CreateEmbeddingAsync(query, ct), deadline.Token);
            deadline.Token.ThrowIfCancellationRequested();
            return Results.Ok(new { vector.ModelFingerprint, vector.Values });
        }
        catch (PassageRetrievalRefusalException refusal)
        {
            caller.ThrowIfCancellationRequested();
            return Results.Json(new { semanticStatus = refusal.Status }, statusCode: 503);
        }
        catch (OperationCanceledException) when (!caller.IsCancellationRequested)
        { return Results.Json(new { semanticStatus = "timeout" }, statusCode: 503); }
    }
}
