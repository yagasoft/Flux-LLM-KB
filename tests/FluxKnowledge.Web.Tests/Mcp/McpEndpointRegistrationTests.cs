using System.Net;
using System.Text;
using System.Text.Json;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Sources;
using FluxKnowledge.Application.Visibility;
using FluxKnowledge.Domain.Sources;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace FluxKnowledge.Web.Tests.Mcp;

public sealed class McpEndpointRegistrationTests : IClassFixture<McpEndpointRegistrationTests.McpApplicationFactory>
{
    private readonly McpApplicationFactory _factory;

    public McpEndpointRegistrationTests(McpApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task Mcp_endpoint_advertises_only_the_four_approved_read_only_tools()
    {
        var initialise = await SendMcpRequestAsync(_factory, new
        {
            jsonrpc = "2.0",
            id = 1,
            method = "initialize",
            @params = new { protocolVersion = "2025-11-25", capabilities = new { }, clientInfo = new { name = "test", version = "1" } }
        }, IPAddress.Loopback);
        Assert.Equal(StatusCodes.Status200OK, initialise.Response.StatusCode);

        var discovery = await SendMcpRequestAsync(
            _factory,
            new { jsonrpc = "2.0", id = 2, method = "tools/list", @params = new { } },
            IPAddress.Loopback);
        Assert.Equal(StatusCodes.Status200OK, discovery.Response.StatusCode);
        using var document = JsonDocument.Parse(ReadJsonRpcPayload(await ReadResponseBodyAsync(discovery)));
        var names = document.RootElement.GetProperty("result").GetProperty("tools")
            .EnumerateArray()
            .Select(tool => tool.GetProperty("name").GetString()!)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(["kb.brief", "kb.retained_csharp_detail", "kb.retained_csharp_search", "kb.search"], names);
    }

    [Fact]
    public async Task Direct_loopback_stateless_MCP_dispatch_returns_the_named_retained_Csharp_detail_projection()
    {
        var branchId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var response = await SendMcpRequestAsync(_factory, new
        {
            jsonrpc = "2.0",
            id = 3,
            method = "tools/call",
            @params = new { name = "kb.retained_csharp_detail", arguments = new { branch_id = branchId, symbol_after_ordinal = 255, reference_after_ordinal = 127, diagnostic_after_ordinal = 7 } }
        }, IPAddress.Loopback);

        Assert.Equal(StatusCodes.Status200OK, response.Response.StatusCode);
        using var rpc = JsonDocument.Parse(ReadJsonRpcPayload(await ReadResponseBodyAsync(response)));
        var detailText = rpc.RootElement.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString();
        using var detail = JsonDocument.Parse(detailText!);
        Assert.Equal("C:\\retained-detail\\mcp.cs", detail.RootElement.GetProperty("localPath").GetString());
        Assert.Equal("Example.Type", detail.RootElement.GetProperty("symbols")[0].GetProperty("qualifiedName").GetString());
        Assert.DoesNotContain("secret-content-sentinel", detail.RootElement.GetRawText(), StringComparison.Ordinal);
        Assert.Equal(255, detail.RootElement.GetProperty("nextSymbolOrdinal").GetInt32());
        Assert.Equal(127, detail.RootElement.GetProperty("nextReferenceOrdinal").GetInt32());
        Assert.Equal(7, detail.RootElement.GetProperty("nextDiagnosticOrdinal").GetInt32());
    }

    [Fact]
    public async Task Direct_loopback_stateless_MCP_dispatch_passes_the_durable_Csharp_search_fact_cursor()
    {
        var response = await SendMcpRequestAsync(_factory, new
        {
            jsonrpc = "2.0",
            id = 4,
            method = "tools/call",
            @params = new
            {
                name = "kb.retained_csharp_search",
                arguments = new { query = "Example", limit = 10, cursor = "opaque-bound-cursor" }
            }
        }, IPAddress.Loopback);

        Assert.Equal(StatusCodes.Status200OK, response.Response.StatusCode);
        using var rpc = JsonDocument.Parse(ReadJsonRpcPayload(await ReadResponseBodyAsync(response)));
        var searchText = rpc.RootElement.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString();
        using var search = JsonDocument.Parse(searchText!);
        Assert.Equal("opaque-bound-cursor", search.RootElement.GetProperty("nextCursor").GetProperty("token").GetString());
    }

    [Fact]
    public async Task Direct_loopback_MCP_tampered_search_cursor_returns_only_the_fixed_safe_reason_code()
    {
        var response = await SendMcpRequestAsync(_factory, new
        {
            jsonrpc = "2.0",
            id = 5,
            method = "tools/call",
            @params = new
            {
                name = "kb.retained_csharp_search",
                arguments = new { query = "Example", limit = 10, cursor = "tampered" }
            }
        }, IPAddress.Loopback);

        Assert.Equal(StatusCodes.Status200OK, response.Response.StatusCode);
        using var rpc = JsonDocument.Parse(ReadJsonRpcPayload(await ReadResponseBodyAsync(response)));
        var text = rpc.RootElement.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString();
        using var result = JsonDocument.Parse(text!);
        Assert.Equal(
            LocalRetainedCsharpCodeSearchCursorException.ReasonCode,
            result.RootElement.GetProperty("reasonCode").GetString());
        Assert.DoesNotContain("tampered", result.RootElement.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Direct_loopback_stateless_MCP_posts_are_allowed_on_initial_connection_and_reconnect_but_remote_or_each_proxy_header_is_denied()
    {
        using var factory = new McpApplicationFactory();

        var allowed = await SendMcpInitialiseAsync(factory, IPAddress.Loopback);
        Assert.Equal(StatusCodes.Status200OK, allowed.Response.StatusCode);
        Assert.True(
            allowed.Response.ContentType?.StartsWith("application/json", StringComparison.OrdinalIgnoreCase) == true ||
            allowed.Response.ContentType?.StartsWith("text/event-stream", StringComparison.OrdinalIgnoreCase) == true,
            $"Expected the streamable HTTP MCP response, got '{allowed.Response.ContentType}'.");
        Assert.False(allowed.Response.Headers.ContainsKey("MCP-Session-Id"));

        // Program configures the MCP transport as stateless. Its real reconnect path is a new
        // direct POST, rather than a session-backed SSE/GET connection.
        var reconnect = await SendMcpInitialiseAsync(factory, IPAddress.Loopback);
        Assert.Equal(StatusCodes.Status200OK, reconnect.Response.StatusCode);
        Assert.False(reconnect.Response.Headers.ContainsKey("MCP-Session-Id"));

        var remote = await SendMcpInitialiseAsync(factory, IPAddress.Parse("192.0.2.20"));
        Assert.Equal(StatusCodes.Status403Forbidden, remote.Response.StatusCode);

        foreach (var header in ProxyAuthorityHeaders)
        {
            var proxied = await SendMcpInitialiseAsync(factory, IPAddress.Loopback, header);
            Assert.Equal(StatusCodes.Status403Forbidden, proxied.Response.StatusCode);
        }
    }

    private static string ReadJsonRpcPayload(string responseBody) =>
        responseBody.StartsWith("event:", StringComparison.Ordinal)
            ? responseBody.Split('\n').Single(line => line.StartsWith("data: ", StringComparison.Ordinal))[6..]
            : responseBody;

    private static Task<HttpContext> SendMcpInitialiseAsync(
        McpApplicationFactory factory,
        IPAddress remoteIpAddress,
        KeyValuePair<string, string>? proxyAuthorityHeader = null)
        => SendMcpRequestAsync(
            factory,
            new
            {
                jsonrpc = "2.0",
                id = 1,
                method = "initialize",
                @params = new { protocolVersion = "2025-11-25", capabilities = new { }, clientInfo = new { name = "test", version = "1" } }
            },
            remoteIpAddress,
            proxyAuthorityHeader);

    private static async Task<HttpContext> SendMcpRequestAsync(
        McpApplicationFactory factory,
        object payload,
        IPAddress remoteIpAddress,
        KeyValuePair<string, string>? proxyAuthorityHeader = null)
    {
        var payloadBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload));
        return await factory.Server.SendAsync(context =>
        {
            context.Request.Method = HttpMethods.Post;
            context.Request.Path = "/mcp";
            context.Request.ContentType = "application/json";
            context.Request.ContentLength = payloadBytes.Length;
            context.Request.Headers.Accept = "application/json, text/event-stream";
            context.Request.Body = new MemoryStream(payloadBytes);
            context.Connection.RemoteIpAddress = remoteIpAddress;
            if (proxyAuthorityHeader is { } header)
            {
                context.Request.Headers[header.Key] = header.Value;
            }
        });
    }

    private static async Task<string> ReadResponseBodyAsync(HttpContext context)
    {
        using var reader = new StreamReader(context.Response.Body, Encoding.UTF8, leaveOpen: true);
        return await reader.ReadToEndAsync();
    }

    private static IReadOnlyList<KeyValuePair<string, string>> ProxyAuthorityHeaders =>
    [
        new("Forwarded", "for=198.51.100.8"),
        new("Forwarded-Host", "public.example.test"),
        new("X-Forwarded-For", "198.51.100.8"),
        new("X-Original-Host", "public.example.test"),
        new("Proxy", "proxy.example.test"),
        new("X-Proxy-Id", "proxy.example.test"),
        new("X-Real-IP", "198.51.100.8"),
        new("Via", "1.1 proxy.example.test"),
        new("True-Client-IP", "198.51.100.8"),
        new("CF-Connecting-IP", "198.51.100.8")
    ];

    public sealed class McpApplicationFactory : WebApplicationFactory<Program>
    {
        public Reader Reader { get; } = new();

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
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<ILocalRetainedCsharpCodeReader>();
                services.AddSingleton<ILocalRetainedCsharpCodeReader>(Reader);
            });
        }
    }

    public sealed class Reader : ILocalRetainedCsharpCodeReader
    {
        private static readonly Guid BranchId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        public LocalRetainedCsharpCodePageRequest? PageRequest { get; private set; }
        public LocalRetainedCsharpCodeSearchPageRequest? SearchPageRequest { get; private set; }

        public ValueTask<LocalRetainedCsharpCodeDetailProjection?> ReadAsync(Guid branchId, CancellationToken cancellationToken) =>
            ValueTask.FromResult<LocalRetainedCsharpCodeDetailProjection?>(branchId == BranchId
                ? new LocalRetainedCsharpCodeDetailProjection(
                    BranchId,
                    new SourceRevisionId(Guid.Parse("22222222-2222-2222-2222-222222222222")),
                    "C:\\retained-detail\\mcp.cs",
                    new string('a', 64),
                    12,
                    "success",
                    new string('b', 64),
                    new string('c', 64),
                    1,
                    0,
                    0,
                    [new LocalRetainedCsharpSymbolProjection(0, 1, "Type", "Example.Type", "public void Run()", "public", -1, 0, 4)],
                    [],
                    [])
                : null);

        public ValueTask<IReadOnlyList<LocalRetainedCsharpCodeSearchProjection>> SearchAsync(
            string query,
            int limit,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlyList<LocalRetainedCsharpCodeSearchProjection>>(
            [
                new LocalRetainedCsharpCodeSearchProjection(
                    BranchId,
                    "C:\\retained-detail\\mcp.cs",
                    new string('a', 64),
                    [new LocalRetainedCsharpSymbolProjection(0, 1, "Type", "Example.Type", "public void Run()", "public", -1, 0, 4)])
            ]);

        public async ValueTask<LocalRetainedCsharpCodeDetailProjection?> ReadPageAsync(
            Guid branchId,
            LocalRetainedCsharpCodePageRequest pageRequest,
            CancellationToken cancellationToken)
        {
            PageRequest = pageRequest;
            var detail = await ReadAsync(branchId, cancellationToken);
            return detail is null
                ? null
                : detail with
                {
                    NextSymbolOrdinal = pageRequest.SymbolAfterOrdinal,
                    NextReferenceOrdinal = pageRequest.ReferenceAfterOrdinal,
                    NextDiagnosticOrdinal = pageRequest.DiagnosticAfterOrdinal
                };
        }

        public ValueTask<LocalRetainedCsharpCodeSearchPage> SearchPageAsync(
            LocalRetainedCsharpCodeSearchPageRequest pageRequest,
            CancellationToken cancellationToken)
        {
            SearchPageRequest = pageRequest;
            if (string.Equals(pageRequest.Cursor?.Token, "tampered", StringComparison.Ordinal))
            {
                throw new LocalRetainedCsharpCodeSearchCursorException();
            }

            return ValueTask.FromResult(new LocalRetainedCsharpCodeSearchPage(
            [
                new LocalRetainedCsharpCodeSearchProjection(
                    BranchId,
                    "C:\\retained-detail\\mcp.cs",
                    new string('a', 64),
                    [new LocalRetainedCsharpSymbolProjection(0, 1, "Type", "Example.Type", "public void Run()", "public", -1, 0, 4)])
            ],
            pageRequest.Cursor));
        }

        public ValueTask<LocalDisclosureResult> ReadExcerptAsync(Guid branchId, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new LocalDisclosureResult(null, true, "secret-content-withheld"));
    }
}
