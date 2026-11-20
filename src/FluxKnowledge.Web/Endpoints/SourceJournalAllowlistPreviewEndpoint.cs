using System.Text.Json;
using FluxKnowledge.Web.Components.Sources;

namespace FluxKnowledge.Web.Endpoints;

internal static class SourceJournalAllowlistPreviewEndpoint
{
    internal static void MapSourceJournalAllowlistPreview(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapPost("/sources/journal/verify-preview", VerifyAsync);

    private static async Task<IResult> VerifyAsync(HttpContext context, SourceJournalAllowlistHandoff handoff,
        LocalOperatorOriginPolicy origin, SourceJournalOperator journal, SourceRootProjectionReader sources,
        CancellationToken cancellationToken)
    {
        if (handoff.ReviewUrl is null) return Results.NotFound();
        var request = context.Request;
        if (!LocalOperatorLoopbackGate.IsDirectLoopback(context) || request.Headers.ContainsKey("Origin") ||
            !Uri.TryCreate($"{request.Scheme}://{request.Host}", UriKind.Absolute, out var requestOrigin) || !origin.Matches(requestOrigin))
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        if (!request.HasJsonContentType() || request.ContentLength is not (>= 1 and <= 4096))
            return Results.BadRequest();

        try
        {
            using var preview = await JsonDocument.ParseAsync(request.Body, cancellationToken: cancellationToken);
            var root = preview.RootElement;
            if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != 3 ||
                !ReadString(root, "proposal", out var encoded) || !ReadString(root, "sha256", out var sha256) ||
                !ReadString(root, "impactSha256", out var impactSha256))
                return Results.BadRequest();

            using var proposed = JsonDocument.Parse(Convert.FromBase64String(encoded));
            var selection = proposed.RootElement.GetProperty("ApprovedVolumes").Deserialize<string[]>();
            if (selection is null || selection.Length is < 1 or > 16 || selection.Any(string.IsNullOrEmpty))
                return Results.BadRequest();

            var roots = await sources.ReadJournalRootsAsync(cancellationToken);
            var actual = journal.Preview(selection, journal.ReadLocalVolumes(), roots);
            if (!string.Equals(actual.Base64, encoded, StringComparison.Ordinal) ||
                !string.Equals(actual.Sha256, sha256, StringComparison.Ordinal) ||
                !string.Equals(SourceJournalAllowlistHandoff.BuildImpact(actual).Sha256, impactSha256, StringComparison.Ordinal))
                return Results.Conflict();

            return Results.NoContent();
        }
        catch (Exception error) when (error is JsonException or FormatException or KeyNotFoundException)
        {
            return Results.BadRequest();
        }
        catch (InvalidOperationException)
        {
            return Results.Conflict();
        }
    }

    private static bool ReadString(JsonElement root, string key, out string value)
    {
        value = string.Empty;
        if (!root.TryGetProperty(key, out var element) || element.ValueKind != JsonValueKind.String)
            return false;

        value = element.GetString()!;
        return value.Length > 0;
    }
}
