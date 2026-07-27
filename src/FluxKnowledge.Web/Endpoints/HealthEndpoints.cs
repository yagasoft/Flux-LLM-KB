using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using Microsoft.EntityFrameworkCore;

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
        IDbContextFactory<FluxKnowledgeDbContext> contextFactory,
        CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        if (!await context.Database.CanConnectAsync(cancellationToken).ConfigureAwait(false))
        {
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        }

        var active = await context.IndexState.AsNoTracking()
            .Where(state => state.Id == 1)
            .Select(state => state.ActiveIndexGenerationId)
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        if (active is null)
        {
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        }

        var valid = await context.IndexGenerations.AsNoTracking()
            .AnyAsync(generation => generation.Id == active && generation.ValidatedAtUtc != null, cancellationToken)
            .ConfigureAwait(false);
        return valid ? Results.Ok() : Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    }
}
