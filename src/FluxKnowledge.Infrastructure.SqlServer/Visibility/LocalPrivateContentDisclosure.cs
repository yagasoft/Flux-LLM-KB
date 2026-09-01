using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using FluxKnowledge.Application.Visibility;

namespace FluxKnowledge.Infrastructure.SqlServer.Visibility;

/// <summary>Bounded secret detector for values eligible only for trusted-local presentation.</summary>
public sealed partial class LocalPrivateContentDisclosure : ILocalPrivateContentDisclosure
{
    private const int MaximumScannedCharacters = 16 * 1024;
    private const int MaximumEncodedJsonStringDepth = 8;
    private const int MaximumEmbeddedJsonCandidates = 64;
    private const int MaximumEmbeddedJsonCandidateCharacters = 4 * 1024;
    private const int MaximumJsonNestingDepth = 32;
    private const string WithheldReason = "secret-content-withheld";
    private static readonly string[] CredentialPropertyMarkers =
    [
        "password",
        "passphrase",
        "accesstoken",
        "refreshtoken",
        "idtoken",
        "apikey",
        "clientsecret",
        "connectionstring",
        "authorization",
        "cookie",
        "setcookie",
        "privatekey"
    ];

    public LocalDisclosureResult Evaluate(string value, LocalDisclosureKind kind)
    {
        ArgumentNullException.ThrowIfNull(value);

        if (value.Length > MaximumScannedCharacters)
        {
            return new LocalDisclosureResult(null, true, WithheldReason);
        }

        try
        {
            if (ContainsSecret(value)) return new LocalDisclosureResult(null, true, WithheldReason);
        }
        catch (RegexMatchTimeoutException)
        {
            return new LocalDisclosureResult(null, true, WithheldReason);
        }

        return new LocalDisclosureResult(value, false, null);
    }

    private static bool ContainsSecret(string value, int encodedDepth = 0) =>
        value.Contains("secret-content-sentinel", StringComparison.Ordinal) ||
        PrivateKeyEnvelopePattern().IsMatch(value) ||
        CredentialUriPattern().IsMatch(value) ||
        SecretAssignmentPattern().IsMatch(value) ||
        CredentialHeaderPattern().IsMatch(value) ||
        ContainsJsonCredential(value) ||
        ContainsEncodedCredential(value, encodedDepth);

    private static bool ContainsEncodedCredential(string value, int encodedDepth)
    {
        var normalised = string.Concat(value.Where(character => !char.IsWhiteSpace(character)));
        if (normalised.Length < 12 ||
            !normalised.All(character => char.IsLetterOrDigit(character) || character is '+' or '/' or '-' or '_' or '='))
        {
            return false;
        }

        normalised = normalised.Replace('-', '+').Replace('_', '/');
        normalised = normalised.PadRight(normalised.Length + (4 - normalised.Length % 4) % 4, '=');
        try
        {
            var decoded = new UTF8Encoding(false, true).GetString(Convert.FromBase64String(normalised));
            return encodedDepth >= 2 || ContainsSecret(decoded, encodedDepth + 1);
        }
        catch (Exception exception) when (exception is FormatException or DecoderFallbackException)
        {
            return false;
        }
    }

    private static bool ContainsJsonCredential(string value, int encodedJsonStringDepth = 0)
    {
        if (!LooksLikeJson(value))
        {
            return ContainsEmbeddedJsonCredential(value, encodedJsonStringDepth);
        }

        if (encodedJsonStringDepth >= MaximumEncodedJsonStringDepth)
        {
            return true;
        }

        try
        {
            using var document = JsonDocument.Parse(value, new JsonDocumentOptions { MaxDepth = 32 });
            return ContainsJsonCredential(document.RootElement, encodedJsonStringDepth);
        }
        catch (JsonException)
        {
            // A JSON-shaped diagnostic we cannot parse within the bounded reader
            // cannot be established as credential-safe, so local projection fails closed.
            return true;
        }
    }

