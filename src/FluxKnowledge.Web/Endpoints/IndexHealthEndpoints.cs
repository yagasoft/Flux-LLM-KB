using FluxKnowledge.Application.Indexing;

namespace FluxKnowledge.Web.Endpoints;

public sealed record IndexRecoveryProjection(
    string State,
    string? ActiveGeneration,
    DateTimeOffset? LastCompletedAtUtc,
    DateTimeOffset? NextRetryAtUtc,
    string? FailureCategory,
    int CleanedCandidateCount);

public static class IndexHealthEndpoints
{
    public static IEndpointRouteBuilder MapFluxKnowledgeIndexHealth(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/index-health", Get);
        return endpoints;
    }

    private static IResult Get(IDerivedIndexRecoveryStatus recoveryStatus)
    {
        var snapshot = recoveryStatus.Snapshot;
        return Results.Ok(new IndexRecoveryProjection(
            snapshot.State.ToString(),
            snapshot.ActiveGenerationId?.ToString("N"),
            snapshot.LastCompletedAtUtc,
            snapshot.NextRetryAtUtc,
            snapshot.FailureCategory?.ToString(),
            snapshot.CleanedCandidateCount));
    }
}
