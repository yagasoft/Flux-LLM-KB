using System.ComponentModel;
using FluxKnowledge.Application.Contracts;
using FluxKnowledge.Application.Mcp;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Sources;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace FluxKnowledge.Web.Mcp;

public sealed class KnowledgeMcpTools
{
    private readonly ReadonlyMcpRetryExecutor _retryExecutor;
    private readonly IServiceScopeFactory _scopeFactory;

    public KnowledgeMcpTools(IServiceScopeFactory scopeFactory, ReadonlyMcpRetryExecutor retryExecutor)
    {
        _scopeFactory = scopeFactory;
        _retryExecutor = retryExecutor;
    }

    [McpServerTool(Name = "kb.search")]
    [Description("Search the local Phase 1 knowledge corpus.")]
    public Task<CallToolResult> Search(
        [Description("Search text.")] string query,
        [Description("Maximum result count.")] int limit = 5,
        [Description("Optional workspace path.")] string? cwd = null,
        [Description("Optional indexed root name.")] string? root_name = null,
        [Description("Search scope.")] string scope_mode = "local_first",
        [Description("Optional filters.")] IReadOnlyDictionary<string, IReadOnlyList<string>>? filters = null,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(
            "kb.search",
            new SearchRequest(query, limit, scope_mode, cwd, root_name, filters),
            response => McpResultFactory.Json(response),
            cancellationToken);

    [McpServerTool(Name = "kb.brief")]
    [Description("Create a concise plain-text brief from local search results.")]
    public Task<CallToolResult> Brief(
        [Description("Search text.")] string query,
        [Description("Maximum brief token budget.")] int token_budget = 1200,
        [Description("Optional workspace path.")] string? cwd = null,
        [Description("Optional indexed root name.")] string? root_name = null,
        [Description("Search scope.")] string scope_mode = "local_first",
        [Description("Optional filters.")] IReadOnlyDictionary<string, IReadOnlyList<string>>? filters = null,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(
            "kb.brief",
            new SearchRequest(query, 5, scope_mode, cwd, root_name, filters),
            response => McpResultFactory.Text(CreateBrief(response, token_budget)),
            cancellationToken);

    [McpServerTool(Name = "kb.retained_csharp_detail")]
    [Description("Read one verified retained C# branch's trusted-local code facts.")]
    public async Task<CallToolResult> RetainedCsharpDetail(
        [Description("Retained processor branch identifier.")] Guid branch_id,
        [Description("Exclusive symbol ordinal continuation.")] int? symbol_after_ordinal = null,
        [Description("Exclusive relationship ordinal continuation.")] int? reference_after_ordinal = null,
        [Description("Exclusive diagnostic ordinal continuation.")] int? diagnostic_after_ordinal = null,
        CancellationToken cancellationToken = default)
    {
        var execution = await _retryExecutor.ExecuteAsync(
            "kb.retained_csharp_detail",
            async token =>
            {
                await using var scope = _scopeFactory.CreateAsyncScope();
                return await scope.ServiceProvider.GetRequiredService<ILocalRetainedCsharpCodeReader>()
                    .ReadPageAsync(
                        branch_id,
                        new LocalRetainedCsharpCodePageRequest(
                            symbol_after_ordinal,
                            reference_after_ordinal,
                            diagnostic_after_ordinal),
                        token)
                    .ConfigureAwait(false);
            },
            cancellationToken).ConfigureAwait(false);

        if (!execution.Succeeded)
        {
            return McpResultFactory.Failure("kb.retained_csharp_detail", execution.Failure!);
        }

        return execution.Value is null
            ? McpResultFactory.Json(new { reasonCode = "retained-csharp-code-unavailable" })
            : McpResultFactory.Json(execution.Value);
    }

    // Keeps direct in-process callers source-compatible while the MCP tool accepts paging fields.
    public Task<CallToolResult> RetainedCsharpDetail(Guid branchId, CancellationToken cancellationToken) =>
        RetainedCsharpDetail(branchId, null, null, null, cancellationToken);

    [McpServerTool(Name = "kb.retained_csharp_search")]
    [Description("Search trusted-local facts from checksum-verified retained C# branches.")]
    public async Task<CallToolResult> RetainedCsharpSearch(
        [Description("C# symbol or signature text to search.")] string query,
        [Description("Maximum result count.")] int limit = 10,
        [Description("Opaque query-bound cursor returned by the preceding matching durable-fact page.")] string? cursor = null,
        CancellationToken cancellationToken = default)
    {
        var execution = await _retryExecutor.ExecuteAsync(
            "kb.retained_csharp_search",
            async token =>
            {
                await using var scope = _scopeFactory.CreateAsyncScope();
                return await scope.ServiceProvider.GetRequiredService<ILocalRetainedCsharpCodeReader>()
                    .SearchPageAsync(
                        new LocalRetainedCsharpCodeSearchPageRequest(
                            query,
                            limit,
                            cursor is null ? null : new LocalRetainedCsharpCodeSearchCursor(cursor)),
                        token)
                    .ConfigureAwait(false);
            },
            cancellationToken).ConfigureAwait(false);

        if (execution.Succeeded)
        {
            return McpResultFactory.Json(execution.Value!);
        }

        return execution.Failure is LocalRetainedCsharpCodeSearchCursorException
            ? McpResultFactory.Json(new { reasonCode = LocalRetainedCsharpCodeSearchCursorException.ReasonCode })
            : McpResultFactory.Failure("kb.retained_csharp_search", execution.Failure!);
    }

    // Keeps direct in-process callers source-compatible while the MCP tool accepts paging fields.
    public Task<CallToolResult> RetainedCsharpSearch(string query, int limit, CancellationToken cancellationToken) =>
        RetainedCsharpSearch(query, limit, null, cancellationToken);

    private async Task<CallToolResult> ExecuteAsync(
        string toolName,
        SearchRequest request,
        Func<SearchResponse, CallToolResult> success,
        CancellationToken cancellationToken)
    {
        var execution = await _retryExecutor.ExecuteAsync(
            toolName,
            async token => await SearchAsync(request, token).ConfigureAwait(false),
            cancellationToken).ConfigureAwait(false);

        return execution.Succeeded
            ? success(execution.Value!)
            : McpResultFactory.Failure(toolName, execution.Failure!);
    }

    private async Task<SearchResponse> SearchAsync(SearchRequest request, CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var searchService = scope.ServiceProvider.GetRequiredService<ISearchService>();
        return await searchService.SearchAsync(request, cancellationToken).ConfigureAwait(false);
    }

    private static string CreateBrief(SearchResponse response, int tokenBudget)
    {
        var remainingTokens = Math.Max(0, tokenBudget);
        var entries = new List<string>();
        foreach (var hit in response.Results)
        {
            var entry = $"{hit.Title}{Environment.NewLine}{hit.Snippet}";
            var estimatedTokens = (entry.Length + 3) / 4;
            if (estimatedTokens > remainingTokens)
            {
                break;
            }

            entries.Add(entry);
            remainingTokens -= estimatedTokens;
        }

        return string.Join($"{Environment.NewLine}{Environment.NewLine}", entries);
    }
}
