using System.Text.Json;
using FluxKnowledge.Application.Contracts;
using FluxKnowledge.Application.IntegrationV1;
using FluxKnowledge.Web.NativeV1;
using Xunit;

namespace FluxKnowledge.Web.Tests.Mcp;

public sealed class CorpusMatchModeMappingTests
{
    [Fact]
    public void Literal_query_keeps_edge_spaces_and_decomposed_unicode_in_native_payload()
    {
        const string query = " e\u0301 ";
        var request = Assert.IsType<CorpusSearchRequest>(new NativeV1RequestMapper().MapQuery("corpus.search",
            JsonSerializer.SerializeToElement(new { query, scope = "all", match_mode = "literal" })));
        Assert.Equal(query, request.Query);
        Assert.Equal("literal", JsonSerializer.SerializeToElement(request).GetProperty("MatchMode").GetString());
    }

    [Fact]
    public void Omitted_match_mode_uses_the_keyword_default_and_existing_normalisation()
    {
        var request = Assert.IsType<CorpusSearchRequest>(new NativeV1RequestMapper().MapQuery("corpus.search",
            JsonSerializer.SerializeToElement(new { query = " e\u0301 ", scope = "all" })));
        Assert.Equal("é", request.Query);
        Assert.Equal("keyword", JsonSerializer.SerializeToElement(request).GetProperty("MatchMode").GetString());
    }

    [Theory]
    [InlineData("KEYWORD")]
    [InlineData("unknown")]
    [InlineData("")]
    public void Unknown_match_modes_are_invalid_requests(string matchMode)
    {
        var error = Assert.Throws<NativeOperationException>(() => new NativeV1RequestMapper().MapQuery("corpus.search",
            JsonSerializer.SerializeToElement(new { query = "query", scope = "all", match_mode = matchMode })));
        Assert.Equal("invalid-request", error.ReasonCode);
    }
}
