using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Web.Components.Sources;
using Microsoft.EntityFrameworkCore;

namespace FluxKnowledge.Web.Endpoints;

internal static class SourceRepairAuthorityEndpoint
{
    internal static void MapSourceRepairAuthority(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapGet("/sources/{sourceId:guid}/repair-authority", ReadAsync);

    private static async Task<IResult> ReadAsync(Guid sourceId, HttpContext context, SourceJournalAllowlistHandoff handoff,
        LocalOperatorOriginPolicy origin, IDbContextFactory<FluxKnowledgeDbContext> factory, CancellationToken cancellationToken)
    {
        if (handoff.SourceRepairReviewUrl is null) return Results.NotFound();
        var request = context.Request;
        if (!LocalOperatorLoopbackGate.IsDirectLoopback(context) || request.Headers.ContainsKey("Origin") ||
            !Uri.TryCreate($"{request.Scheme}://{request.Host}", UriKind.Absolute, out var requestOrigin) || !origin.Matches(requestOrigin))
            return Results.StatusCode(StatusCodes.Status403Forbidden);

        context.Response.Headers.CacheControl = "no-store";
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var root = await db.SourceRootConfigurations.AsNoTracking()
            .Where(row => row.Id == sourceId && row.CrawlMode == (int)SourceDiscoveryMode.Filesystem &&
                (row.State == (int)SourceRootState.Enabled || row.State == (int)SourceRootState.Paused))
            .Select(row => new { row.Id, row.DisplayName, row.CanonicalPath, row.ConfigurationRevision, row.HealthEvidenceJson })
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (root is null || SqlSourceScanStore.ParseAdmissionIdentityFingerprint(root.HealthEvidenceJson) is not { } fingerprint)
            return Results.NotFound();

        return Results.Json(new { sourceId = root.Id, path = root.CanonicalPath, root.DisplayName,
            revision = root.ConfigurationRevision, fingerprint });
    }
}
