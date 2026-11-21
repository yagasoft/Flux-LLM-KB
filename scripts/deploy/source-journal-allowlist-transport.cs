using System;
using System.Net;

public static class FluxSourceJournalOperatorTransport
{
    public static void RequireLocalWindowsAuthentication(HttpListener listener, string authority, string dashboardOrigin, string operatorOrigin)
    {
        listener.AuthenticationSchemeSelectorDelegate = request =>
            IsDirectLoopback(request, authority) && IsOperatorRequest(request, dashboardOrigin, operatorOrigin)
                ? AuthenticationSchemes.IntegratedWindowsAuthentication
                : AuthenticationSchemes.Anonymous;
    }

    private static bool IsOperatorRequest(HttpListenerRequest request, string dashboardOrigin, string operatorOrigin)
    {
        var path = request.RawUrl;
        if (request.HttpMethod == "POST")
            return path switch
            {
                "/review" or "/review-source-repair" => request.Headers["Origin"] == dashboardOrigin,
                "/activate" or "/activate-source-repair" => request.Headers["Origin"] == operatorOrigin,
                _ => false
            };

        var statusPrefix = path?.StartsWith("/source-repair/", StringComparison.Ordinal) == true ? "/source-repair/" : "/status/";
        return request.HttpMethod == "GET" && path is not null &&
            path.StartsWith(statusPrefix, StringComparison.Ordinal) &&
            Guid.TryParseExact(path.AsSpan(statusPrefix.Length), "D", out var operationId) &&
            path.AsSpan(statusPrefix.Length).SequenceEqual(operationId.ToString("D").AsSpan());
    }

    public static bool IsDirectLoopback(HttpListenerRequest request, string authority)
    {
        if (!IPAddress.IsLoopback(request.RemoteEndPoint.Address) ||
            !IPAddress.IsLoopback(request.LocalEndPoint.Address) ||
            !string.Equals(request.Headers["Host"], authority, StringComparison.Ordinal) ||
            request.Url is not Uri url || url.Scheme != "http" || url.Authority != authority)
            return false;

        foreach (var name in request.Headers.AllKeys)
        {
            if (name.Equals("Forwarded", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("Forwarded-", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("X-Forwarded", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("X-Original", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("X-Rewrite-", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("Proxy", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("X-Proxy", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("X-Real-IP", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("Via", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("True-Client-IP", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("CF-Connecting-IP", StringComparison.OrdinalIgnoreCase))
                return false;
        }
        return true;
    }
}
