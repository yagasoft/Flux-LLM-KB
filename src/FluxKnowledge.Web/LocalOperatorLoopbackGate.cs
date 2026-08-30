using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace FluxKnowledge.Web;

/// <summary>Fixed loopback authority used for browser same-origin validation.</summary>
public sealed class LocalOperatorOriginPolicy
{
    private readonly Uri canonicalOrigin;

    public LocalOperatorOriginPolicy(string configuredOrigin)
    {
        if (!Uri.TryCreate(configuredOrigin, UriKind.Absolute, out var origin) ||
            origin.Scheme is not ("http" or "https") ||
            !IsLoopbackHost(origin) ||
            !string.IsNullOrEmpty(origin.UserInfo) ||
            origin.AbsolutePath != "/" ||
            !string.IsNullOrEmpty(origin.Query) ||
            !string.IsNullOrEmpty(origin.Fragment))
        {
            throw new InvalidOperationException("LocalOperator:CanonicalOrigin must be an absolute HTTP loopback origin without a path, query, or fragment.");
        }

        canonicalOrigin = origin;
    }

    public bool Matches(Uri supplied) =>
        string.Equals(canonicalOrigin.Scheme, supplied.Scheme, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(canonicalOrigin.Host, supplied.Host, StringComparison.OrdinalIgnoreCase) &&
        canonicalOrigin.Port == supplied.Port;

    private static bool IsLoopbackHost(Uri origin) =>
        string.Equals(origin.Host, "localhost", StringComparison.OrdinalIgnoreCase) ||
        IPAddress.TryParse(origin.Host, out var address) && IPAddress.IsLoopback(address);
}

/// <summary>Restricts every anonymous operator surface to a direct, unproxied loopback peer.</summary>
public static class LocalOperatorLoopbackGate
{
    public static IApplicationBuilder UseLocalOperatorLoopbackGate(this IApplicationBuilder application)
    {
        ArgumentNullException.ThrowIfNull(application);
        return application.UseWhen(
            static context => context.Request.Path.StartsWithSegments("/health") ||
                context.Request.Path.StartsWithSegments("/outlook") ||
                context.Request.Path.StartsWithSegments("/operator-actions") ||
                context.Request.Path.StartsWithSegments("/api/operator-actions") ||
                context.Request.Path.StartsWithSegments("/api/local/retained-branches") ||
                context.Request.Path.StartsWithSegments("/api/local/retained-csharp-code") ||
                context.Request.Path.StartsWithSegments("/api/v1") ||
                context.Request.Path.StartsWithSegments("/sources/retained") ||
                context.Request.Path.StartsWithSegments("/search/csharp-code") ||
                context.Request.Path.StartsWithSegments("/mcp") ||
                context.Request.Path.StartsWithSegments("/_blazor"),
            branch => branch.Use(async (context, next) =>
            {
                if (!IsDirectLoopback(context))
                {
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    return;
                }

                await next(context).ConfigureAwait(false);
            }));
    }

    public static bool IsDirectLoopback(HttpContext context) =>
        context.Connection.RemoteIpAddress is { } remoteAddress &&
        IPAddress.IsLoopback(remoteAddress) &&
        !context.Request.Headers.Any(header =>
            string.Equals(header.Key, "Forwarded", StringComparison.OrdinalIgnoreCase) ||
            header.Key.StartsWith("Forwarded-", StringComparison.OrdinalIgnoreCase) ||
            header.Key.StartsWith("X-Forwarded", StringComparison.OrdinalIgnoreCase) ||
            header.Key.StartsWith("X-Original", StringComparison.OrdinalIgnoreCase) ||
            header.Key.StartsWith("Proxy", StringComparison.OrdinalIgnoreCase) ||
            header.Key.StartsWith("X-Proxy", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(header.Key, "X-Real-IP", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(header.Key, "Via", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(header.Key, "True-Client-IP", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(header.Key, "CF-Connecting-IP", StringComparison.OrdinalIgnoreCase));
}
