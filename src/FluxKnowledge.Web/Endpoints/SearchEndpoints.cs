using FluxKnowledge.Application.Contracts;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Search;

namespace FluxKnowledge.Web.Endpoints;

public static class SearchEndpoints
{
    private static readonly HashSet<string> KnownParameters = new(StringComparer.OrdinalIgnoreCase)
    {
        "query", "limit", "scope_mode", "cwd", "root_name"
    };

    public static IEndpointRouteBuilder MapFluxKnowledgeSearch(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/search", SearchAsync);
        return endpoints;
    }

    private static async Task<IResult> SearchAsync(
        HttpRequest request,
        ISearchService searchService,
        CancellationToken cancellationToken)
    {
        var values = request.Query;
        var limit = int.TryParse(values["limit"], out var parsedLimit) ? parsedLimit : 0;
        var filters = values
            .Where(pair => !KnownParameters.Contains(pair.Key))
            .ToDictionary(
                static pair => pair.Key,
                static pair => (IReadOnlyList<string>)pair.Value
                    .Where(static value => value is not null)
                    .Select(static value => value!)
                    .ToArray(),
                StringComparer.OrdinalIgnoreCase);
        var searchRequest = new SearchRequest(
            values["query"].ToString(),
            limit == 0 && !values.ContainsKey("limit") ? 10 : limit,
            values["scope_mode"].FirstOrDefault() ?? "local_first",
            values["cwd"].FirstOrDefault(),
            values["root_name"].FirstOrDefault(),
            filters.Count == 0 ? null : filters);

        try
        {
            var response = await searchService.SearchAsync(searchRequest, cancellationToken).ConfigureAwait(false);
            return Results.Ok(response);
        }
        catch (SearchQueryValidationException exception)
        {
            return Results.Problem(
                detail: exception.Message,
                statusCode: StatusCodes.Status400BadRequest,
                title: exception.IsUnsupportedScope ? "Unsupported search scope" : "Invalid search request",
                type: exception.IsUnsupportedScope
                    ? "https://fluxknowledge.dev/problems/unsupported-search-scope"
                    : "https://fluxknowledge.dev/problems/invalid-search-request",
                extensions: new Dictionary<string, object?>
                {
                    ["retryable"] = exception.IsRetryable,
                    ["code"] = exception.IsUnsupportedScope ? "unsupported_scope" : "invalid_search_request"
                });
        }
    }
}
