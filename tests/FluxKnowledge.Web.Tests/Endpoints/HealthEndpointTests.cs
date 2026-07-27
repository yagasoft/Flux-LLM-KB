using System.Net;
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
    [InlineData(true, HttpStatusCode.OK)]
    [InlineData(false, HttpStatusCode.ServiceUnavailable)]
    public async Task Ready_endpoint_delegates_to_the_canonical_non_mutating_validator(
        bool isReady,
        HttpStatusCode expectedStatus)
    {
        var readiness = new RecordingReadinessValidator(isReady);
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton<ISqlServerReadinessValidator>(readiness);
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
