using System.Security.Cryptography;
using System.Text.Json;

namespace FluxKnowledge.Web.Components.Sources;

internal sealed class SourceJournalAllowlistHandoff
{
    internal SourceJournalAllowlistHandoff(string? operatorOrigin)
    {
        if (string.IsNullOrWhiteSpace(operatorOrigin)) return;

        if (!Uri.TryCreate(operatorOrigin, UriKind.Absolute, out var origin) ||
            origin.Scheme != "http" ||
            origin.Host != "localhost" || origin.Port < 1024 || origin.Port == 5137 ||
            origin.UserInfo.Length != 0 || origin.AbsolutePath != "/" ||
            origin.Query.Length != 0 || origin.Fragment.Length != 0 ||
            !(string.Equals(operatorOrigin, origin.GetLeftPart(UriPartial.Authority), StringComparison.Ordinal) ||
              string.Equals(operatorOrigin, origin.AbsoluteUri, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException("SourceJournalAllowlistOperator:Origin must use the exact http://localhost origin with an explicit port.");
        }

        ReviewUrl = new Uri(origin, "/review").AbsoluteUri;
        SourceRepairReviewUrl = new Uri(origin, "/review-source-repair").AbsoluteUri;
    }

    internal string? ReviewUrl { get; }
    internal string? SourceRepairReviewUrl { get; }

    internal const string ImpactDigestPrefix = "Source impact SHA-256 ";

    internal static (string Base64, string Sha256) BuildImpact(JournalAllowlistProposal proposal)
    {
        var affected = proposal.Impacts.OrderBy(row => row.Root.Id).ThenBy(row => row.Volume, StringComparer.Ordinal)
            .ThenBy(row => row.Added).ToArray();
        var unmapped = proposal.Unmapped.OrderBy(row => row.Id).ToArray();
        var fullImpact = JsonSerializer.SerializeToUtf8Bytes(new { affected, unmapped });
        var digest = ImpactDigestPrefix + Convert.ToHexString(SHA256.HashData(fullImpact));

        var descriptions = affected.Select(row =>
            $"{row.Root.Id:D} {row.Root.DisplayName} ({row.Root.State}) · {row.Root.CanonicalPath} · {row.Volume} · {(row.Added ? "Journal access added" : "Watcher and metadata fallback")}")
            .Concat(unmapped.Select(row => $"{row.Id:D} {row.DisplayName} ({row.State}) · {row.CanonicalPath} · Local volume unknown"));
        // Reserve room for the complete impact digest and an omitted-row summary.
        const int displayBudget = 11 * 1024;
        var shown = new List<string>();
        var displayBytes = 0;
        foreach (var description in descriptions.Take(126))
        {
            var display = string.Concat(description.Select(character =>
                char.IsControl(character) || character is >= '\u202A' and <= '\u202E' or >= '\u2066' and <= '\u2069' ? ' ' : character));
            if (display.Length > 900) display = display[..900] + "…";
            displayBytes += JsonSerializer.SerializeToUtf8Bytes(display).Length + 1;
            if (displayBytes > displayBudget) break;
            shown.Add(display);
        }

        var omitted = affected.Length + unmapped.Length - shown.Count;
        if (omitted > 0) shown.Add($"{omitted} more sources are listed in the dashboard preview.");
        shown.Add(digest);

        var bytes = JsonSerializer.SerializeToUtf8Bytes(shown);
        return (Convert.ToBase64String(bytes), Convert.ToHexString(SHA256.HashData(bytes)));
    }
}
