using FluxKnowledge.Infrastructure.SqlServer.Configuration;
using FluxKnowledge.Infrastructure.SqlServer.Provisioning;
using FluxKnowledge.Application.Indexing;
using FluxKnowledge.Integrations.Windows.NativeGoLive;

namespace FluxKnowledge.Web.Endpoints;

public static class HealthEndpoints
{
    public static IEndpointRouteBuilder MapFluxKnowledgeHealth(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/health/live", Live);
        endpoints.MapGet("/health/ready", ReadyAsync);
        return endpoints;
    }

    private static IResult Live(HttpResponse response)
    {
        AddNativeProofMarker(response);
        return Results.Ok();
    }

    private static async Task<IResult> ReadyAsync(
        HttpResponse response,
        ISqlServerReadinessValidator readinessValidator,
        IDerivedIndexRecoveryStatus recoveryStatus,
        SqlServerOptions options,
        CancellationToken cancellationToken)
    {
        AddNativeProofMarker(response);
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

    private static void AddNativeProofMarker(HttpResponse response) =>
        response.Headers[NativeGoLiveLoopbackContract.NativeProofHeader] =
            NativeGoLiveLoopbackContract.NativeProofValue;
}
