using System.Net;
using System.Text.Json;
using FluxKnowledge.Application.Indexing;
using FluxKnowledge.Web.Endpoints;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FluxKnowledge.Web.Tests.Endpoints;

public sealed class IndexHealthEndpointTests
{
    [Fact]
    public async Task Index_health_exposes_only_the_safe_recovery_summary_over_GET()
    {
        var snapshot = new DerivedIndexRecoverySnapshot(
            DerivedIndexRecoveryState.RetryScheduled,
            Guid.Parse("11111111-2222-3333-4444-555555555555"),
            DateTimeOffset.Parse("2026-07-27T08:00:00Z"),
            DateTimeOffset.Parse("2026-07-27T08:00:05Z"),
            DerivedIndexRecoveryFailureCategory.TransientIo,
            3);
        await using var application = await CreateApplicationAsync(snapshot);

        using var response = await application.GetTestClient().GetAsync("/api/index-health");
        var payload = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(payload);
        var result = document.RootElement;

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(JsonValueKind.Object, result.ValueKind);
        Assert.Equal(
            new[]
            {
                "activeGeneration",
                "cleanedCandidateCount",
                "failureCategory",
                "lastCompletedAtUtc",
                "nextRetryAtUtc",
                "state"
            },
            result.EnumerateObject().Select(property => property.Name).OrderBy(name => name, StringComparer.Ordinal));
        Assert.Equal("RetryScheduled", result.GetProperty("state").GetString());
        Assert.Equal("11111111222233334444555555555555", result.GetProperty("activeGeneration").GetString());
        Assert.Equal("2026-07-27T08:00:00+00:00", result.GetProperty("lastCompletedAtUtc").GetString());
        Assert.Equal("2026-07-27T08:00:05+00:00", result.GetProperty("nextRetryAtUtc").GetString());
        Assert.Equal("TransientIo", result.GetProperty("failureCategory").GetString());
        Assert.Equal(3, result.GetProperty("cleanedCandidateCount").GetInt32());

        using var post = await application.GetTestClient().PostAsync("/api/index-health", null);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, post.StatusCode);
    }

    private static async Task<WebApplication> CreateApplicationAsync(DerivedIndexRecoverySnapshot snapshot)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton<IDerivedIndexRecoveryStatus>(new FixedRecoveryStatus(snapshot));
        var application = builder.Build();
        application.MapFluxKnowledgeIndexHealth();
        await application.StartAsync();
        return application;
    }

    private sealed class FixedRecoveryStatus(DerivedIndexRecoverySnapshot snapshot)
        : IDerivedIndexRecoveryStatus
    {
        public DerivedIndexRecoverySnapshot Snapshot { get; } = snapshot;
    }
}