    private static bool ContainsEmbeddedJsonCredential(string value, int encodedJsonStringDepth)
    {
        var candidates = 0;
        for (var start = 0; start < value.Length; start++)
        {
            if (value[start] is not ('{' or '['))
            {
                continue;
            }

            if (++candidates > MaximumEmbeddedJsonCandidates)
            {
                return true;
            }

            var scan = ScanEmbeddedJsonCandidate(value, start);
            if (scan.BoundExceeded || ContainsCredentialPropertyToken(
                    value,
                    start,
                    scan.ScannedEndExclusive,
                    scan.IsEscaped,
                    scan.EndIndex is null || scan.IsMalformed))
            {
                return true;
            }

            if (ContainsCredentialInCandidateTail(
                    value,
                    start,
                    scan.FollowingTailStartIndex,
                    scan.IsEscaped))
            {
                return true;
            }

            if (scan.EndIndex is not int end)
            {
                continue;
            }

            var fragment = value[start..(end + 1)];
            if (scan.IsEscaped)
            {
                if (TryDecodeEscapedJsonFragment(fragment, out var decoded))
                {
                    if (ContainsJsonCredential(decoded, encodedJsonStringDepth + 1))
                    {
                        return true;
                    }
                }
            }
            else if (TryParseJson(fragment, out var document) && document is not null)
            {
                using (document)
                {
                    if (ContainsJsonCredential(document.RootElement, encodedJsonStringDepth))
                    {
                        return true;
                    }
                }
            }
            else if (ContainsCredentialPropertyToken(
                         value,
                         start,
                         end + 1,
                         escapedJson: false,
                         failClosedOnAmbiguousKey: true))
            {
                return true;
            }
        }

        return false;
    }

    private static bool ContainsCredentialInCandidateTail(
        string value,
        int candidateStart,
        int tailStart,
        bool escapedJson)
    {
        var scanLimit = Math.Min(value.Length, candidateStart + MaximumEmbeddedJsonCandidateCharacters);
        return value.Length - candidateStart > MaximumEmbeddedJsonCandidateCharacters ||
            ContainsCredentialPropertyToken(
                value,
                tailStart,
                scanLimit,
                escapedJson,
                failClosedOnAmbiguousKey: true) ||
            ContainsCredentialPropertyToken(
                value,
                tailStart,
                scanLimit,
                !escapedJson,
                failClosedOnAmbiguousKey: true);
    }

    private static JsonCandidateScanResult ScanEmbeddedJsonCandidate(string value, int start)
    {
        var escapedJson = IsEscapedJsonCandidate(value, start);
        Span<char> containers = stackalloc char[MaximumJsonNestingDepth];
        var depth = 0;
        var insideString = false;
        var rawEscape = false;
        var scanLimit = Math.Min(value.Length, start + MaximumEmbeddedJsonCandidateCharacters);

        for (var index = start; index < scanLimit; index++)
        {
            var character = value[index];
            if (escapedJson)
            {
                if (IsTransportEscapedQuoteAt(value, index))
                {
                    insideString = !insideString;
                    index++;
                    continue;
                }

                if (insideString)
                {
                    continue;
                }
            }
            else if (insideString)
            {
                if (rawEscape)
                {
                    rawEscape = false;
                }
                else if (character == '\\')
                {
                    rawEscape = true;
                }
                else if (character == '"')
                {
                    insideString = false;
                }

                continue;
            }
            else if (character == '"')
            {
                insideString = true;
                continue;
            }

            if (character is '{' or '[')
            {
                if (depth == MaximumJsonNestingDepth)
                {
                    return new JsonCandidateScanResult(
                        scanLimit,
                        null,
                        escapedJson,
                        BoundExceeded: true,
                        IsMalformed: false);
                }

                containers[depth++] = character;
                continue;
            }

            if (character is not ('}' or ']'))
            {
                continue;
            }

            if (depth == 0 || !IsMatchingContainer(containers[depth - 1], character))
            {
                return new JsonCandidateScanResult(
                    index + 1,
                    null,
                    escapedJson,
                    BoundExceeded: false,
                    IsMalformed: true);
            }

            depth--;
            if (depth == 0)
            {
                return new JsonCandidateScanResult(
                    index + 1,
                    index,
                    escapedJson,
                    BoundExceeded: false,
                    IsMalformed: false);
            }
        }

        return new JsonCandidateScanResult(
            scanLimit,
            null,
            escapedJson,
            BoundExceeded: value.Length - start > MaximumEmbeddedJsonCandidateCharacters,
            IsMalformed: false);
    }

