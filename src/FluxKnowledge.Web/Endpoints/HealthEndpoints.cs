using FluxKnowledge.Infrastructure.SqlServer.Configuration;
using FluxKnowledge.Infrastructure.SqlServer.Provisioning;
using FluxKnowledge.Application.Indexing;

namespace FluxKnowledge.Web.Endpoints;

public static class HealthEndpoints
{
    public static IEndpointRouteBuilder MapFluxKnowledgeHealth(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/health/live", () => Results.Ok());
        endpoints.MapGet("/health/ready", ReadyAsync);
        return endpoints;
    }

    private static async Task<IResult> ReadyAsync(
        ISqlServerReadinessValidator readinessValidator,
        IDerivedIndexRecoveryStatus recoveryStatus,
        SqlServerOptions options,
        CancellationToken cancellationToken)
    {
        SqlServerReadinessResult result;
        try
        {
            result = await readinessValidator
                .ValidateAsync(options, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is Microsoft.Data.SqlClient.SqlException or InvalidOperationException)
        {
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        }

        return result.IsReady && recoveryStatus.Snapshot.State == DerivedIndexRecoveryState.Healthy
            ? Results.Ok()
            : Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    }
}
