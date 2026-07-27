using FluxKnowledge.Application.Contracts;
using FluxKnowledge.Application.Search;
using Xunit;

namespace FluxKnowledge.Domain.Tests.Search;

public sealed class SearchQueryValidatorTests
{
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Validate_rejects_empty_trimmed_query(string query)
    {
        var error = Assert.Throws<SearchQueryValidationException>(
            () => SearchQueryValidator.Validate(new SearchRequest(query, 5, "local_first", null, null, null)));

        Assert.False(error.IsRetryable);
        Assert.Contains("query", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(51)]
    public void Validate_rejects_limits_outside_one_through_fifty(int limit)
    {
        var error = Assert.Throws<SearchQueryValidationException>(
            () => SearchQueryValidator.Validate(new SearchRequest("restart", limit, "local_first", null, null, null)));

        Assert.False(error.IsRetryable);
    }

    [Fact]
    public void Validate_rejects_scope_that_would_change_phase_one_semantics()
    {
        var error = Assert.Throws<SearchQueryValidationException>(
            () => SearchQueryValidator.Validate(
                new SearchRequest("restart", 5, "workspace_boosted", "C:/workspace", null, null)));

        Assert.False(error.IsRetryable);
        Assert.True(error.IsUnsupportedScope);
        Assert.Contains("unsupported", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_accepts_local_first_request_without_scope_filters()
    {
        var request = SearchQueryValidator.Validate(
            new SearchRequest("  restart  ", 5, "local_first", null, null, null));

        Assert.Equal("restart", request.Query);
        Assert.Equal(5, request.Limit);
    }
}