    private static bool ContainsCredentialPropertyToken(
        string value,
        int start,
        int endExclusive,
        bool escapedJson,
        bool failClosedOnAmbiguousKey)
    {
        for (var index = start; index < endExclusive; index++)
        {
            int contentStart;
            int contentEnd;
            int afterToken;

            if (escapedJson)
            {
                if (!IsTransportEscapedQuoteAt(value, index))
                {
                    continue;
                }

                contentStart = index + 2;
                var closingDelimiter = FindTransportEscapedQuote(value, contentStart, endExclusive);
                if (closingDelimiter < 0)
                {
                    return failClosedOnAmbiguousKey &&
                        IsPotentialJsonCredentialProperty(value[contentStart..endExclusive]);
                }

                contentEnd = closingDelimiter;
                afterToken = closingDelimiter + 2;
                index = closingDelimiter + 1;
            }
            else
            {
                if (value[index] != '"')
                {
                    continue;
                }

                contentStart = index + 1;
                var closingQuote = FindRawJsonStringEnd(value, contentStart, endExclusive);
                if (closingQuote < 0)
                {
                    return failClosedOnAmbiguousKey &&
                        IsPotentialJsonCredentialProperty(value[contentStart..endExclusive]);
                }

                contentEnd = closingQuote;
                afterToken = closingQuote + 1;
                index = closingQuote;
            }

            while (afterToken < endExclusive && char.IsWhiteSpace(value[afterToken]))
            {
                afterToken++;
            }

            var isProperty = afterToken < endExclusive && value[afterToken] == ':';
            if (isProperty || failClosedOnAmbiguousKey)
            {
                var propertyName = DecodeJsonPropertyName(value, contentStart, contentEnd);
                if (isProperty
                    ? IsJsonCredentialProperty(propertyName)
                    : IsPotentialJsonCredentialProperty(propertyName))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static int FindRawJsonStringEnd(string value, int start, int endExclusive)
    {
        var escaped = false;
        for (var index = start; index < endExclusive; index++)
        {
            if (escaped)
            {
                escaped = false;
            }
            else if (value[index] == '\\')
            {
                escaped = true;
            }
            else if (value[index] == '"')
            {
                return index;
            }
        }

        return -1;
    }

    private static int FindTransportEscapedQuote(string value, int start, int endExclusive)
    {
        for (var index = start; index + 1 < endExclusive; index++)
        {
            if (IsTransportEscapedQuoteAt(value, index))
            {
                return index;
            }
        }

        return -1;
    }

    private static bool IsEscapedJsonCandidate(string value, int start)
    {
        var index = start + 1;
        while (index < value.Length && char.IsWhiteSpace(value[index]))
        {
            index++;
        }

        return IsTransportEscapedQuoteAt(value, index);
    }

    private static bool IsTransportEscapedQuoteAt(string value, int index) =>
        index >= 0 &&
        index + 1 < value.Length &&
        value[index] == '\\' &&
        value[index + 1] == '"' &&
        (index == 0 || value[index - 1] != '\\');

    private static bool IsMatchingContainer(char opening, char closing) =>
        (opening == '{' && closing == '}') || (opening == '[' && closing == ']');

    private static string DecodeJsonPropertyName(
        string value,
        int contentStart,
        int contentEnd)
    {
        var content = value[contentStart..contentEnd];
        if (!content.Contains('\\'))
        {
            return content;
        }

        try
        {
            return JsonSerializer.Deserialize<string>($"\"{content}\"") ?? content;
        }
        catch (JsonException)
        {
            return content;
        }
    }

    private static bool TryDecodeEscapedJsonFragment(string fragment, out string decoded)
    {
        if (TryParseJson($"\"{fragment}\"", out var document) && document is not null)
        {
            using (document)
            {
                decoded = document.RootElement.GetString() ?? string.Empty;
                return true;
            }
        }

        decoded = string.Empty;
        return false;
    }

    private static bool TryParseJson(string value, out JsonDocument? document)
    {
        try
        {
            document = JsonDocument.Parse(value, new JsonDocumentOptions { MaxDepth = 32 });
            return true;
        }
        catch (JsonException)
        {
            document = null;
            return false;
        }
    }

    private static bool ContainsJsonCredential(JsonElement element, int encodedJsonStringDepth) => element.ValueKind switch
    {
        JsonValueKind.Object => element.EnumerateObject().Any(property =>
            IsJsonCredentialProperty(property.Name) || ContainsJsonCredential(property.Value, encodedJsonStringDepth)),
        JsonValueKind.Array => element.EnumerateArray().Any(item => ContainsJsonCredential(item, encodedJsonStringDepth)),
        JsonValueKind.String => ContainsCredentialText(element.GetString() ?? string.Empty) ||
            ContainsJsonCredential(element.GetString() ?? string.Empty, encodedJsonStringDepth + 1),
        _ => false
    };

    private static bool LooksLikeJson(string value)
    {
        var index = 0;
        while (index < value.Length && char.IsWhiteSpace(value[index]))
        {
            index++;
        }

        return index < value.Length && value[index] is '{' or '[' or '"';
    }

    private static bool IsJsonCredentialProperty(string propertyName)
    {
        var normalized = NormalizeJsonPropertyName(propertyName);
        return normalized is "password" or "pwd" or "passphrase" or "accesstoken" or "refreshtoken" or "idtoken" or "token" or
            "apikey" or "clientsecret" or "connectionstring" or "authorization" or "cookie" or "setcookie" or "privatekey" or
            "accesstokenvalue" or "oauthclientsecret" ||
            normalized.Contains("accesstoken", StringComparison.Ordinal) ||
            normalized.Contains("refreshtoken", StringComparison.Ordinal) ||
            normalized.Contains("clientsecret", StringComparison.Ordinal);
    }

    private static bool IsPotentialJsonCredentialProperty(string propertyName)
    {
        if (IsJsonCredentialProperty(propertyName))
        {
            return true;
        }

        var normalized = NormalizeJsonPropertyName(propertyName);
        if (normalized.Length < 4)
        {
            return false;
        }

        foreach (var marker in CredentialPropertyMarkers)
        {
            var maximumPrefixLength = Math.Min(marker.Length - 1, normalized.Length);
            for (var prefixLength = maximumPrefixLength; prefixLength >= 4; prefixLength--)
            {
                if (normalized.EndsWith(marker[..prefixLength], StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static string NormalizeJsonPropertyName(string propertyName) => string.Concat(
        propertyName.Where(char.IsLetterOrDigit)).ToLowerInvariant();

    private static bool ContainsCredentialText(string value) =>
        value.Contains("secret-content-sentinel", StringComparison.Ordinal) ||
        PrivateKeyEnvelopePattern().IsMatch(value) ||
        CredentialUriPattern().IsMatch(value) ||
        SecretAssignmentPattern().IsMatch(value) ||
        CredentialHeaderPattern().IsMatch(value);

    [GeneratedRegex(@"-{5}BEGIN[ \t]+(?:[A-Z0-9][A-Z0-9 -]{0,63}[ \t]+)?PRIVATE[ \t]+KEY(?:[ \t]+BLOCK)?-{5}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex PrivateKeyEnvelopePattern();

    [GeneratedRegex(@"\b[a-z][a-z0-9+.-]{0,31}://[^\s/?#:@]+:[^\s/?#@]+@", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex CredentialUriPattern();

    [GeneratedRegex(@"\b(?:password|pwd|access[_-]?token|api[_-]?key|client[_-]?secret|connection\s*string)\s*[:=]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex SecretAssignmentPattern();

    [GeneratedRegex(@"\b(?:authorization|cookie|set-cookie)\s*:", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex CredentialHeaderPattern();

    private readonly record struct JsonCandidateScanResult(
        int ScannedEndExclusive,
        int? EndIndex,
        bool IsEscaped,
        bool BoundExceeded,
        bool IsMalformed)
    {
        public int FollowingTailStartIndex => EndIndex is int end ? end + 1 : ScannedEndExclusive;
    }

}
