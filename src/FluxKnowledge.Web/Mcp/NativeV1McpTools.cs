using System.ComponentModel;
using System.Text.Json;
using FluxKnowledge.Application.IntegrationV1;
using FluxKnowledge.Application.Mcp;
using FluxKnowledge.Web.NativeV1;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace FluxKnowledge.Web.Mcp;

/// <summary>The native v1 MCP surface. It is intentionally a direct facade binding, not a compatibility layer.</summary>
public sealed class NativeV1McpTools
{
    private readonly INativeV1Facade _facade;
    private readonly NativeV1RequestMapper _mapper;
    private readonly ReadonlyMcpRetryExecutor _retryExecutor;

    public NativeV1McpTools(INativeV1Facade facade, NativeV1RequestMapper mapper, ReadonlyMcpRetryExecutor retryExecutor)
    {
        _facade = facade ?? throw new ArgumentNullException(nameof(facade));
        _mapper = mapper ?? throw new ArgumentNullException(nameof(mapper));
        _retryExecutor = retryExecutor ?? throw new ArgumentNullException(nameof(retryExecutor));
    }

    [McpServerTool(Name = "knowledge.search")]
    [Description("Search retained native knowledge.")]
    public Task<CallToolResult> KnowledgeSearch(string query, int limit, CancellationToken cancellationToken = default) =>
        QueryAsync("knowledge.search", new { query, limit }, cancellationToken);

    [McpServerTool(Name = "knowledge.graph")]
    [Description("Traverse bounded native knowledge relationships.")]
    public Task<CallToolResult> KnowledgeGraph(string node, int max_depth, int max_results, CancellationToken cancellationToken = default) =>
        QueryAsync("knowledge.graph", new { node, max_depth, max_results }, cancellationToken);

    [McpServerTool(Name = "code.query")]
    [Description("Read bounded native retained-code projections.")]
    public Task<CallToolResult> CodeQuery(string view, string? query, string? branch_id, int limit, string? cursor, CancellationToken cancellationToken = default) =>
        QueryAsync("code.query", new { view, query, branch_id, limit, cursor }, cancellationToken);

    [McpServerTool(Name = "corpus.query")]
    [Description("Read bounded native corpus projections.")]
    public Task<CallToolResult> CorpusQuery(string view, string? root_id, string? branch_id, string? job_id, int limit, string? cursor, CancellationToken cancellationToken = default) =>
        QueryAsync("corpus.query", new { view, root_id, branch_id, job_id, limit, cursor }, cancellationToken);

    [McpServerTool(Name = "operations.status")]
    [Description("Read a closed native operations status view.")]
    public Task<CallToolResult> OperationsStatus(string view, string? root_id, string? job_id, int limit, CancellationToken cancellationToken = default) =>
        QueryAsync("operations.status", new { view, root_id, job_id, limit }, cancellationToken);

    [McpServerTool(Name = "operations.audit")]
    [Description("Read bounded immutable native audit evidence.")]
    public Task<CallToolResult> OperationsAudit(string view, string? root_id, string? job_id, int limit, string? cursor, CancellationToken cancellationToken = default) =>
        QueryAsync("operations.audit", new { view, root_id, job_id, limit, cursor }, cancellationToken);

    [McpServerTool(Name = "knowledge.write")]
    [Description("Preview or commit a closed native knowledge mutation.")]
    public Task<CallToolResult> KnowledgeWrite(
        string mode, string action, string? item_id, string? title, string? body, string? subject, string? predicate,
        string? object_text, string? transition, string? related_claim_id, string? reason, decimal? confidence,
        string? confirmation_id = null, string? idempotency_key = null, CancellationToken cancellationToken = default) =>
        ActionAsync("knowledge.write", mode, new { action, item_id, title, body, subject, predicate, object_text, transition, related_claim_id, reason, confidence, confirmation_id }, idempotency_key, cancellationToken);

    [McpServerTool(Name = "code.write")]
    [Description("Preview or commit privacy-safe native code retrieval feedback.")]
    public Task<CallToolResult> CodeWrite(string mode, JsonElement payload, string? confirmation_id = null, string? idempotency_key = null, CancellationToken cancellationToken = default) =>
        ActionAsync("code.write", mode, new { payload, confirmation_id }, idempotency_key, cancellationToken);

    [McpServerTool(Name = "corpus.write")]
    [Description("Preview or commit a closed native corpus command.")]
    public Task<CallToolResult> CorpusWrite(string mode, string action, JsonElement payload, string? confirmation_id = null, string? idempotency_key = null, CancellationToken cancellationToken = default) =>
        ActionAsync("corpus.write", mode, new { action, payload, confirmation_id }, idempotency_key, cancellationToken);

    private async Task<CallToolResult> QueryAsync(string toolName, object arguments, CancellationToken cancellationToken)
    {
        try
        {
            var request = _mapper.MapQuery(toolName, JsonSerializer.SerializeToElement(arguments));
            var execution = await _retryExecutor.ExecuteAsync(
                toolName,
                token => _facade.ExecuteQueryAsync(Family(toolName), request, token).AsTask(),
                cancellationToken).ConfigureAwait(false);
            return McpResultFactory.NativeJson(execution.Succeeded
                ? McpResultFactory.NativeSuccess(execution.Value!)
                : McpResultFactory.NativeFailure(execution.Failure!));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return McpResultFactory.NativeJson(McpResultFactory.NativeFailure(exception));
        }
    }

    private async Task<CallToolResult> ActionAsync(string toolName, string mode, object arguments, string? idempotencyKey, CancellationToken cancellationToken)
    {
        try
        {
            var payload = JsonSerializer.SerializeToElement(arguments);
            var command = _mapper.MapAction(toolName, payload);
            var family = _mapper.ActionFamily(toolName, command);
            if (string.Equals(mode, "preview", StringComparison.OrdinalIgnoreCase))
            {
                return McpResultFactory.NativeJson(McpResultFactory.NativeSuccess(
                    await _facade.PreviewAsync(family, command, "mcp", cancellationToken).ConfigureAwait(false)));
            }

            if (!string.Equals(mode, "commit", StringComparison.OrdinalIgnoreCase)) throw new NativeOperationException("invalid-mode");
            var confirmationId = _mapper.ConfirmationId(payload);
            if (string.IsNullOrWhiteSpace(confirmationId)) throw new NativeOperationException("confirmation-required");
            if (string.IsNullOrWhiteSpace(idempotencyKey)) throw new NativeOperationException("idempotency-key-required");
            return McpResultFactory.NativeJson(McpResultFactory.NativeSuccess(
                await _facade.CommitAsync(family, command, confirmationId, idempotencyKey, "mcp", cancellationToken).ConfigureAwait(false)));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return McpResultFactory.NativeJson(McpResultFactory.NativeFailure(exception));
        }
    }

    private static string Family(string toolName) => toolName switch
    {
        "knowledge.search" or "knowledge.write" => "knowledge",
        "knowledge.graph" => "graph",
        "code.query" or "code.write" => "code",
        "corpus.query" or "corpus.write" => "corpus",
        "operations.status" => "operations.status",
        "operations.audit" => "operations.audit",
        _ => throw new NativeOperationException("tool-not-allowed")
    };
}
