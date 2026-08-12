using FluxKnowledge.Application.Contracts;
using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Web.Components.OperatorActions;
using Microsoft.AspNetCore.Antiforgery;

namespace FluxKnowledge.Web.Endpoints;

public sealed record OperatorActionMutationRequest(
    Guid OperationId,
    string RequestFingerprint,
    string ExpectedBlockedRowVersion);

public static class OperatorActionEndpoints
{
    public static IEndpointRouteBuilder MapFluxKnowledgeOperatorActions(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/operator-actions", ListAsync);
        endpoints.MapGet("/api/operator-actions/{actionId}", GetAsync);
        foreach (var routeAction in new[] { "override", "retry", "ignore", "unignore" })
        {
            endpoints.MapPost($"/api/operator-actions/{{actionId}}/{routeAction}",
                (string actionId, OperatorActionMutationRequest request, HttpContext context,
                    IAntiforgery antiforgery, LocalOperatorOriginPolicy originPolicy,
                    OperatorActionService service, CancellationToken cancellationToken) =>
                    MutateAsync(actionId, routeAction, request, context, antiforgery, originPolicy, service, cancellationToken));
        }
        return endpoints;
    }

    private static async Task<IResult> ListAsync(
        bool? includeIgnored,
        OperatorActionService service,
        CancellationToken cancellationToken) =>
        Results.Ok(await service.ListAsync(includeIgnored ?? false, cancellationToken).ConfigureAwait(false));

    private static async Task<IResult> GetAsync(
        string actionId,
        bool? includeIgnored,
        OperatorActionService service,
        CancellationToken cancellationToken)
    {
        try { OoxmlForceRequestIdentity.RequireSha256(actionId, nameof(actionId)); }
        catch (ArgumentException) { return Results.BadRequest(new { reasonCode = "operator-request-invalid" }); }
        var action = (await service.ListAsync(includeIgnored ?? false, cancellationToken).ConfigureAwait(false))
            .SingleOrDefault(value => string.Equals(value.ActionId, actionId, StringComparison.Ordinal));
        return action is null ? Results.NotFound(new { reasonCode = "operator-action-unlisted" }) : Results.Ok(action);
    }

    private static async Task<IResult> MutateAsync(
        string actionId,
        string routeAction,
        OperatorActionMutationRequest request,
        HttpContext context,
        IAntiforgery antiforgery,
        LocalOperatorOriginPolicy originPolicy,
        OperatorActionService service,
        CancellationToken cancellationToken)
    {
        if (!LocalOperatorLoopbackGate.IsDirectLoopback(context) || !HasSameOrigin(context.Request, originPolicy))
            return Results.Json(new { reasonCode = "operator-authority-denied" }, statusCode: StatusCodes.Status403Forbidden);
        try { await antiforgery.ValidateRequestAsync(context).ConfigureAwait(false); }
        catch (AntiforgeryValidationException)
        {
            return Results.Json(new { reasonCode = "operator-authority-denied" }, statusCode: StatusCodes.Status403Forbidden);
        }

        try
        {
            if (request.OperationId == Guid.Empty) throw new ArgumentException("Operation identity is required.");
            var actionKind = routeAction == "override" ? "policy-override" : routeAction;
            var receipt = await service.ExecuteAsync(new OperatorActionMutationCommand(
                actionId, request.OperationId, request.RequestFingerprint,
                request.ExpectedBlockedRowVersion, actionKind), cancellationToken).ConfigureAwait(false);
            return receipt.WasReplay ? Results.Ok(receipt) : Results.Created($"/api/operator-actions/{actionId}", receipt);
        }
        catch (ArgumentException)
        {
            return Results.BadRequest(new { reasonCode = "operator-request-invalid" });
        }
        catch (OperatorActionRequestRejectedException exception)
        {
            return Results.Json(new { reasonCode = exception.ReasonCode }, statusCode: MapStatus(exception.ReasonCode));
        }
    }

    private static bool HasSameOrigin(HttpRequest request, LocalOperatorOriginPolicy originPolicy)
    {
        foreach (var header in new[] { "Origin", "Referer" })
        {
            if (!request.Headers.TryGetValue(header, out var raw)) continue;
            if (!Uri.TryCreate(raw.ToString(), UriKind.Absolute, out var supplied) ||
                !originPolicy.Matches(supplied))
                return false;
        }
        return true;
    }

    private static int MapStatus(string reasonCode) => reasonCode switch
    {
        "operator-action-unlisted" => StatusCodes.Status404NotFound,
        "operator-descriptor-disabled" => StatusCodes.Status503ServiceUnavailable,
        _ => StatusCodes.Status409Conflict
    };
}
