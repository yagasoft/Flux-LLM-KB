using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace FluxKnowledge.Web;

/// <summary>Restricts the Outlook UI and its interactive circuit to direct loopback peers.</summary>
public static class OutlookOperatorLoopbackGate
{
    public static IApplicationBuilder UseOutlookOperatorLoopbackGate(this IApplicationBuilder application)
    {
        ArgumentNullException.ThrowIfNull(application);

        return application.UseWhen(
            static context => context.Request.Path.StartsWithSegments("/outlook") ||
                context.Request.Path.StartsWithSegments("/_blazor"),
            branch => branch.Use(async (context, next) =>
            {
                if (context.Connection.RemoteIpAddress is not { } remoteAddress ||
                    !IPAddress.IsLoopback(remoteAddress))
                {
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    return;
                }

                await next(context).ConfigureAwait(false);
            }));
    }
}
