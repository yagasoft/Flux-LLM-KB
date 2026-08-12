using System.Net;
using System.Text.Json;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Sources;
using FluxKnowledge.Application.Visibility;
using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Infrastructure.SqlServer.Visibility;
using FluxKnowledge.Web;
using FluxKnowledge.Web.Endpoints;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FluxKnowledge.Web.Tests.Endpoints;

public sealed class LocalRetainedDetailEndpointTests
{
    [Fact]
    public async Task Direct_loopback_gets_local_retained_detail_while_remote_and_proxy_requests_are_denied()
    {
        var branchId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        await using var host = await StartAsync(branchId);

        using var local = await host.Client.GetAsync($"/api/local/retained-branches/{branchId:D}");
        using var remoteRequest = new HttpRequestMessage(HttpMethod.Get, $"/api/local/retained-branches/{branchId:D}");
        remoteRequest.Headers.Add("X-Test-Remote", "192.0.2.20");
        using var remote = await host.Client.SendAsync(remoteRequest);
        using var forwardedRequest = new HttpRequestMessage(HttpMethod.Get, $"/api/local/retained-branches/{branchId:D}");
        forwardedRequest.Headers.TryAddWithoutValidation("Forwarded", "for=198.51.100.20");
        using var forwarded = await host.Client.SendAsync(forwardedRequest);

        Assert.Equal(HttpStatusCode.OK, local.StatusCode);
        var json = await local.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(json);
        Assert.Equal("C:\\retained-detail\\sample.txt", document.RootElement.GetProperty("localPath").GetString());
        Assert.Equal(new string('a', 64), document.RootElement.GetProperty("artifactHash").GetString());
        Assert.Equal(HttpStatusCode.Forbidden, remote.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, forwarded.StatusCode);
    }

    [Theory]
    [InlineData("-----BEGIN RSA PRIVATE KEY-----\nsynthetic-key-material\n-----END RSA PRIVATE KEY-----")]
    [InlineData("-----BEGIN OPENSSH PRIVATE KEY-----\nsynthetic-key-material\n-----END OPENSSH PRIVATE KEY-----")]
    [InlineData("postgresql://synthetic-user:synthetic-password@127.0.0.1/synthetic")]
    public async Task Secret_bearing_excerpt_is_withheld_locally_and_public_projection_does_not_gain_detail_fields(
        string syntheticValue)
    {
        var branchId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var excerpt = new LocalPrivateContentDisclosure().Evaluate(
            syntheticValue,
            LocalDisclosureKind.RetainedDetail);
        await using var host = await StartAsync(branchId, excerpt);

        using var response = await host.Client.GetAsync($"/api/local/retained-branches/{branchId:D}/excerpt");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadAsStringAsync();
        Assert.Contains("secret-content-withheld", json, StringComparison.Ordinal);
        Assert.DoesNotContain(syntheticValue, json, StringComparison.Ordinal);
        Assert.Null(typeof(FluxKnowledge.Application.Contracts.CorpusEntryDetail).GetProperty("LocalPath"));
        Assert.Null(typeof(FluxKnowledge.Application.Contracts.CorpusEntryDetail).GetProperty("ArtifactHash"));
    }

    private static async Task<TestHost> StartAsync(Guid branchId, LocalDisclosureResult? excerpt = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton<ILocalRetainedDetailReader>(new Reader(
            branchId,
            excerpt ?? new LocalDisclosureResult(null, true, "secret-content-withheld")));
        var app = builder.Build();
        app.Use(async (context, next) =>
        {
            context.Connection.RemoteIpAddress = context.Request.Headers.TryGetValue("X-Test-Remote", out var remote)
                ? System.Net.IPAddress.Parse(remote.ToString())
                : System.Net.IPAddress.Loopback;
            await next(context);
        });
        app.UseLocalOperatorLoopbackGate();
        app.MapFluxKnowledgeLocalRetainedDetails();
        await app.StartAsync();
        return new TestHost(app, app.GetTestClient());
    }

    private sealed class Reader(Guid branchId, LocalDisclosureResult excerpt) : ILocalRetainedDetailReader
    {
        public ValueTask<LocalRetainedDetailProjection?> ReadAsync(Guid requestedBranchId, CancellationToken cancellationToken) =>
            ValueTask.FromResult<LocalRetainedDetailProjection?>(requestedBranchId == branchId
                ? new LocalRetainedDetailProjection(branchId, Guid.NewGuid(), new SourceRevisionId(Guid.NewGuid()),
                    "C:\\retained-detail\\sample.txt", new string('a', 64), new string('a', 64), 12,
                    new LocalRetainedContentHandle(branchId, new SourceRevisionId(Guid.NewGuid())), [], [])
                : null);

        public ValueTask<LocalDisclosureResult> ReadExcerptAsync(Guid requestedBranchId, CancellationToken cancellationToken) =>
            ValueTask.FromResult(excerpt);
    }

    private sealed record TestHost(WebApplication Application, HttpClient Client) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync() { Client.Dispose(); await Application.DisposeAsync(); }
    }
}
