using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Sources;
using FluxKnowledge.Application.Visibility;
using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Web;
using FluxKnowledge.Web.Endpoints;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FluxKnowledge.Web.Tests.Endpoints;

public sealed class LocalRetainedCsharpCodeEndpointTests
{
    [Fact]
    public async Task Direct_loopback_gets_retained_Csharp_facts_while_remote_and_proxy_requests_are_denied()
    {
        var branchId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        await using var host = await StartAsync(branchId);

        using var local = await host.Client.GetAsync($"/api/local/retained-csharp-code/{branchId:D}");
        using var search = await host.Client.GetAsync("/api/local/retained-csharp-code?query=Example&limit=3&cursor=opaque-bound-cursor");
        using var excerpt = await host.Client.GetAsync($"/api/local/retained-csharp-code/{branchId:D}/excerpt");
        using var paged = await host.Client.GetAsync($"/api/local/retained-csharp-code/{branchId:D}?symbolAfterOrdinal=255&referenceAfterOrdinal=255&diagnosticAfterOrdinal=12");
        using var remoteRequest = new HttpRequestMessage(HttpMethod.Get, $"/api/local/retained-csharp-code/{branchId:D}");
        remoteRequest.Headers.Add("X-Test-Remote", "192.0.2.20");
        using var remote = await host.Client.SendAsync(remoteRequest);
        using var forwardedRequest = new HttpRequestMessage(HttpMethod.Get, $"/api/local/retained-csharp-code/{branchId:D}");
        forwardedRequest.Headers.TryAddWithoutValidation("Forwarded", "for=198.51.100.20");
        using var forwarded = await host.Client.SendAsync(forwardedRequest);

        Assert.Equal(HttpStatusCode.OK, local.StatusCode);
        var json = await local.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(json);
        Assert.Equal("C:\\retained-detail\\sample.cs", document.RootElement.GetProperty("localPath").GetString());
        Assert.Equal(new string('a', 64), document.RootElement.GetProperty("artifactHash").GetString());
        Assert.Equal("Example.Type", document.RootElement.GetProperty("symbols")[0].GetProperty("qualifiedName").GetString());
        Assert.Equal("public void Run()", document.RootElement.GetProperty("symbols")[0].GetProperty("renderedSignature").GetString());
        Assert.Equal("System.String", document.RootElement.GetProperty("references")[0].GetProperty("targetDisplay").GetString());
        Assert.Equal("CS0001", document.RootElement.GetProperty("diagnostics")[0].GetProperty("diagnosticId").GetString());
        Assert.DoesNotContain("secret-content-sentinel", json, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.OK, search.StatusCode);
        var searchJson = await search.Content.ReadAsStringAsync();
        using var searchDocument = JsonDocument.Parse(searchJson);
        Assert.Equal("C:\\retained-detail\\sample.cs", searchDocument.RootElement.GetProperty("results")[0].GetProperty("localPath").GetString());
        Assert.Equal("Example.Type", searchDocument.RootElement.GetProperty("results")[0].GetProperty("symbols")[0].GetProperty("qualifiedName").GetString());
        Assert.DoesNotContain("secret-content-sentinel", searchJson, StringComparison.Ordinal);
        Assert.NotNull(host.Reader.SearchPageRequest);
        Assert.Equal("opaque-bound-cursor", host.Reader.SearchPageRequest!.Cursor!.Token);
        Assert.Equal(HttpStatusCode.OK, excerpt.StatusCode);
        Assert.Equal("verified retained excerpt", (await excerpt.Content.ReadFromJsonAsync<LocalDisclosureResult>())!.Value);
        Assert.Equal(HttpStatusCode.OK, paged.StatusCode);
        Assert.Equal(255, host.Reader.PageRequest!.SymbolAfterOrdinal);
        Assert.Equal(255, host.Reader.PageRequest.ReferenceAfterOrdinal);
        Assert.Equal(12, host.Reader.PageRequest.DiagnosticAfterOrdinal);
        Assert.Equal(HttpStatusCode.Forbidden, remote.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, forwarded.StatusCode);
    }

    [Fact]
    public void Public_search_and_corpus_contracts_do_not_gain_local_Csharp_fact_members()
    {
        Assert.Null(typeof(FluxKnowledge.Application.Contracts.SearchHit).GetProperty("LocalPath"));
        Assert.Null(typeof(FluxKnowledge.Application.Contracts.SearchHit).GetProperty("ArtifactHash"));
        Assert.Null(typeof(FluxKnowledge.Application.Contracts.SearchHit).GetProperty("Symbols"));
        Assert.Null(typeof(FluxKnowledge.Application.Contracts.CorpusEntryDetail).GetProperty("References"));
        Assert.Equal(
            ["Token"],
            typeof(LocalRetainedCsharpCodeSearchCursor).GetProperties()
                .Select(property => property.Name)
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToArray());
    }

