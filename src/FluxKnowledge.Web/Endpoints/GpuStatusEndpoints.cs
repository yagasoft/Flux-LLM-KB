using System.Data.Common;
using FluxKnowledge.Web.Components.Status;

namespace FluxKnowledge.Web.Endpoints;

public static class GpuStatusEndpoints
{
    public static IEndpointRouteBuilder MapFluxKnowledgeGpuStatus(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/gpu-status", GetAsync);
        return endpoints;
    }

    private static async Task<IResult> GetAsync(
        IProjectionReader reader,
        CancellationToken cancellationToken)
    {
        try
        {
            return Results.Ok(await reader
                .ReadGpuSchedulerStatusAsync(cancellationToken)
                .ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is DbException or InvalidOperationException)
        {
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        }
    }
}
