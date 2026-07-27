using System.ComponentModel;
using FluxKnowledge.Application.Contracts;
using FluxKnowledge.Application.Mcp;
using FluxKnowledge.Application.Ports;
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
