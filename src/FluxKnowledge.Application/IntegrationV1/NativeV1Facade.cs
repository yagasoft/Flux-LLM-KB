using FluxKnowledge.Application.IntegrationV1.Code;
using FluxKnowledge.Application.IntegrationV1.Corpus;
using FluxKnowledge.Application.IntegrationV1.Operations;
using FluxKnowledge.Application.Knowledge;

namespace FluxKnowledge.Application.IntegrationV1;

public interface INativeV1Facade
{
    ValueTask<object> ExecuteQueryAsync(string family, object request, CancellationToken cancellationToken);
    ValueTask<NativeActionPreview> PreviewAsync(string family, object command, string surface, CancellationToken cancellationToken);
    ValueTask<NativeActionReceipt> CommitAsync(string family, object command, string confirmationId, string idempotencyKey, string surface, CancellationToken cancellationToken);
}

public sealed record NativeKnowledgeQuery(string Query, int Limit);
public sealed record NativeGraphQuery(string Node, int MaxDepth, int MaxResults);

/// <summary>Single transport-neutral discriminator gate for the native v1 Application surface.</summary>
public sealed class NativeV1Facade(
    NativeCorpusQueryService corpusQueries,
    NativeCorpusCommandService corpusCommands,
    NativeCodeQueryService codeQueries,
    NativeCodeFeedbackService codeFeedback,
    NativeOperationsStatusService status,
    NativeAuditQueryService audit,
    IKnowledgeQueryService knowledgeQueries,
    IKnowledgeCommandService knowledgeCommands) : INativeV1Facade
{
    public async ValueTask<object> ExecuteQueryAsync(string family, object request, CancellationToken cancellationToken) =>
        CanonicalFamily(family) switch
        {
            "knowledge" when request is NativeKnowledgeQuery knowledge => await SearchAsync(knowledge, cancellationToken).ConfigureAwait(false),
            "graph" when request is NativeGraphQuery graph => await GraphAsync(graph, cancellationToken).ConfigureAwait(false),
            "corpus" when request is NativeCorpusQuery corpus => await corpusQueries.ExecuteAsync(corpus, cancellationToken).ConfigureAwait(false),
            "code" when request is NativeCodeQuery code => await codeQueries.ExecuteAsync(code, cancellationToken).ConfigureAwait(false),
            "operations.status" when request is NativeOperationsStatus operationStatus => await status.ExecuteAsync(operationStatus, cancellationToken).ConfigureAwait(false),
            "operations.audit" when request is NativeAuditQuery auditQuery => await audit.ExecuteAsync(auditQuery, cancellationToken).ConfigureAwait(false),
            "knowledge" or "graph" or "corpus" or "code" or "operations.status" or "operations.audit" => throw new NativeOperationException("invalid-request"),
            _ => throw new NativeOperationException("family-not-allowed")
        };

    public ValueTask<NativeActionPreview> PreviewAsync(string family, object command, string surface, CancellationToken cancellationToken) =>
        CanonicalFamily(family) switch
        {
            "knowledge" when command is KnowledgeMutation knowledge && knowledge.Action is "note_create" or "forget" => knowledgeCommands.PreviewAsync(knowledge, surface, cancellationToken),
            "graph" when command is KnowledgeMutation graph && graph.Action is "claim_upsert" or "claim_transition" => knowledgeCommands.PreviewAsync(graph, surface, cancellationToken),
            "corpus" when command is NativeCorpusMutation corpus => corpusCommands.PreviewAsync(corpus, surface, cancellationToken),
            "code" when command is NativeCodeFeedbackMutation feedback => codeFeedback.PreviewAsync(feedback, surface, cancellationToken),
            "knowledge" or "graph" or "corpus" or "code" => throw new NativeOperationException("invalid-request"),
            _ => throw new NativeOperationException("family-not-allowed")
        };

    public ValueTask<NativeActionReceipt> CommitAsync(string family, object command, string confirmationId, string idempotencyKey, string surface, CancellationToken cancellationToken) =>
        CanonicalFamily(family) switch
        {
            "knowledge" when command is KnowledgeMutation knowledge && knowledge.Action is "note_create" or "forget" => knowledgeCommands.CommitAsync(knowledge, confirmationId, idempotencyKey, surface, cancellationToken),
            "graph" when command is KnowledgeMutation graph && graph.Action is "claim_upsert" or "claim_transition" => knowledgeCommands.CommitAsync(graph, confirmationId, idempotencyKey, surface, cancellationToken),
            "corpus" when command is NativeCorpusMutation corpus => corpusCommands.CommitAsync(corpus, confirmationId, idempotencyKey, surface, cancellationToken),
            "code" when command is NativeCodeFeedbackMutation feedback => codeFeedback.CommitAsync(feedback, confirmationId, idempotencyKey, surface, cancellationToken),
            "knowledge" or "graph" or "corpus" or "code" => throw new NativeOperationException("invalid-request"),
            _ => throw new NativeOperationException("family-not-allowed")
        };

    private static string CanonicalFamily(string? family)
    {
        var canonical = family?.Trim().ToLowerInvariant();
        return canonical is "knowledge" or "graph" or "corpus" or "code" or "operations.status" or "operations.audit"
            ? canonical
            : string.Empty;
    }

    private async ValueTask<object> SearchAsync(NativeKnowledgeQuery request, CancellationToken cancellationToken)
    {
        if (request.Limit is < 1 or > 100) throw new NativeOperationException("invalid-query");
        var canonicalQuery = NativeV1ContractLimits.CanonicalizeKnowledgeQuery(request.Query);
        return await knowledgeQueries.SearchAsync(canonicalQuery, request.Limit, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<object> GraphAsync(NativeGraphQuery request, CancellationToken cancellationToken)
    {
        if (request.MaxDepth is < 1 or > 8 || request.MaxResults is < 1 or > 100) throw new NativeOperationException("invalid-query");
        var canonicalNode = NativeV1ContractLimits.CanonicalizeGraphNode(request.Node);
        return await knowledgeQueries.GraphAsync(canonicalNode, request.MaxDepth, request.MaxResults, cancellationToken).ConfigureAwait(false);
    }
}
