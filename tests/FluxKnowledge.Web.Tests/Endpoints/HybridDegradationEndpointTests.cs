using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluxKnowledge.Application.Contracts;
using FluxKnowledge.Application.IntegrationV1;
using FluxKnowledge.Application.Knowledge;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Search;
using FluxKnowledge.Infrastructure.SqlServer.Visibility;
using FluxKnowledge.Web.Endpoints;
using FluxKnowledge.Web.Mcp;
using FluxKnowledge.Web.NativeV1;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FluxKnowledge.Web.Tests.Endpoints;

public sealed class HybridDegradationEndpointTests
{
    [Theory]
    [InlineData("busy")]
    [InlineData("timeout")]
    [InlineData("unavailable")]
    [InlineData("index-updating")]
    public async Task Empty_degraded_source_search_is_an_explicit_retryable_refusal_over_both_http_surfaces(string status)
    {
        var search = new PassageSearchService(new RefusedEngine(status));
        var knowledge = new KnowledgeQueryService(new Notes(), search, new LocalPrivateContentDisclosure());
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton<ISearchService>(search);
        builder.Services.AddSingleton<INativeV1Facade>(new KnowledgeFacade(knowledge));
        builder.Services.AddSingleton<NativeV1RequestMapper>();
        await using var app = builder.Build();
        app.MapFluxKnowledgeSearch();
        app.MapFluxKnowledgeNativeV1();
        await app.StartAsync();
        using var client = app.GetTestClient();
        using var legacy = await client.GetAsync("/api/search?query=paraphrase");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, legacy.StatusCode);
        using var problem = JsonDocument.Parse(await legacy.Content.ReadAsStringAsync());
        Assert.Equal("search-" + status, problem.RootElement.GetProperty("code").GetString());
        Assert.True(problem.RootElement.GetProperty("retryable").GetBoolean());
        using var native = await client.PostAsJsonAsync("/api/v1/knowledge/search", new { query = "paraphrase", limit = 10 });
        Assert.Equal(HttpStatusCode.ServiceUnavailable, native.StatusCode);
        using var envelope = JsonDocument.Parse(await native.Content.ReadAsStringAsync());
        Assert.False(envelope.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("search-" + status, envelope.RootElement.GetProperty("reasonCode").GetString());
        Assert.True(envelope.RootElement.GetProperty("retryable").GetBoolean());
        // MCP and REST share the native failure factory; remote CLI reads this envelope.
        var mcp = McpResultFactory.NativeFailure(new PassageRetrievalRefusalException(status));
        Assert.Equal("search-" + status, mcp.ReasonCode);
        Assert.True(mcp.Retryable);
    }

    private sealed class RefusedEngine(string status) : IHybridPassageRetrieval
    {
        public ValueTask<CorpusSearchResponse> SearchAsync(CorpusSearchRequest request, CancellationToken ct) =>
            ValueTask.FromResult(new CorpusSearchResponse([], new("all", [], null), "lexical", status, null, ["semantic:" + status]));
    }
    private sealed class Notes : IKnowledgeStore
    {
        public ValueTask<KnowledgeTarget?> FindTargetAsync(KnowledgeMutation mutation, CancellationToken ct) => ValueTask.FromResult<KnowledgeTarget?>(null);
        public ValueTask<IReadOnlyList<KnowledgeSearchResult>> SearchAsync(string query, int limit, CancellationToken ct) =>
            ValueTask.FromResult<IReadOnlyList<KnowledgeSearchResult>>([new(Guid.NewGuid(), "note", "Public note", "Safe stored note", "knowledge")]);
        public ValueTask<IReadOnlyList<KnowledgeGraphResult>> TraverseAsync(string node, int depth, int limit, CancellationToken ct) =>
            ValueTask.FromResult<IReadOnlyList<KnowledgeGraphResult>>([]);
    }
    private sealed class KnowledgeFacade(IKnowledgeQueryService service) : INativeV1Facade
    {
        public async ValueTask<object> ExecuteQueryAsync(string family, object request, CancellationToken ct) =>
            request is NativeKnowledgeQuery query ? await service.SearchAsync(query.Query, query.Limit, ct) : throw new NativeOperationException("invalid-request");
        public ValueTask<NativeActionPreview> PreviewAsync(string family, object command, string surface, CancellationToken ct) => throw new NotSupportedException();
        public ValueTask<NativeActionReceipt> CommitAsync(string family, object command, string confirmation, string key, string surface, CancellationToken ct) => throw new NotSupportedException();
    }
}
