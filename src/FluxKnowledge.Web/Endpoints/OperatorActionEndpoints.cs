using System.Security.Cryptography;
using FluxKnowledge.Application.Contracts;
using FluxKnowledge.Application.IntegrationV1;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Sources;
using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Web.Components.OperatorActions;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.DataProtection;

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
        endpoints.MapPost("/api/operator-actions/archive-zip-member-not-utf8/{branchId:guid}/reconcile",
            ReconcileArchiveZipMemberNotUtf8Async);
        endpoints.MapPost("/api/operator-actions/retained-recovery/{step:regex(^(preview|commit)$)}", GuidedRetainedRecoveryAsync);
        return endpoints;
    }

    private static async Task<IResult> GuidedRetainedRecoveryAsync(
        string step, HttpContext context, IAntiforgery antiforgery, LocalOperatorOriginPolicy originPolicy,
        IStatusEventPublisher statusEvents, TimeProvider clock, CancellationToken cancellationToken)
    {
        if (!LocalOperatorLoopbackGate.IsDirectLoopback(context) || !context.Request.Headers.ContainsKey("Origin") ||
            !HasSameOrigin(context.Request, originPolicy))
            return Results.Json(new { reasonCode = "operator-authority-denied" }, statusCode: StatusCodes.Status403Forbidden);
        try { await antiforgery.ValidateRequestAsync(context).ConfigureAwait(false); }
        catch (AntiforgeryValidationException)
        {
            return Results.Json(new { reasonCode = "operator-authority-denied" }, statusCode: StatusCodes.Status403Forbidden);
        }

        if (!context.Request.HasFormContentType)
            return Results.BadRequest(new { reasonCode = "invalid-payload" });
        var form = await context.Request.ReadFormAsync(cancellationToken).ConfigureAwait(false);
        var kind = form["kind"].ToString();
        if (!Guid.TryParse(form["sourceRevisionId"], out var revision) || revision == Guid.Empty ||
            kind is not ("zip" or "pdf" or "image" or "visio" or "policy") ||
            (kind != "visio" && (!Guid.TryParse(form["exactId"], out var exact) || exact == Guid.Empty)) ||
            (kind == "visio" && !string.IsNullOrEmpty(form["exactId"])))
            return Results.BadRequest(new { reasonCode = "invalid-payload" });
        var target = new RetainedRecoveryTarget(kind, revision,
            Guid.TryParse(form["exactId"], out var exactId) ? exactId : null);
        var confirmationId = form["confirmationId"].ToString();
        var idempotencyKey = form["idempotencyKey"].ToString();
        if (step == "commit" && (confirmationId.Length is 0 or > 256 || string.IsNullOrWhiteSpace(confirmationId) ||
            idempotencyKey.Length is 0 or > 128 || idempotencyKey.Any(value => value is < '!' or > '~')))
            return Results.BadRequest(new { reasonCode = "confirmation-required" });

        var commands = context.RequestServices.GetRequiredService<RetainedRecoveryNativeCommands>();
        try
        {
            if (step == "preview")
            {
                if (kind == "policy")
                {
                    var (preview, payload) = await commands.PreviewPolicyAsync(target, cancellationToken).ConfigureAwait(false);
                    return Results.Ok(new { preview, policyBinding = PolicyProtector(context).Protect(payload) });
                }
                return Results.Ok(new { preview = await commands.PreviewAsync(target, cancellationToken).ConfigureAwait(false) });
            }

            NativeActionReceipt receipt;
            if (kind == "policy")
            {
                var binding = form["policyBinding"].ToString();
                if (binding.Length is 0 or > 4096) return Results.BadRequest(new { reasonCode = "confirmation-mismatch" });
                var payload = PolicyProtector(context).Unprotect(binding);
                receipt = await commands.CommitPolicyAsync(target, payload, confirmationId, idempotencyKey, cancellationToken).ConfigureAwait(false);
            }
            else
                receipt = await commands.CommitAsync(target, confirmationId, idempotencyKey, cancellationToken).ConfigureAwait(false);
            await statusEvents.PublishAsync(new StatusChanged(null, "sources", clock.GetUtcNow()), CancellationToken.None).ConfigureAwait(false);
            return Results.Ok(new { receipt });
        }
        catch (NativeOperationException exception)
        {
            return Results.Conflict(new { reasonCode = exception.ReasonCode });
        }
        catch (NativeOperationCommitUncertainException)
        {
            return Results.Json(new { reasonCode = "commit-uncertain" }, statusCode: StatusCodes.Status503ServiceUnavailable);
        }
        catch (CryptographicException)
        {
            return Results.BadRequest(new { reasonCode = "confirmation-mismatch" });
        }
    }

    private static IDataProtector PolicyProtector(HttpContext context) => context.RequestServices.GetRequiredService<IDataProtectionProvider>()
        .CreateProtector("FluxKnowledge.Web.GuidedRetainedPolicy.v1");

    private static async Task<IResult> ReconcileArchiveZipMemberNotUtf8Async(
        Guid branchId,
        HttpContext context,
        IAntiforgery antiforgery,
        LocalOperatorOriginPolicy originPolicy,
        RetainedProcessorOptions options,
        IRetainedProcessorBranchStore branchStore,
        IStatusEventPublisher statusEvents,
        CancellationToken cancellationToken)
    {
        if (!LocalOperatorLoopbackGate.IsDirectLoopback(context) || !HasSameOrigin(context.Request, originPolicy))
            return Results.Json(new { reasonCode = "operator-authority-denied" }, statusCode: StatusCodes.Status403Forbidden);
        try { await antiforgery.ValidateRequestAsync(context).ConfigureAwait(false); }
        catch (AntiforgeryValidationException)
        {
            return Results.Json(new { reasonCode = "operator-authority-denied" }, statusCode: StatusCodes.Status403Forbidden);
        }

        if (!options.ArchiveZipExpandEnabled)
            return Results.Json(new { reasonCode = "archive-zip-descriptor-disabled" }, statusCode: StatusCodes.Status503ServiceUnavailable);

        var result = await branchStore.ReconcileArchiveZipMemberNotUtf8Async(branchId, cancellationToken).ConfigureAwait(false);
        if (!result.Accepted)
            return Results.Json(new { reasonCode = "archive-zip-reconciliation-not-eligible" }, statusCode: StatusCodes.Status409Conflict);
        if (result.Created)
        {
            await statusEvents.PublishAsync(new StatusChanged(null, "sources", DateTimeOffset.UtcNow), cancellationToken).ConfigureAwait(false);
        }

        return result.WasReplay
            ? Results.Ok(result)
            : Results.Created($"/api/operator-actions/archive-zip-member-not-utf8/{branchId:D}/reconcile", result);
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
