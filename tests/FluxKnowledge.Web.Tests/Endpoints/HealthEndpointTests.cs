using System.Net;
using FluxKnowledge.Application.Indexing;
using FluxKnowledge.Infrastructure.SqlServer.Configuration;
using FluxKnowledge.Infrastructure.SqlServer.Provisioning;
using FluxKnowledge.Web.Endpoints;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FluxKnowledge.Web.Tests.Endpoints;

public sealed class HealthEndpointTests
{
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
