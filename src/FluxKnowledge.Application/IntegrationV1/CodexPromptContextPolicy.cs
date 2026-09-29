using System.Text;
using System.Text.RegularExpressions;

namespace FluxKnowledge.Application.IntegrationV1;

/// <summary>Conservative, body-only admission for automatic workspace context.</summary>
public static partial class CodexPromptContextPolicy
{
    public const string Version = "workspace-lexical-v1";

    private static readonly HashSet<string> Exclusions = new(
        ("a an and are as at be been but by can could did do does for from had has have " +
         "how i if in into is it its me my of on or our please should so than that the " +
         "their them then there these they this those to us was we were what when where " +
         "which who why will with would you your also again continue done help next " +
         "now okay proceed thanks yes").Split(' '), StringComparer.OrdinalIgnoreCase);

    [GeneratedRegex(@"[\p{L}\p{N}]+(?:[._:-][\p{L}\p{N}]+)*", RegexOptions.CultureInvariant)]
    private static partial Regex TokenPattern();

    public static QueryTerms Analyse(string prompt)
    {
        var tokens = Tokens(prompt).ToArray();
        var ordinary = tokens.Where(token => token.Length >= 3 && !Exclusions.Contains(token))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var identifiers = tokens.Where(token => token.Length >= 4 && !Exclusions.Contains(token) && IsIdentifier(token))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        return new QueryTerms(ordinary, identifiers, ordinary.Length >= 2 || identifiers.Length > 0);
    }

    public static bool MatchesBody(QueryTerms query, string body)
    {
        if (!query.Eligible) return false;
        var bodyTokens = Tokens(body).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (query.Identifiers.Any(bodyTokens.Contains)) return true;
        if (query.Ordinary.Length < 2) return false;
        var required = Math.Max(2, (query.Ordinary.Length + 1) / 2);
        return query.Ordinary.Count(bodyTokens.Contains) >= required;
    }

    public static string NormaliseBodyIdentity(string body) =>
        Regex.Replace(body.Normalize(NormalizationForm.FormC), @"\s+", " ").Trim();

    private static IEnumerable<string> Tokens(string text) => TokenPattern()
        .Matches(text.Normalize(NormalizationForm.FormC))
        .Select(match => match.Value);

    private static bool IsIdentifier(string token) => token.Length >= 4 &&
        (token.Any(char.IsDigit) || token.Contains('_') || token.Count(char.IsUpper) >= 2);

    public sealed record QueryTerms(string[] Ordinary, string[] Identifiers, bool Eligible);
}
