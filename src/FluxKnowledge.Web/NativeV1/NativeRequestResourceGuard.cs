using FluxKnowledge.Application.IntegrationV1;
using FluxKnowledge.Web.Mcp;

namespace FluxKnowledge.Web.NativeV1;

public static class NativeRequestResourceGuard
{
    public static IApplicationBuilder UseNativeRequestResources(this IApplicationBuilder app) => app.Use(async (context, next) =>
    {
        if (!context.Request.Path.StartsWithSegments("/mcp") && !context.Request.Path.StartsWithSegments("/api/v1") &&
            !context.Request.Path.StartsWithSegments("/native/v1/codex")) { await next(context); return; }
        using var reservation = new NativeRequestInput.Reservation();
        var original = context.Request.Body;
        await using var guarded = new NativeRequestInput.ResourceReadStream(original, reservation);
        context.Request.Body = guarded;
        try { await next(context); }
        catch (NativeOperationException exception) when (exception.ReasonCode == "resource-pressure" && !context.Response.HasStarted)
        {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            await context.Response.WriteAsJsonAsync(McpResultFactory.NativeFailure("resource-pressure", retryable: true), context.RequestAborted);
        }
        finally { context.Request.Body = original; }
    });
}
