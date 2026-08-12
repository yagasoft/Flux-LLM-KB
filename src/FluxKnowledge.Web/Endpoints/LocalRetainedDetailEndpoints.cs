using FluxKnowledge.Application.Ports;

namespace FluxKnowledge.Web.Endpoints;

/// <summary>Direct-loopback-only trusted-local retained branch detail endpoints.</summary>
public static class LocalRetainedDetailEndpoints
{
    public static IEndpointRouteBuilder MapFluxKnowledgeLocalRetainedDetails(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/local/retained-branches/{branchId:guid}", ReadAsync);
        endpoints.MapGet("/api/local/retained-branches/{branchId:guid}/excerpt", ReadExcerptAsync);
        return endpoints;
    }

    private static async Task<IResult> ReadAsync(
        Guid branchId,
        ILocalRetainedDetailReader reader,
        CancellationToken cancellationToken)
    {
        try
        {
            var detail = await reader.ReadAsync(branchId, cancellationToken).ConfigureAwait(false);
            return detail is null ? Results.NotFound(new { reasonCode = "retained-detail-unavailable" }) : Results.Ok(detail);
        }
        catch (InvalidDataException)
        {
            return Results.Conflict(new { reasonCode = "retained-detail-unavailable" });
        }
    }

    private static async Task<IResult> ReadExcerptAsync(
        Guid branchId,
        ILocalRetainedDetailReader reader,
        CancellationToken cancellationToken)
    {
        try
        {
            return Results.Ok(await reader.ReadExcerptAsync(branchId, cancellationToken).ConfigureAwait(false));
        }
        catch (FileNotFoundException)
        {
            return Results.NotFound(new { reasonCode = "retained-detail-unavailable" });
        }
        catch (InvalidDataException)
        {
            return Results.Conflict(new { reasonCode = "retained-detail-unavailable" });
        }
    }
}
