using System.Net;
using FluxKnowledge.Application.Indexing;
using FluxKnowledge.Infrastructure.SqlServer.Configuration;
using FluxKnowledge.Infrastructure.SqlServer.Provisioning;
using FluxKnowledge.Web.Endpoints;
using FluxKnowledge.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FluxKnowledge.Web.Tests.Endpoints;

public sealed class HealthEndpointTests
{
    [Fact]
    public async Task Live_health_refuses_forwarded_requests_even_from_a_loopback_peer()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton<ISqlServerReadinessValidator>(new RecordingReadinessValidator(true));
        builder.Services.AddSingleton<IDerivedIndexRecoveryStatus>(new FixedRecoveryStatus(
            new DerivedIndexRecoverySnapshot(DerivedIndexRecoveryState.Healthy, null, null, null, null, 0)));
        builder.Services.AddSingleton(SqlServerOptions.ForProduction(
            "Server=unreachable.invalid;Initial Catalog=FluxKnowledge;Integrated Security=true;" +
            "Encrypt=true;TrustServerCertificate=true",
            SqlServerOptions.ProductionDataFilePath,
            SqlServerOptions.ProductionLogFilePath));
        await using var application = builder.Build();
        application.Use(async (context, next) =>
        {
            context.Connection.RemoteIpAddress = IPAddress.Loopback;
            await next(context);
        });
        application.UseLocalOperatorLoopbackGate();
        application.MapFluxKnowledgeHealth();
        await application.StartAsync();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/health/live");
        request.Headers.TryAddWithoutValidation("Forwarded", "for=192.0.2.20");
        request.Headers.TryAddWithoutValidation("X-Forwarded-For", "192.0.2.20");

        var response = await application.GetTestClient().SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Native_liveness_and_readiness_emit_the_fixed_application_proof_marker()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton<ISqlServerReadinessValidator>(new RecordingReadinessValidator(true));
        builder.Services.AddSingleton<IDerivedIndexRecoveryStatus>(new FixedRecoveryStatus(
            new DerivedIndexRecoverySnapshot(DerivedIndexRecoveryState.Healthy, null, null, null, null, 0)));
        builder.Services.AddSingleton(SqlServerOptions.ForProduction(
            "Server=unreachable.invalid;Initial Catalog=FluxKnowledge;Integrated Security=true;" +
            "Encrypt=true;TrustServerCertificate=true",
            SqlServerOptions.ProductionDataFilePath,
            SqlServerOptions.ProductionLogFilePath));
        await using var application = builder.Build();
        application.MapFluxKnowledgeHealth();
        await application.StartAsync();

        using var live = await application.GetTestClient().GetAsync("/health/live");
        using var ready = await application.GetTestClient().GetAsync("/health/ready");

        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
        Assert.True(live.Headers.TryGetValues("X-FluxKnowledge-Native-Proof", out var liveValues));
        Assert.Equal(["native-go-live-v1"], liveValues);
        Assert.True(ready.Headers.TryGetValues("X-FluxKnowledge-Native-Proof", out var readyValues));
        Assert.Equal(["native-go-live-v1"], readyValues);
    }

    [Theory]
    [InlineData(true, DerivedIndexRecoveryState.Healthy, HttpStatusCode.OK)]
    [InlineData(true, DerivedIndexRecoveryState.Starting, HttpStatusCode.ServiceUnavailable)]
    [InlineData(true, DerivedIndexRecoveryState.Recovering, HttpStatusCode.ServiceUnavailable)]
    [InlineData(true, DerivedIndexRecoveryState.RetryScheduled, HttpStatusCode.ServiceUnavailable)]
    [InlineData(true, DerivedIndexRecoveryState.OperatorActionRequired, HttpStatusCode.ServiceUnavailable)]
    [InlineData(false, DerivedIndexRecoveryState.Healthy, HttpStatusCode.ServiceUnavailable)]
    public async Task Ready_requires_both_canonical_SQL_validation_and_a_healthy_derived_index(
        bool isReady,
        DerivedIndexRecoveryState recoveryState,
        HttpStatusCode expectedStatus)
    {
        var readiness = new RecordingReadinessValidator(isReady);
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton<ISqlServerReadinessValidator>(readiness);
        builder.Services.AddSingleton<IDerivedIndexRecoveryStatus>(
            new FixedRecoveryStatus(new DerivedIndexRecoverySnapshot(
                recoveryState,
                Guid.NewGuid(),
                null,
                null,
                null,
                0)));
        builder.Services.AddSingleton(
            SqlServerOptions.ForProduction(
                "Server=unreachable.invalid;Initial Catalog=FluxKnowledge;" +
                "Integrated Security=true;Encrypt=true;TrustServerCertificate=true",
                SqlServerOptions.ProductionDataFilePath,
                SqlServerOptions.ProductionLogFilePath));
        await using var application = builder.Build();
        application.MapFluxKnowledgeHealth();
        await application.StartAsync();

        var response = await application.GetTestClient().GetAsync("/health/ready");

        Assert.Equal(expectedStatus, response.StatusCode);
        Assert.Equal(1, readiness.CallCount);
    }

    [Fact]
    public async Task Ready_accepts_a_healthy_explicitly_validated_empty_catalogue()
    {
        var readiness = new RecordingReadinessValidator(true);
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton<ISqlServerReadinessValidator>(readiness);
        builder.Services.AddSingleton<IDerivedIndexRecoveryStatus>(
            new FixedRecoveryStatus(new DerivedIndexRecoverySnapshot(
                DerivedIndexRecoveryState.Healthy,
                ActiveGenerationId: null,
                LastCompletedAtUtc: DateTimeOffset.UtcNow,
                NextRetryAtUtc: null,
                FailureCategory: null,
                CleanedCandidateCount: 0,
                IsValidatedEmptyCatalogue: true)));
        builder.Services.AddSingleton(SqlServerOptions.ForProduction(
            "Server=unreachable.invalid;Initial Catalog=FluxKnowledge;" +
            "Integrated Security=true;Encrypt=true;TrustServerCertificate=true",
            SqlServerOptions.ProductionDataFilePath,
            SqlServerOptions.ProductionLogFilePath));
        await using var application = builder.Build();
        application.MapFluxKnowledgeHealth();
        await application.StartAsync();

        var response = await application.GetTestClient().GetAsync("/health/ready");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, readiness.CallCount);
    }

    private sealed class FixedRecoveryStatus(DerivedIndexRecoverySnapshot snapshot)
        : IDerivedIndexRecoveryStatus
    {
        public DerivedIndexRecoverySnapshot Snapshot { get; } = snapshot;
    }

    private sealed class RecordingReadinessValidator(bool isReady)
        : ISqlServerReadinessValidator
    {
        public int CallCount { get; private set; }

        public Task<SqlServerReadinessResult> ValidateAsync(
            SqlServerOptions options,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(
                new SqlServerReadinessResult(
                    isReady,
                    isReady ? [] : ["canonical validation failed"]));
        }
    }
}
