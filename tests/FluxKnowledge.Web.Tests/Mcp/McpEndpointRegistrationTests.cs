using System.Net;
using System.Text;
using System.Text.Json;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Sources;
using FluxKnowledge.Application.Visibility;
using FluxKnowledge.Application.IntegrationV1;
using FluxKnowledge.Application.IntegrationV1.Code;
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
    public async Task Mcp_endpoint_advertises_only_the_nine_native_v1_tools()
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

        Assert.Equal(
            ["code.query", "code.write", "corpus.query", "corpus.write", "knowledge.graph", "knowledge.search", "knowledge.write", "operations.audit", "operations.status"],
            names);
    }

    [Fact]
    public async Task Direct_loopback_stateless_MCP_dispatch_returns_the_native_v1_envelope()
    {
        var branchId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var response = await SendMcpRequestAsync(_factory, new
        {
            jsonrpc = "2.0",
            id = 3,
            method = "tools/call",
            @params = new { name = "knowledge.search", arguments = new { query = "needle", limit = 3 } }
        }, IPAddress.Loopback);

        Assert.Equal(StatusCodes.Status200OK, response.Response.StatusCode);
        using var rpc = JsonDocument.Parse(ReadJsonRpcPayload(await ReadResponseBodyAsync(response)));
        var payloadText = rpc.RootElement.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString();
        using var payload = JsonDocument.Parse(payloadText!);
        Assert.True(payload.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("knowledge", payload.RootElement.GetProperty("result").GetProperty("family").GetString());
    }

    [Fact]
    public async Task Direct_loopback_MCP_tampered_native_cursor_returns_only_the_fixed_safe_reason_code()
    {
        var response = await SendMcpRequestAsync(_factory, new
        {
            jsonrpc = "2.0",
            id = 4,
            method = "tools/call",
            @params = new
            {
                name = "code.query",
                arguments = new { view = "symbols", query = (string?)null, branch_id = (string?)null, limit = 10, cursor = "tampered" }
            }
        }, IPAddress.Loopback);

        Assert.Equal(StatusCodes.Status200OK, response.Response.StatusCode);
        using var rpc = JsonDocument.Parse(ReadJsonRpcPayload(await ReadResponseBodyAsync(response)));
        var text = rpc.RootElement.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString();
        using var result = JsonDocument.Parse(text!);
        Assert.Equal("cursor-invalid", result.RootElement.GetProperty("reasonCode").GetString());
        Assert.DoesNotContain("tampered", result.RootElement.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Live_MCP_claim_actions_preview_and_commit_through_the_graph_family()
    {
        _factory.Facade.Reset();
        var claimUpsert = new
        {
            action = "claim_upsert", item_id = (string?)null, title = (string?)null, body = (string?)null,
            subject = (string?)"subject", predicate = (string?)"supports", object_text = (string?)"object",
            transition = (string?)null, related_claim_id = (string?)null, reason = (string?)null, confidence = (decimal?)0.8m
        };
        var claimTransition = new
        {
            action = "claim_transition", item_id = (string?)Guid.NewGuid().ToString("D"), title = (string?)null, body = (string?)null,
            subject = (string?)null, predicate = (string?)null, object_text = (string?)null,
            transition = (string?)"confirmed", related_claim_id = (string?)null, reason = (string?)null, confidence = (decimal?)null
        };

        foreach (var claim in new[] { claimUpsert, claimTransition })
        {
            var preview = await SendMcpRequestAsync(_factory, new
            {
                jsonrpc = "2.0", id = 20, method = "tools/call",
                @params = new { name = "knowledge.write", arguments = new { mode = "preview", claim.action, claim.item_id, claim.title, claim.body, claim.subject, claim.predicate, claim.object_text, claim.transition, claim.related_claim_id, claim.reason, claim.confidence } }
            }, IPAddress.Loopback);
            var commit = await SendMcpRequestAsync(_factory, new
            {
                jsonrpc = "2.0", id = 21, method = "tools/call",
                @params = new { name = "knowledge.write", arguments = new { mode = "commit", claim.action, claim.item_id, claim.title, claim.body, claim.subject, claim.predicate, claim.object_text, claim.transition, claim.related_claim_id, claim.reason, claim.confidence, confirmation_id = "opaque-confirmation", idempotency_key = Guid.NewGuid().ToString("N") } }
            }, IPAddress.Loopback);

            Assert.Equal(StatusCodes.Status200OK, preview.Response.StatusCode);
            Assert.Equal(StatusCodes.Status200OK, commit.Response.StatusCode);
            Assert.True(ReadToolEnvelope(preview).GetProperty("ok").GetBoolean());
            Assert.True(ReadToolEnvelope(commit).GetProperty("ok").GetBoolean());
        }

        Assert.Equal(["graph", "graph"], _factory.Facade.PreviewFamilies);
        Assert.Equal(["graph", "graph"], _factory.Facade.CommitFamilies);
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

    private static JsonElement ReadToolEnvelope(HttpContext response)
    {
        using var rpc = JsonDocument.Parse(ReadJsonRpcPayload(ReadResponseBodyAsync(response).GetAwaiter().GetResult()));
        var text = rpc.RootElement.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString();
        using var envelope = JsonDocument.Parse(text!);
        return envelope.RootElement.Clone();
    }

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
        public NativeFacade Facade { get; } = new();

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
                services.RemoveAll<INativeV1Facade>();
                services.AddSingleton<INativeV1Facade>(Facade);
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

    public sealed class NativeFacade : INativeV1Facade
    {
        public List<string> PreviewFamilies { get; } = [];
        public List<string> CommitFamilies { get; } = [];

        public void Reset()
        {
            PreviewFamilies.Clear();
            CommitFamilies.Clear();
        }

        public ValueTask<object> ExecuteQueryAsync(string family, object request, CancellationToken cancellationToken)
        {
            if (request is NativeCodeQuery { Cursor: not null }) throw new NativeOperationException("cursor-invalid");
            return ValueTask.FromResult<object>(new { family });
        }

        public ValueTask<NativeActionPreview> PreviewAsync(string family, object command, string surface, CancellationToken cancellationToken)
        {
            PreviewFamilies.Add(family);
            return ValueTask.FromResult(new NativeActionPreview(Guid.Empty, "opaque-confirmation", "fingerprint", DateTimeOffset.UnixEpoch, [], "safe"));
        }

        public ValueTask<NativeActionReceipt> CommitAsync(string family, object command, string confirmationId, string idempotencyKey, string surface, CancellationToken cancellationToken)
        {
            CommitFamilies.Add(family);
            return ValueTask.FromResult(new NativeActionReceipt(Guid.Empty, false, "committed", null));
        }
    }
}
