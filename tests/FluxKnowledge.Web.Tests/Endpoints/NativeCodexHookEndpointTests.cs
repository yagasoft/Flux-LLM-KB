using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FluxKnowledge.Application.IntegrationV1;
using FluxKnowledge.Application.Knowledge;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Operations;
using FluxKnowledge.Integrations.Codex;
using FluxKnowledge.Web;
using FluxKnowledge.Web.Endpoints;
using FluxKnowledge.Web.Mcp;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FluxKnowledge.Web.Tests.Endpoints;

public sealed class NativeCodexHookEndpointTests
{
    [Fact]
    public async Task Loopback_hook_forwards_ordinary_input_without_attestation_headers()
    {
        await using var host = await StartAsync();

        using var response = await host.Client.PostAsJsonAsync(
            "/native/v1/codex/hooks/UserPromptSubmit",
            new { prompt = "Continue the native activation work using prior decisions." });
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("{\"continue\":true,\"hookSpecificOutput\":{\"hookEventName\":\"UserPromptSubmit\",\"additionalContext\":\"Relevant local knowledge:\\n- Prior decision: Use the native loopback boundary.\"}}", body);
    }

    [Theory]
    [InlineData("PreCompact", "{\"trigger\":\"manual\"}")]
    [InlineData("Stop", "{\"session_id\":\"session-1\",\"turn_id\":\"turn-1\",\"last_assistant_message\":\"Captured local note.\"}")]
    public async Task Loopback_hook_emits_a_Codex_compatible_success_envelope_without_unsupported_fields(string eventName, string payload)
    {
        await using var host = await StartAsync();
        using var response = await host.Client.PostAsync(
            $"/native/v1/codex/hooks/{eventName}",
            new StringContent(payload, Encoding.UTF8, "application/json"));
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("{\"continue\":true}", body);
    }

    [Fact]
    public async Task Loopback_empty_UserPromptSubmit_context_emits_only_common_fields()
    {
        await using var host = await StartAsync([]);
        using var response = await host.Client.PostAsJsonAsync(
            "/native/v1/codex/hooks/UserPromptSubmit",
            new { prompt = "Find no matching local knowledge." });
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("{\"continue\":true}", body);
    }

    [Fact]
    public async Task Non_loopback_hook_route_is_rejected()
    {
        await using var host = await StartAsync();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/native/v1/codex/hooks/PreCompact")
        {
            Content = JsonContent.Create(new { trigger = "manual" })
        };
        request.Headers.Add("X-Test-Remote", "192.0.2.10");

        using var response = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Theory]
    [InlineData("{")]
    [InlineData("[]")]
    public async Task Malformed_hook_input_returns_a_fail_open_Codex_envelope(string body)
    {
        await using var host = await StartAsync();
        using var response = await host.Client.PostAsync(
            "/native/v1/codex/hooks/UserPromptSubmit",
            new StringContent(body, System.Text.Encoding.UTF8, "application/json"));
        var envelope = await ReadAsync(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(envelope.GetProperty("continue").GetBoolean());
        Assert.Equal("Native Codex hook ignored invalid input.", envelope.GetProperty("systemMessage").GetString());
    }

    [Fact]
    public async Task Invalid_Stop_preserves_its_non_empty_fail_open_system_message()
    {
        await using var host = await StartAsync();
        using var response = await host.Client.PostAsync(
            "/native/v1/codex/hooks/Stop",
            new StringContent("{\"session_id\":\"session-1\"}", Encoding.UTF8, "application/json"));
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("{\"continue\":true,\"systemMessage\":\"Native Codex hook ignored invalid input.\"}", body);
    }

    [Fact]
    public async Task Oversized_hook_body_returns_a_fail_open_Codex_envelope()
    {
        await using var host = await StartAsync();
        var body = "{\"prompt\":\"" + new string('x', NativeV1ContractLimits.MaximumRequestBytes) + "\"}";
        using var response = await host.Client.PostAsync(
            "/native/v1/codex/hooks/UserPromptSubmit",
            new StringContent(body, System.Text.Encoding.UTF8, "application/json"));
        var envelope = await ReadAsync(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(envelope.GetProperty("continue").GetBoolean());
        Assert.Equal("Native Codex hook ignored invalid input.", envelope.GetProperty("systemMessage").GetString());
    }

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.Clone();
    }

    private static async Task<TestHost> StartAsync(IReadOnlyList<KnowledgeSearchResult>? knowledgeResults = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton<INativeV1Facade>(new Facade(knowledgeResults ??
        [
            new KnowledgeSearchResult(Guid.Empty, "note", "Prior decision", "Use the native loopback boundary.", "knowledge")
        ]));
        builder.Services.AddSingleton<INativeOperationStore>(new OperationStore());
        builder.Services.AddSingleton<NativeCodexHookService>();
        var app = builder.Build();
        app.Use(async (context, next) =>
        {
            context.Connection.RemoteIpAddress = context.Request.Headers.TryGetValue("X-Test-Remote", out var remote)
                ? System.Net.IPAddress.Parse(remote.ToString())
                : System.Net.IPAddress.Loopback;
            await next(context);
        });
        app.UseLocalOperatorLoopbackGate();
        app.MapFluxKnowledgeNativeCodexHooks();
        await app.StartAsync();
        return new TestHost(app, app.GetTestClient());
    }

    private sealed class Facade(IReadOnlyList<KnowledgeSearchResult> knowledgeResults) : INativeV1Facade
    {
        public ValueTask<object> ExecuteQueryAsync(string family, object request, CancellationToken cancellationToken) =>
            ValueTask.FromResult<object>(knowledgeResults);

        public ValueTask<NativeActionPreview> PreviewAsync(string family, object command, string surface, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new NativeActionPreview(Guid.Empty, "confirmation", "fingerprint", DateTimeOffset.MaxValue, [], "saved"));

        public ValueTask<NativeActionReceipt> CommitAsync(string family, object command, string confirmationId, string idempotencyKey, string surface, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new NativeActionReceipt(Guid.Empty, false, "completed", null));
    }

    private sealed class OperationStore : INativeOperationStore
    {
        public ValueTask<NativeActionReceipt?> FindReceiptAsync(string idempotencyKey, string actorSurface, CancellationToken cancellationToken) => ValueTask.FromResult<NativeActionReceipt?>(null);
        public ValueTask<NativeActionReceipt?> TryReplayAsync(string action, string canonicalPayload, string confirmationId, string idempotencyKey, string actorSurface, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<NativeActionPreview> CreatePreviewAsync(NativeActionPreviewRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<NativeActionReceipt> CommitAsync(NativeActionCommitRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed record TestHost(WebApplication Application, HttpClient Client) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await Application.DisposeAsync();
        }
    }
}
