using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluxKnowledge.Application.IntegrationV1;
using FluxKnowledge.Application.IntegrationV1.Code;
using FluxKnowledge.Application.IntegrationV1.Corpus;
using FluxKnowledge.Application.Visibility;
using FluxKnowledge.Infrastructure.SqlServer.Visibility;
using FluxKnowledge.Web;
using FluxKnowledge.Web.Endpoints;
using FluxKnowledge.Web.NativeV1;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FluxKnowledge.Web.Tests.Endpoints;

public sealed class NativeV1EndpointTests
{
    [Fact]
    public async Task Native_routes_map_queries_and_preview_to_the_same_envelope_and_facade_shapes()
    {
        await using var host = await StartAsync();

        using var search = await host.Client.PostAsJsonAsync("/api/v1/knowledge/search", new { query = "needle", limit = 3 });
        using var graph = await host.Client.PostAsJsonAsync("/api/v1/knowledge/graph/query", new { node = "node", max_depth = 2, max_results = 5 });
        using var code = await host.Client.PostAsJsonAsync("/api/v1/code/query", new { view = "symbols", limit = 3 });
        using var corpus = await host.Client.PostAsJsonAsync("/api/v1/corpus/query", new { view = "roots", limit = 3 });
        using var status = await host.Client.GetAsync("/api/v1/operations/status?view=overview&limit=3");
        using var audit = await host.Client.PostAsJsonAsync("/api/v1/operations/audit/query", new { view = "events", limit = 3 });
        using var preview = await host.Client.PostAsJsonAsync("/api/v1/knowledge/actions/preview", new { action = "note_create", title = "Title", body = "Body" });
        using var codePreview = await host.Client.PostAsJsonAsync("/api/v1/code/actions/preview", new { payload = new { rating = "useful" } });
        using var corpusPreview = await host.Client.PostAsJsonAsync("/api/v1/corpus/actions/preview", new { action = "root_create", payload = new { name = "Root" } });

        foreach (var response in new[] { search, graph, code, corpus, status, audit, preview, codePreview, corpusPreview })
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.True((await ReadAsync(response)).GetProperty("ok").GetBoolean());
        }

