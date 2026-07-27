using FluxKnowledge.Application.Contracts;

namespace FluxKnowledge.Application.Search;

public sealed class SearchQueryValidationException(
    string message,
    bool isUnsupportedScope = false) : ArgumentException(message)
{
    public bool IsRetryable => false;
    public bool IsUnsupportedScope { get; } = isUnsupportedScope;
}

public static class SearchQueryValidator
{
    public static SearchRequest Validate(SearchRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var query = request.Query?.Trim();
        if (string.IsNullOrWhiteSpace(query))
        {
            throw new SearchQueryValidationException("Search query must contain non-whitespace text.");
        }
        if (request.Limit is < 1 or > 50)
        {
            throw new SearchQueryValidationException("Search limit must be between 1 and 50.");
        }
        if (request.ScopeMode is not ("local_first" or "workspace_boosted"))
        {
            throw new SearchQueryValidationException("Search scope_mode must be local_first or workspace_boosted.");
        }
        ValidateFilters(request.Filters);

        if (request.ScopeMode == "workspace_boosted" ||
            !string.IsNullOrWhiteSpace(request.Cwd) ||
            !string.IsNullOrWhiteSpace(request.RootName) ||
            request.Filters is { Count: > 0 })
        {
            throw new SearchQueryValidationException(
                "The requested search scope is unsupported in the Phase 1 local corpus and will not be broadened.",
                isUnsupportedScope: true);
        }

        return request with { Query = query };
    }

    private static void ValidateFilters(IReadOnlyDictionary<string, IReadOnlyList<string>>? filters)
    {
        if (filters is null)
        {
            return;
        }

        foreach (var filter in filters)
        {
            if (string.IsNullOrWhiteSpace(filter.Key) ||
                filter.Value is null ||
                filter.Value.Count == 0 ||
                filter.Value.Any(string.IsNullOrWhiteSpace))
            {
                throw new SearchQueryValidationException("Search filters are malformed.");
            }
        }
    }
}
