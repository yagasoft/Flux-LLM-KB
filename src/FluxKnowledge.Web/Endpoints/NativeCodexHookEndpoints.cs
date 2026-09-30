using System.Text.Json;
using FluxKnowledge.Application.IntegrationV1;
using FluxKnowledge.Web.Mcp;

namespace FluxKnowledge.Web.Endpoints;

/// <summary>Loopback-only HTTP adapter for Codex command hooks.</summary>
public static class NativeCodexHookEndpoints
{
    public static IEndpointRouteBuilder MapFluxKnowledgeNativeCodexHooks(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        endpoints.MapPost("/native/v1/codex/hooks/{eventName}", HandleAsync);
        return endpoints;
    }

    private static async Task<IResult> HandleAsync(
        string eventName,
        HttpContext context,
        NativeCodexHookService service,
        CancellationToken cancellationToken)
    {
        if (!LocalOperatorLoopbackGate.IsDirectLoopback(context)) return Results.StatusCode(StatusCodes.Status403Forbidden);

        try
        {
            using var document = await NativeRequestInput.ReadJsonAsync(context.Request.Body, cancellationToken).ConfigureAwait(false);
            var payload = document.RootElement;
            var response = await service.HandleAsync(eventName, payload, cancellationToken).ConfigureAwait(false);
            return Results.Json(response);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (JsonException)
        {
            return Results.Json(await service.HandleInvalidInputAsync(cancellationToken).ConfigureAwait(false));
        }
        catch (NativeOperationException)
        {
            return Results.Json(await service.HandleInvalidInputAsync(cancellationToken).ConfigureAwait(false));
        }
    }

}