        Assert.Equal(["knowledge", "graph", "code", "corpus", "operations.status", "operations.audit"], host.Facade.Queries);
        Assert.Equal(3, host.Facade.PreviewCalls);
        Assert.Equal(0, host.Facade.CommitCalls);
    }

    [Fact]
    public async Task Mutations_require_direct_loopback_confirmation_and_idempotency_before_dispatch()
    {
        await using var host = await StartAsync();
        var command = new { payload = new { rating = "useful" } };

        using var remoteRequest = JsonContent.Create(command);
        remoteRequest.Headers.Add("X-Test-Remote", "192.0.2.20");
        using var remote = await host.Client.PostAsync("/api/v1/code/actions/preview", remoteRequest);
        using var missingConfirmation = await host.Client.PostAsJsonAsync("/api/v1/code/actions/commit", command);
        using var missingIdempotency = await host.Client.PostAsJsonAsync("/api/v1/code/actions/commit", new { payload = new { rating = "useful" }, confirmation_id = "opaque-confirmation" });
        using var committedRequest = new HttpRequestMessage(HttpMethod.Post, "/api/v1/code/actions/commit")
        {
            Content = JsonContent.Create(new { payload = new { rating = "useful" }, confirmation_id = "opaque-confirmation" })
        };
        committedRequest.Headers.Add("Idempotency-Key", "key-1");
        using var committed = await host.Client.SendAsync(committedRequest);

        Assert.Equal(HttpStatusCode.Forbidden, remote.StatusCode);
        Assert.Equal("confirmation-required", (await ReadAsync(missingConfirmation)).GetProperty("reasonCode").GetString());
        Assert.Equal("idempotency-key-required", (await ReadAsync(missingIdempotency)).GetProperty("reasonCode").GetString());
        Assert.Equal(HttpStatusCode.OK, committed.StatusCode);
        Assert.Equal(0, host.Facade.PreviewCalls);
        Assert.Equal(1, host.Facade.CommitCalls);
    }

    [Fact]
    public async Task Malformed_or_tampered_input_has_safe_error_envelopes_and_does_not_dispatch()
    {
        await using var host = await StartAsync();

        using var malformed = await host.Client.PostAsync("/api/v1/code/query", new StringContent("{", System.Text.Encoding.UTF8, "application/json"));
        using var cursor = await host.Client.PostAsJsonAsync("/api/v1/code/query", new { view = "symbols", limit = 3, cursor = "tampered-cursor" });
        var malformedPayload = await ReadAsync(malformed);
        var cursorPayload = await ReadAsync(cursor);

        Assert.Equal("invalid-json", malformedPayload.GetProperty("reasonCode").GetString());
        Assert.Equal("cursor-invalid", cursorPayload.GetProperty("reasonCode").GetString());
        Assert.DoesNotContain("tampered-cursor", cursorPayload.GetRawText(), StringComparison.Ordinal);
        Assert.Equal(0, host.Facade.QueryCalls);
    }

    [Fact]
    public async Task Oversize_bodies_and_invalid_limits_are_rejected_before_facade_dispatch()
    {
        await using var host = await StartAsync();

        using var oversized = await host.Client.PostAsync("/api/v1/knowledge/search", new StringContent("{\"query\":\"" + new string('x', NativeV1RequestMapper.MaximumBodyBytes) + "\",\"limit\":3}", System.Text.Encoding.UTF8, "application/json"));
        using var invalidLimit = await host.Client.PostAsJsonAsync("/api/v1/code/query", new { view = "symbols", limit = -1 });

        Assert.Equal("body-too-large", (await ReadAsync(oversized)).GetProperty("reasonCode").GetString());
        Assert.Equal("invalid-limit", (await ReadAsync(invalidLimit)).GetProperty("reasonCode").GetString());
        Assert.Equal(0, host.Facade.QueryCalls);
    }

    [Theory]
    [InlineData("knowledge", 2047, true)]
    [InlineData("knowledge", 2048, true)]
    [InlineData("knowledge", 2049, false)]
    [InlineData("graph", 2047, true)]
    [InlineData("graph", 2048, true)]
    [InlineData("graph", 2049, false)]
    [InlineData("graph", 4096, false)]
    [InlineData("code", 2047, true)]
    [InlineData("code", 2048, true)]
    [InlineData("code", 2049, false)]
    public async Task Query_character_boundaries_are_identical_at_the_REST_mapper(
        string family,
        int characterCount,
        bool accepted)
    {
        await using var host = await StartAsync();
        var value = new string('q', characterCount);
        var (path, payload) = family switch
        {
            "knowledge" => ("/api/v1/knowledge/search", JsonSerializer.SerializeToElement(new { query = value, limit = 3 })),
            "graph" => ("/api/v1/knowledge/graph/query", JsonSerializer.SerializeToElement(new { node = value, max_depth = 1, max_results = 3 })),
            "code" => ("/api/v1/code/query", JsonSerializer.SerializeToElement(new { view = "matches", query = value, limit = 3 })),
            _ => throw new InvalidOperationException()
        };

        using var response = await host.Client.PostAsJsonAsync(path, payload);
        var envelope = await ReadAsync(response);

        Assert.Equal(accepted, envelope.GetProperty("ok").GetBoolean());
        Assert.Equal(accepted ? null : "invalid-query", envelope.GetProperty("reasonCode").GetString());
        Assert.Equal(accepted ? 1 : 0, host.Facade.QueryCalls);
    }

    [Theory]
    [InlineData("knowledge")]
    [InlineData("audit")]
    [InlineData("code")]
    public async Task REST_accepts_every_schema_valid_maximum_native_page(string family)
    {
        var (path, request, result) = family switch
        {
            "knowledge" => ("/api/v1/knowledge/search", (object)new { query = "needle", limit = 100 }, MaximumKnowledgePage()),
            "audit" => ("/api/v1/operations/audit/query", new { view = "events", limit = 100 }, MaximumAuditPage()),
            "code" => ("/api/v1/code/query", new { view = "symbols", limit = 100 }, MaximumCodePage()),
            _ => throw new InvalidOperationException()
        };
        await using var host = await StartAsync(result);

        using var response = await host.Client.PostAsJsonAsync(path, request);
        var envelope = await ReadAsync(response);
        var responseBytes = await response.Content.ReadAsByteArrayAsync();
        var escapedValueBytes = family switch
        {
            "knowledge" => 100 * (256 + (16 * 1024)) * 6,
            "audit" => 100 * ((16 * 1024) + 256 + 128 + 64) * 6,
            "code" => 100 * (4096 + 4096) * 6,
            _ => throw new InvalidOperationException()
        };

        Assert.True(envelope.GetProperty("ok").GetBoolean());
        Assert.Equal(
            100,
            family == "knowledge"
                ? envelope.GetProperty("result").GetArrayLength()
                : envelope.GetProperty("result").GetProperty("items").GetArrayLength());
        Assert.InRange(responseBytes.Length, escapedValueBytes, NativeV1ContractLimits.MaximumResponseBytes);
    }

    [Fact]
    public async Task REST_rejects_a_facade_result_above_the_shared_native_response_budget()
    {
        var overBudget = new string('z', NativeV1ContractLimits.MaximumResponseBytes + 1);
        await using var host = await StartAsync(new { overBudget });

        using var response = await host.Client.PostAsJsonAsync(
            "/api/v1/code/query",
            new { view = "status", limit = 3 });
        var envelope = await ReadAsync(response);

        Assert.False(envelope.GetProperty("ok").GetBoolean());
        Assert.Equal("response-too-large", envelope.GetProperty("reasonCode").GetString());
        Assert.DoesNotContain(new string('z', 100), envelope.GetRawText(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("secret-content-sentinel")]
    [InlineData("password=synthetic-value")]
    [InlineData("postgresql://synthetic-user:synthetic-password@127.0.0.1/db")]
    [InlineData("-----BEGIN PRIVATE KEY----- synthetic -----END PRIVATE KEY-----")]
    [InlineData("eyJhY2Nlc3NUb2tlbiI6InZhbHVlIn0=")]
    public async Task Corpus_metadata_rejection_has_the_safe_shared_REST_envelope(string protectedDisplayName)
    {
        await using var host = await StartAsync();

        using var response = await host.Client.PostAsJsonAsync(
            "/api/v1/corpus/actions/preview",
            new
            {
                action = "root_create",
                payload = new { path = @"C:\native-v1-transport", displayName = protectedDisplayName }
            });
        var envelope = await ReadAsync(response);

        Assert.False(envelope.GetProperty("ok").GetBoolean());
        Assert.Equal("secret-content-withheld", envelope.GetProperty("reasonCode").GetString());
        Assert.DoesNotContain(protectedDisplayName, envelope.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Claim_actions_route_to_the_graph_family_for_preview_and_commit()
    {
        await using var host = await StartAsync();
        var claims = new[]
        {
            new { action = "claim_upsert", subject = (string?)"subject", predicate = (string?)"supports", object_text = (string?)"object", confidence = (decimal?)0.8m, item_id = (string?)null, transition = (string?)null },
            new { action = "claim_transition", subject = (string?)null, predicate = (string?)null, object_text = (string?)null, confidence = (decimal?)null, item_id = (string?)Guid.NewGuid().ToString("D"), transition = (string?)"confirmed" }
        };

        foreach (var claim in claims)
        {
            using var preview = await host.Client.PostAsJsonAsync("/api/v1/knowledge/actions/preview", claim);
            using var commit = new HttpRequestMessage(HttpMethod.Post, "/api/v1/knowledge/actions/commit")
            {
                Content = JsonContent.Create(new { claim.action, claim.subject, claim.predicate, claim.object_text, claim.confidence, claim.item_id, claim.transition, confirmation_id = "opaque-confirmation" })
            };
            commit.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
            using var committed = await host.Client.SendAsync(commit);

            Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
            Assert.Equal(HttpStatusCode.OK, committed.StatusCode);
        }

        Assert.Equal(["graph", "graph"], host.Facade.PreviewFamilies);
        Assert.Equal(["graph", "graph"], host.Facade.CommitFamilies);
    }

    [Fact]
    public async Task Content_length_null_oversize_body_stops_at_the_stream_bound_before_json_parsing_or_dispatch()
    {
        await using var host = await StartAsync();
        var source = new CountingNonSeekableStream(System.Text.Encoding.UTF8.GetBytes(
            "{\"query\":\"" + new string('x', NativeV1RequestMapper.MaximumBodyBytes) + "\",\"limit\":3}"));

        var response = await host.Server.SendAsync(context =>
        {
            context.Request.Method = HttpMethods.Post;
            context.Request.Path = "/api/v1/knowledge/search";
            context.Request.ContentType = "application/json";
            context.Request.ContentLength = null;
            context.Request.Body = source;
            context.Connection.RemoteIpAddress = System.Net.IPAddress.Loopback;
        });

        using var reader = new StreamReader(response.Response.Body, System.Text.Encoding.UTF8, leaveOpen: true);
        using var document = JsonDocument.Parse(await reader.ReadToEndAsync());
        Assert.Equal("body-too-large", document.RootElement.GetProperty("reasonCode").GetString());
        Assert.InRange(source.BytesRead, 1, NativeV1RequestMapper.MaximumBodyBytes + 1);
        Assert.Equal(0, host.Facade.QueryCalls);
    }

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.Clone();
    }

    private static object MaximumKnowledgePage()
    {
        var title = new string('\u0800', 256);
        var content = new string('\u0800', 16 * 1024);
        return Enumerable.Range(0, 100)
            .Select(_ => new
            {
                id = Guid.Empty,
                kind = "note",
                title,
                content,
                provenance = "knowledge",
                confidence = (decimal?)null,
                sourceIdentity = (string?)null,
                sourceRevision = (long?)null
            })
            .ToArray();
    }

    private static object MaximumAuditPage()
    {
        var eventType = new string('\u0800', 256);
        var eventFamily = new string('\u0800', 128);
        var severity = new string('\u0800', 64);
        var details = new string('\u0800', 16 * 1024);
        return new
        {
            items = Enumerable.Range(0, 100).Select(_ => new
            {
                id = long.MinValue,
                occurredAtUtc = DateTimeOffset.MinValue,
                eventType,
                eventFamily,
                severity,
                sourceRootId = (Guid?)Guid.Empty,
                sourceScanRequestId = (Guid?)Guid.Empty,
                details = new { withheld = false, value = details }
            }).ToArray(),
            nextCursor = (string?)null
        };
    }

    private static object MaximumCodePage()
    {
        var qualifiedName = new string('\u0800', 4096);
        var renderedSignature = new string('\u0800', 4096);
        return new
        {
            items = Enumerable.Range(0, 100).Select(ordinal => new
            {
                documentId = Guid.Empty,
                ordinal,
                declarationKindCode = 1,
                qualifiedName = new { withheld = false, value = qualifiedName },
                renderedSignature = new { withheld = false, value = renderedSignature }
            }).ToArray(),
            nextCursor = (string?)null
        };
    }

    private static async Task<TestHost> StartAsync(object? queryResult = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        var facade = new RecordingFacade(queryResult);
        builder.Services.AddSingleton<INativeV1Facade>(facade);
        builder.Services.AddSingleton<NativeV1RequestMapper>();
        var app = builder.Build();
        app.Use(async (context, next) =>
        {
            context.Connection.RemoteIpAddress = context.Request.Headers.TryGetValue("X-Test-Remote", out var remote)
                ? System.Net.IPAddress.Parse(remote.ToString())
                : System.Net.IPAddress.Loopback;
            await next(context);
        });
        app.UseLocalOperatorLoopbackGate();
        app.MapFluxKnowledgeNativeV1();
        await app.StartAsync();
        return new TestHost(app, app.GetTestClient(), app.GetTestServer(), facade);
    }

    private sealed class RecordingFacade(object? queryResult = null) : INativeV1Facade
    {
        public List<string> Queries { get; } = [];
        public int QueryCalls => Queries.Count;
        public int PreviewCalls { get; private set; }
        public int CommitCalls { get; private set; }
        public List<string> PreviewFamilies { get; } = [];
        public List<string> CommitFamilies { get; } = [];

        public ValueTask<object> ExecuteQueryAsync(string family, object request, CancellationToken cancellationToken)
        {
            if (request is NativeCodeQuery { Cursor: not null }) throw new NativeOperationException("cursor-invalid");
            Queries.Add(family);
            return ValueTask.FromResult(queryResult ?? (object)new { family, safe = true });
        }

        public ValueTask<NativeActionPreview> PreviewAsync(string family, object command, string surface, CancellationToken cancellationToken)
        {
            if (command is NativeCorpusMutation corpus &&
                corpus.Payload.TryGetProperty("displayName", out var displayName) &&
                displayName.ValueKind == JsonValueKind.String &&
                new LocalPrivateContentDisclosure().Evaluate(
                    displayName.GetString()!,
                    LocalDisclosureKind.CorpusMetadata) is { Withheld: true } withheld)
            {
                throw new NativeOperationException(withheld.ReasonCode!);
            }
            PreviewCalls++;
            PreviewFamilies.Add(family);
            return ValueTask.FromResult(new NativeActionPreview(Guid.Empty, "opaque-confirmation", "fingerprint", DateTimeOffset.UnixEpoch, [], "safe"));
        }

        public ValueTask<NativeActionReceipt> CommitAsync(string family, object command, string confirmationId, string idempotencyKey, string surface, CancellationToken cancellationToken)
        {
            CommitCalls++;
            CommitFamilies.Add(family);
            return ValueTask.FromResult(new NativeActionReceipt(Guid.Empty, false, "committed", null));
        }
    }

    private sealed record TestHost(WebApplication Application, HttpClient Client, TestServer Server, RecordingFacade Facade) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await Application.DisposeAsync();
        }
    }

    private sealed class CountingNonSeekableStream(byte[] bytes) : Stream
    {
        private readonly MemoryStream _inner = new(bytes, writable: false);
        public int BytesRead { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = _inner.Read(buffer, offset, count);
            BytesRead += read;
            return read;
        }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var pending = _inner.ReadAsync(buffer, cancellationToken);
            return CountAsync(pending);
        }
        private async ValueTask<int> CountAsync(ValueTask<int> pending)
        {
            var read = await pending;
            BytesRead += read;
            return read;
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
