using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace FluxKnowledge.Web.Tests.Mcp;

public sealed class McpEndpointRegistrationTests : IClassFixture<McpEndpointRegistrationTests.McpApplicationFactory>
{
    private readonly HttpClient _client;

    public McpEndpointRegistrationTests(McpApplicationFactory factory) => _client = factory.CreateClient();

    [Fact]
    public async Task Mcp_endpoint_advertises_only_the_two_approved_read_only_tools()
    {
        using var initialise = await PostMcpAsync(new
        {
            jsonrpc = "2.0",
            id = 1,
            method = "initialize",
            @params = new { protocolVersion = "2025-11-25", capabilities = new { }, clientInfo = new { name = "test", version = "1" } }
        });
        initialise.EnsureSuccessStatusCode();

        using var discovery = await PostMcpAsync(
            new { jsonrpc = "2.0", id = 2, method = "tools/list", @params = new { } });
        discovery.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(ReadJsonRpcPayload(await discovery.Content.ReadAsStringAsync()));
        var names = document.RootElement.GetProperty("result").GetProperty("tools")
            .EnumerateArray()
            .Select(tool => tool.GetProperty("name").GetString()!)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(["kb.brief", "kb.search"], names);
    }

    private Task<HttpResponseMessage> PostMcpAsync(object payload)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = JsonContent.Create(payload)
        };
        request.Headers.Accept.ParseAdd("application/json, text/event-stream");
        return _client.SendAsync(request);
    }

    private static string ReadJsonRpcPayload(string responseBody) =>
        responseBody.StartsWith("event:", StringComparison.Ordinal)
            ? responseBody.Split('\n').Single(line => line.StartsWith("data: ", StringComparison.Ordinal))[6..]
            : responseBody;

    public sealed class McpApplicationFactory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("ConnectionStrings:FluxKnowledge",
                "Server=unreachable.invalid;Initial Catalog=FluxKnowledge;Integrated Security=true;Encrypt=true;TrustServerCertificate=true");
            builder.UseSetting("LocalIngress:AllowedRoots:0", Path.GetTempPath());
            builder.UseSetting("Usearch:RootPath", Path.Combine(Path.GetTempPath(), $"FluxKnowledgeMcpTests_{Guid.NewGuid():N}"));
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["ConnectionStrings:FluxKnowledge"] = "Server=unreachable.invalid;Initial Catalog=FluxKnowledge;Integrated Security=true;Encrypt=true;TrustServerCertificate=true",
                    ["LocalIngress:AllowedRoots:0"] = Path.GetTempPath(),
                    ["Usearch:RootPath"] = Path.Combine(Path.GetTempPath(), $"FluxKnowledgeMcpTests_{Guid.NewGuid():N}")
                }));
        }
    }
}