    [Fact]
    public async Task Tampered_search_cursor_returns_only_the_fixed_safe_local_reason_code()
    {
        await using var host = await StartAsync(Guid.Parse("11111111-1111-1111-1111-111111111111"));

        using var response = await host.Client.GetAsync(
            "/api/local/retained-csharp-code?query=Example&cursor=tampered");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(
            LocalRetainedCsharpCodeSearchCursorException.ReasonCode,
            document.RootElement.GetProperty("reasonCode").GetString());
        Assert.DoesNotContain("tampered", document.RootElement.GetRawText(), StringComparison.Ordinal);
    }

    private static async Task<TestHost> StartAsync(Guid branchId)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        var reader = new Reader(branchId);
        builder.Services.AddSingleton<ILocalRetainedCsharpCodeReader>(reader);
        var app = builder.Build();
        app.Use(async (context, next) =>
        {
            context.Connection.RemoteIpAddress = context.Request.Headers.TryGetValue("X-Test-Remote", out var remote)
                ? IPAddress.Parse(remote.ToString())
                : IPAddress.Loopback;
            await next(context);
        });
        app.UseLocalOperatorLoopbackGate();
        app.MapFluxKnowledgeLocalRetainedCsharpCode();
        await app.StartAsync();
        return new TestHost(app, app.GetTestClient(), reader);
    }

    private sealed class Reader(Guid branchId) : ILocalRetainedCsharpCodeReader
    {
        public LocalRetainedCsharpCodePageRequest? PageRequest { get; private set; }
        public LocalRetainedCsharpCodeSearchPageRequest? SearchPageRequest { get; private set; }

        public ValueTask<LocalRetainedCsharpCodeDetailProjection?> ReadAsync(Guid requestedBranchId, CancellationToken cancellationToken) =>
            ValueTask.FromResult<LocalRetainedCsharpCodeDetailProjection?>(requestedBranchId == branchId
                ? new LocalRetainedCsharpCodeDetailProjection(
                    branchId,
                    new SourceRevisionId(Guid.Parse("22222222-2222-2222-2222-222222222222")),
                    "C:\\retained-detail\\sample.cs",
                    new string('a', 64),
                    12,
                    "success",
                    new string('b', 64),
                    new string('c', 64),
                    1,
                    1,
                    1,
                    [new LocalRetainedCsharpSymbolProjection(0, 1, "Type", "Example.Type", "public void Run()", "public", -1, 0, 4)],
                    [new LocalRetainedCsharpReferenceProjection(0, 1, 0, "System.String", 5, 6)],
                    [new LocalRetainedCsharpDiagnosticProjection(0, "CS0001", 2, 0, 1, "bounded parser diagnostic", false, null, false)])
                : null);

        public ValueTask<IReadOnlyList<LocalRetainedCsharpCodeSearchProjection>> SearchAsync(
            string query,
            int limit,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlyList<LocalRetainedCsharpCodeSearchProjection>>(
            [
                new LocalRetainedCsharpCodeSearchProjection(
                    branchId,
                    "C:\\retained-detail\\sample.cs",
                    new string('a', 64),
                    [new LocalRetainedCsharpSymbolProjection(0, 1, "Type", "Example.Type", "public void Run()", "public", -1, 0, 4)])
            ]);

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
                    branchId,
                    "C:\\retained-detail\\sample.cs",
                    new string('a', 64),
                    [new LocalRetainedCsharpSymbolProjection(0, 1, "Type", "Example.Type", "public void Run()", "public", -1, 0, 4)])
            ],
            null));
        }

        public ValueTask<LocalRetainedCsharpCodeDetailProjection?> ReadPageAsync(
            Guid requestedBranchId,
            LocalRetainedCsharpCodePageRequest pageRequest,
            CancellationToken cancellationToken)
        {
            PageRequest = pageRequest;
            return ReadAsync(requestedBranchId, cancellationToken);
        }

        public ValueTask<LocalDisclosureResult> ReadExcerptAsync(Guid requestedBranchId, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new LocalDisclosureResult("verified retained excerpt", false, null));
    }

    private sealed record TestHost(WebApplication Application, HttpClient Client, Reader Reader) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await Application.DisposeAsync();
        }
    }
}
