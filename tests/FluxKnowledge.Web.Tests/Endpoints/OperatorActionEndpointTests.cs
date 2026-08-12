using System.Net;
using System.Net.Http.Json;
using FluxKnowledge.Application.Contracts;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Web;
using FluxKnowledge.Web.Components.OperatorActions;
using FluxKnowledge.Web.Endpoints;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FluxKnowledge.Web.Tests.Endpoints;

public sealed class OperatorActionEndpointTests
{
    private static readonly OperatorActionProjection VisibleAction = new(
        new string('a', 64),
        new string('c', 64),
        "11111111111111111111111111111111",
        "AQIDBAUGBwg=",
        "document-ooxml-structural-extract",
        "blocked",
        "operator-action-retryable-test",
        null,
        DateTimeOffset.Parse("2026-08-14T18:00:00Z"),
        null,
        null,
        null,
        OverrideAvailable: true,
        RetryAvailable: true,
        Ignored: false);

    [Fact]
    public async Task List_is_sanitised_and_excludes_ignored_actions_by_default()
    {
        var store = new RecordingOperatorActionStore(
        [
            VisibleAction,
            VisibleAction with { ActionId = new string('b', 64), Ignored = true }
        ]);
        await using var host = await StartAsync(store);

        using var response = await host.Client.GetAsync("/api/operator-actions");
        var json = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(VisibleAction.ActionId, json, StringComparison.Ordinal);
        Assert.DoesNotContain(new string('b', 64), json, StringComparison.Ordinal);
        Assert.DoesNotContain("sourceRevision", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("branchId", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sha256", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("canonicalPath", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Include_ignored_and_single_GET_return_only_public_action_fields()
    {
        var ignored = VisibleAction with { ActionId = new string('b', 64), Ignored = true };
        var store = new RecordingOperatorActionStore([VisibleAction, ignored]);
        await using var host = await StartAsync(store);

        var actions = await host.Client.GetFromJsonAsync<List<OperatorActionProjection>>(
            "/api/operator-actions?includeIgnored=true");
        using var single = await host.Client.GetAsync($"/api/operator-actions/{ignored.ActionId}?includeIgnored=true");

        Assert.Equal(2, actions!.Count);
        Assert.Equal(HttpStatusCode.OK, single.StatusCode);
        Assert.Equal(ignored, await single.Content.ReadFromJsonAsync<OperatorActionProjection>());
    }

    [Theory]
    [InlineData("override", "policy-override")]
    [InlineData("retry", "retry")]
    [InlineData("ignore", "ignore")]
    [InlineData("unignore", "unignore")]
    public async Task New_mutation_returns_created_and_publishes_one_public_refresh(
        string routeAction,
        string actionKind)
    {
        var store = new RecordingOperatorActionStore([VisibleAction]);
        var publisher = new RecordingStatusPublisher();
        await using var host = await StartAsync(store, publisher);
        var operationId = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var request = new OperatorActionMutationRequest(
            operationId,
            new string('c', 64),
            VisibleAction.BlockedRowVersionToken);

        using var message = Post($"/api/operator-actions/{VisibleAction.ActionId}/{routeAction}", request);
        using var response = await host.Client.SendAsync(message);
        var receipt = await response.Content.ReadFromJsonAsync<OperatorActionMutationReceipt>();

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.NotNull(receipt);
        Assert.False(receipt.WasReplay);
        Assert.Equal(actionKind, store.LastCommand!.ActionKind);
        Assert.Equal(operationId, store.LastCommand.OperationId);
        var changed = Assert.Single(publisher.Published);
        Assert.Null(changed.PipelineRecordId);
        Assert.Equal("operator-actions", changed.Projection);
    }

    [Fact]
    public async Task Exact_replay_returns_ok_without_a_public_refresh()
    {
        var store = new RecordingOperatorActionStore([VisibleAction]) { Replay = true };
        var publisher = new RecordingStatusPublisher();
        await using var host = await StartAsync(store, publisher);
        var request = new OperatorActionMutationRequest(
            Guid.Parse("22222222-2222-2222-2222-222222222222"),
            new string('c', 64),
            VisibleAction.BlockedRowVersionToken);

        using var message = Post($"/api/operator-actions/{VisibleAction.ActionId}/retry", request);
        using var response = await host.Client.SendAsync(message);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(publisher.Published);
    }

    [Theory]
    [InlineData("operator-action-unlisted", HttpStatusCode.NotFound)]
    [InlineData("operator-operation-conflict", HttpStatusCode.Conflict)]
    [InlineData("operator-action-stale", HttpStatusCode.Conflict)]
    [InlineData("operator-action-not-eligible", HttpStatusCode.Conflict)]
    [InlineData("operator-descriptor-disabled", HttpStatusCode.ServiceUnavailable)]
    public async Task Rejections_use_fixed_public_status_mapping(string reasonCode, HttpStatusCode expected)
    {
        var store = new RecordingOperatorActionStore([VisibleAction]) { RejectionReason = reasonCode };
        var publisher = new RecordingStatusPublisher();
        await using var host = await StartAsync(store, publisher);
        var request = new OperatorActionMutationRequest(
            Guid.Parse("22222222-2222-2222-2222-222222222222"),
            new string('c', 64),
            VisibleAction.BlockedRowVersionToken);

        using var message = Post($"/api/operator-actions/{VisibleAction.ActionId}/retry", request);
        using var response = await host.Client.SendAsync(message);
        var json = await response.Content.ReadAsStringAsync();

        Assert.Equal(expected, response.StatusCode);
        Assert.Contains(reasonCode, json, StringComparison.Ordinal);
        Assert.Empty(publisher.Published);
    }

    [Fact]
    public async Task Mutation_requires_antiforgery_and_same_origin()
    {
        var store = new RecordingOperatorActionStore([VisibleAction]);
        await using var host = await StartAsync(store);
        var request = new OperatorActionMutationRequest(
            Guid.Parse("22222222-2222-2222-2222-222222222222"),
            new string('c', 64),
            VisibleAction.BlockedRowVersionToken);

        using var missingToken = new HttpRequestMessage(HttpMethod.Post,
            $"/api/operator-actions/{VisibleAction.ActionId}/retry")
        { Content = JsonContent.Create(request) };
        missingToken.Headers.TryAddWithoutValidation("Origin", "http://localhost");
        using var crossOrigin = Post($"/api/operator-actions/{VisibleAction.ActionId}/retry", request);
        crossOrigin.Headers.Remove("Origin");
        crossOrigin.Headers.TryAddWithoutValidation("Origin", "https://example.invalid");

        using var missingTokenResponse = await host.Client.SendAsync(missingToken);
        using var crossOriginResponse = await host.Client.SendAsync(crossOrigin);

        Assert.Equal(HttpStatusCode.Forbidden, missingTokenResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, crossOriginResponse.StatusCode);
        Assert.Null(store.LastCommand);
    }

    [Fact]
    public async Task Mutation_allows_an_absent_origin_but_rejects_a_cross_origin_referer()
    {
        var store = new RecordingOperatorActionStore([VisibleAction]);
        await using var host = await StartAsync(store);
        var request = new OperatorActionMutationRequest(
            Guid.Parse("22222222-2222-2222-2222-222222222222"),
            new string('c', 64), VisibleAction.BlockedRowVersionToken);
        using var absentOrigin = Post($"/api/operator-actions/{VisibleAction.ActionId}/retry", request);
        absentOrigin.Headers.Remove("Origin");
        using var crossReferer = Post($"/api/operator-actions/{VisibleAction.ActionId}/retry", request);
        crossReferer.Headers.Remove("Origin");
        crossReferer.Headers.Referrer = new Uri("https://example.invalid/operator-actions");

        using var allowed = await host.Client.SendAsync(absentOrigin);
        using var denied = await host.Client.SendAsync(crossReferer);

        Assert.Equal(HttpStatusCode.Created, allowed.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
    }

    [Fact]
    public async Task Mutation_does_not_trust_a_forged_host_as_the_same_origin_authority()
    {
        var store = new RecordingOperatorActionStore([VisibleAction]);
        await using var host = await StartAsync(store);
        var request = new OperatorActionMutationRequest(
            Guid.Parse("22222222-2222-2222-2222-222222222222"),
            new string('c', 64), VisibleAction.BlockedRowVersionToken);
        using var message = Post($"/api/operator-actions/{VisibleAction.ActionId}/retry", request);
        message.Headers.Host = "attacker.invalid";
        message.Headers.Remove("Origin");
        message.Headers.TryAddWithoutValidation("Origin", "http://attacker.invalid");

        using var response = await host.Client.SendAsync(message);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Null(store.LastCommand);
    }

    [Fact]
    public async Task Mutation_uses_the_configured_canonical_loopback_origin_independently_of_host()
    {
        const string canonicalOrigin = "http://127.0.0.1:4321";
        var store = new RecordingOperatorActionStore([VisibleAction]);
        await using var host = await StartAsync(store, canonicalOrigin: canonicalOrigin);
        var request = new OperatorActionMutationRequest(
            Guid.Parse("22222222-2222-2222-2222-222222222222"),
            new string('c', 64), VisibleAction.BlockedRowVersionToken);
        using var message = Post($"/api/operator-actions/{VisibleAction.ActionId}/retry", request);
        message.Headers.Host = "attacker.invalid";
        message.Headers.Remove("Origin");
        message.Headers.TryAddWithoutValidation("Origin", canonicalOrigin);

        using var response = await host.Client.SendAsync(message);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.NotNull(store.LastCommand);
    }

    [Theory]
    [InlineData("remote")]
    [InlineData("forwarded")]
    [InlineData("x-forwarded-for")]
    [InlineData("x-forwarded-host")]
    [InlineData("x-forwarded-proto")]
    [InlineData("x-forwarded-custom")]
    [InlineData("x-forwarded")]
    [InlineData("x-forwardedfor")]
    [InlineData("x-original")]
    [InlineData("x-original-for")]
    [InlineData("x-original-host")]
    [InlineData("proxy")]
    [InlineData("proxy-connection")]
    [InlineData("x-proxy")]
    [InlineData("x-proxy-user-ip")]
    [InlineData("x-real-ip")]
    [InlineData("via")]
    public async Task Entire_operator_surface_rejects_remote_or_proxied_requests(string authority)
    {
        await using var host = await StartAsync(new RecordingOperatorActionStore([VisibleAction]));
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/operator-actions");
        if (authority == "remote") request.Headers.Add("X-Test-Remote", "192.0.2.20");
        else request.Headers.TryAddWithoutValidation(authority, "198.51.100.20");

        using var response = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Malformed_mutation_returns_bad_request_without_refresh()
    {
        var publisher = new RecordingStatusPublisher();
        await using var host = await StartAsync(new RecordingOperatorActionStore([VisibleAction]), publisher);
        using var request = Post($"/api/operator-actions/{VisibleAction.ActionId}/retry",
            new OperatorActionMutationRequest(Guid.Empty, "bad", "bad"));

        using var response = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(publisher.Published);
    }

    [Fact]
    public async Task Existing_operation_conflict_is_resolved_before_changed_fields_are_format_validated()
    {
        var store = new RecordingOperatorActionStore([VisibleAction])
        {
            RejectionReason = "operator-operation-conflict"
        };
        await using var host = await StartAsync(store);
        using var request = Post("/api/operator-actions/not-a-sha256-action/retry",
            new OperatorActionMutationRequest(
                Guid.Parse("22222222-2222-2222-2222-222222222222"), "malformed", "malformed"));

        using var response = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.NotNull(store.LastCommand);
    }

    private static HttpRequestMessage Post(string path, OperatorActionMutationRequest request)
    {
        var message = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(request) };
        message.Headers.Add("X-CSRF-TOKEN", "valid");
        message.Headers.TryAddWithoutValidation("Origin", "http://localhost");
        return message;
    }

    private static async Task<TestHost> StartAsync(
        RecordingOperatorActionStore store,
        RecordingStatusPublisher? publisher = null,
        string canonicalOrigin = "http://localhost")
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton<IOperatorActionStore>(store);
        builder.Services.AddSingleton<IStatusEventPublisher>(publisher ?? new RecordingStatusPublisher());
        builder.Services.AddSingleton<TimeProvider>(new FixedTimeProvider(DateTimeOffset.Parse("2026-08-14T19:00:00Z")));
        builder.Services.AddSingleton<IAntiforgery, HeaderAntiforgery>();
        builder.Services.AddSingleton(new LocalOperatorOriginPolicy(canonicalOrigin));
        builder.Services.AddScoped<OperatorActionService>();
        var application = builder.Build();
        application.Use(async (context, next) =>
        {
            context.Connection.RemoteIpAddress = context.Request.Headers.TryGetValue("X-Test-Remote", out var remote)
                ? IPAddress.Parse(remote.ToString())
                : IPAddress.Loopback;
            await next(context);
        });
        application.UseLocalOperatorLoopbackGate();
        application.MapFluxKnowledgeOperatorActions();
        await application.StartAsync();
        return new TestHost(application, application.GetTestClient());
    }

    private sealed class RecordingOperatorActionStore(IReadOnlyList<OperatorActionProjection> actions) : IOperatorActionStore
    {
        public bool Replay { get; init; }
        public string? RejectionReason { get; init; }
        public OperatorActionMutationCommand? LastCommand { get; private set; }

        public ValueTask<IReadOnlyList<OperatorActionProjection>> ListAsync(
            bool includeIgnored,
            int maximumCount,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlyList<OperatorActionProjection>>(
                actions.Where(action => includeIgnored || !action.Ignored).Take(maximumCount).ToArray());

        public ValueTask<OperatorActionMutationReceipt> ExecuteAsync(
            OperatorActionMutationCommand command,
            CancellationToken cancellationToken)
        {
            LastCommand = command;
            if (RejectionReason is not null) throw new OperatorActionRequestRejectedException(RejectionReason);
            OoxmlForceRequestIdentity.RequireSha256(command.ActionId, nameof(command.ActionId));
            OoxmlForceRequestIdentity.RequireSha256(command.RequestFingerprint, nameof(command.RequestFingerprint));
            _ = OoxmlForceRequestIdentity.DecodeBlockedRowVersion(command.ExpectedBlockedRowVersion);
            return ValueTask.FromResult(new OperatorActionMutationReceipt(
                command.ActionId,
                command.OperationId,
                "11111111111111111111111111111111",
                command.ActionKind is "ignore" or "unignore" ? "ignored" : "requested",
                command.ActionKind is "ignore" or "unignore" ? 1 : null,
                command.ActionKind == "ignore" ? true : command.ActionKind == "unignore" ? false : null,
                Replay,
                DateTimeOffset.Parse("2026-08-14T19:00:00Z")));
        }
    }

    private sealed class RecordingStatusPublisher : IStatusEventPublisher
    {
        public List<StatusChanged> Published { get; } = [];

        public ValueTask PublishAsync(StatusChanged statusChanged, CancellationToken cancellationToken)
        {
            Published.Add(statusChanged);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class HeaderAntiforgery : IAntiforgery
    {
        public AntiforgeryTokenSet GetAndStoreTokens(HttpContext httpContext) => GetTokens(httpContext);
        public AntiforgeryTokenSet GetTokens(HttpContext httpContext) => new("valid", "cookie", "X-CSRF-TOKEN", "__RequestVerificationToken");
        public Task<bool> IsRequestValidAsync(HttpContext httpContext) =>
            Task.FromResult(httpContext.Request.Headers["X-CSRF-TOKEN"] == "valid");
        public Task ValidateRequestAsync(HttpContext httpContext) =>
            IsRequestValidAsync(httpContext).ContinueWith(task =>
            {
                if (!task.Result) throw new AntiforgeryValidationException("Missing token.");
            }, TaskScheduler.Default);
        public void SetCookieTokenAndHeader(HttpContext httpContext) { }
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
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
