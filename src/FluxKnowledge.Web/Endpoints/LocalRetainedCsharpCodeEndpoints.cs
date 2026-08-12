using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Sources;

namespace FluxKnowledge.Web.Endpoints;

/// <summary>Direct-loopback-only trusted-local retained C# fact endpoints.</summary>
public static class LocalRetainedCsharpCodeEndpoints
{
    public static IEndpointRouteBuilder MapFluxKnowledgeLocalRetainedCsharpCode(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/local/retained-csharp-code/{branchId:guid}", ReadAsync);
        endpoints.MapGet("/api/local/retained-csharp-code/{branchId:guid}/excerpt", ReadExcerptAsync);
        endpoints.MapGet("/api/local/retained-csharp-code", SearchAsync);
        return endpoints;
    }

    private static async Task<IResult> ReadAsync(
        Guid branchId,
        int? symbolAfterOrdinal,
        int? referenceAfterOrdinal,
        int? diagnosticAfterOrdinal,
        ILocalRetainedCsharpCodeReader reader,
        CancellationToken cancellationToken)
    {
        try
        {
            var detail = await reader.ReadPageAsync(
                branchId,
                new LocalRetainedCsharpCodePageRequest(symbolAfterOrdinal, referenceAfterOrdinal, diagnosticAfterOrdinal),
                cancellationToken).ConfigureAwait(false);
            return detail is null
                ? Results.NotFound(new { reasonCode = "retained-csharp-code-unavailable" })
                : Results.Ok(detail);
        }
        catch (InvalidDataException)
        {
            return Results.Conflict(new { reasonCode = "retained-csharp-code-unavailable" });
        }
        catch (ArgumentOutOfRangeException)
        {
            return Results.BadRequest(new { reasonCode = "retained-csharp-code-page-invalid" });
        }
    }

    private static async Task<IResult> ReadExcerptAsync(
        Guid branchId,
        ILocalRetainedCsharpCodeReader reader,
        CancellationToken cancellationToken)
    {
        try
        {
            return Results.Ok(await reader.ReadExcerptAsync(branchId, cancellationToken).ConfigureAwait(false));
        }
        catch (FileNotFoundException)
        {
            return Results.NotFound(new { reasonCode = "retained-csharp-code-unavailable" });
        }
        catch (InvalidDataException)
        {
            return Results.Conflict(new { reasonCode = "retained-csharp-code-unavailable" });
        }
    }

    private static async Task<IResult> SearchAsync(
        string query,
        int? limit,
        string? cursor,
        ILocalRetainedCsharpCodeReader reader,
        CancellationToken cancellationToken)
    {
        try
        {
            return Results.Ok(await reader.SearchPageAsync(
                new LocalRetainedCsharpCodeSearchPageRequest(
                    query,
                    limit ?? 10,
                    cursor is null ? null : new LocalRetainedCsharpCodeSearchCursor(cursor)),
                cancellationToken).ConfigureAwait(false));
        }
        catch (LocalRetainedCsharpCodeSearchCursorException)
        {
            return Results.BadRequest(new { reasonCode = LocalRetainedCsharpCodeSearchCursorException.ReasonCode });
        }
        catch (ArgumentOutOfRangeException)
        {
            return Results.BadRequest(new { reasonCode = "retained-csharp-code-query-invalid" });
        }
        catch (InvalidDataException)
        {
            return Results.Conflict(new { reasonCode = "retained-csharp-code-unavailable" });
        }
    }

}
