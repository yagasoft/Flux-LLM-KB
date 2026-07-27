using System.Net.Http.Json;
using FluxKnowledge.Application.Contracts;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Domain.Common;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FluxKnowledge.Web.Tests.Endpoints;

public sealed class SearchEndpointTests : IClassFixture<SearchEndpointTests.SearchApplicationFactory>
{
    private readonly HttpClient _client;

    public SearchEndpointTests(SearchApplicationFactory factory) => _client = factory.CreateClient();

    [Fact]
    public async Task Search_endpoint_returns_hydrated_results_not_usarch_only_rows()
    {
        var response = await _client.GetFromJsonAsync<SearchResponse>("/api/search?query=restart&limit=5");

        var hit = Assert.Single(response!.Results);
        Assert.Equal("C:/ingress/guide.txt", hit.SourceIdentity);
        Assert.Contains(hit.Explanation, static item => item.StartsWith("lexical:", StringComparison.Ordinal));
        Assert.Contains(hit.Explanation, static item => item.StartsWith("semantic:", StringComparison.Ordinal));
    }

    public sealed class SearchApplicationFactory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("ConnectionStrings:FluxKnowledge",
                "Server=unreachable.invalid;Initial Catalog=FluxKnowledge;Integrated Security=true;Encrypt=true;TrustServerCertificate=true");
            builder.UseSetting("LocalIngress:AllowedRoots:0", Path.GetTempPath());
            builder.UseSetting("Usearch:RootPath", Path.Combine(Path.GetTempPath(), "FluxKnowledgeSearchTests"));
            builder.ConfigureTestServices(
                services => services.AddSingleton<ISearchService, HydratedSearchService>());
        }
    }

    private sealed class HydratedSearchService : ISearchService
    {
        public ValueTask<SearchResponse> SearchAsync(SearchRequest request, CancellationToken cancellationToken) =>
            ValueTask.FromResult(
                new SearchResponse(
                    [new SearchHit(
                        new PipelineRecordId(Guid.Parse("00000000-0000-0000-0000-000000000001")),
                        "C:/ingress/guide.txt",
                        2,
                        "guide.txt",
                        "Restart the service safely.",
                        0.032,
                        ["lexical:rank=1", "semantic:rank=1"])],
                    2,
                    "active-generation",
                    "local_first"));
    }
}
