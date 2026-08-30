using System.Text.Json;
using System.Text.RegularExpressions;

namespace FluxKnowledge.Application.IntegrationV1;

/// <summary>Fail-closed reader for the public native v1 JSON envelope.</summary>
public static partial class NativeV1EnvelopeProtector
{
    private const int MaximumReasonCharacters = 128;
    private const int MaximumMessageCharacters = 512;

    public static bool TryRead(string source, out string protectedEnvelope)
    {
        protectedEnvelope = string.Empty;
        if (string.IsNullOrWhiteSpace(source) || System.Text.Encoding.UTF8.GetByteCount(source) > NativeV1ContractLimits.MaximumResponseBytes) return false;
        try
        {
            using var document = JsonDocument.Parse(source, new JsonDocumentOptions { MaxDepth = 32 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != 5 ||
                !root.TryGetProperty("ok", out var ok) || ok.ValueKind is not (JsonValueKind.True or JsonValueKind.False) ||
                !root.TryGetProperty("result", out var result) ||
                !root.TryGetProperty("reasonCode", out var reasonCode) || !NullableBoundedString(reasonCode, MaximumReasonCharacters) ||
                !root.TryGetProperty("message", out var message) || !NullableBoundedString(message, MaximumMessageCharacters) ||
                !root.TryGetProperty("retryable", out var retryable) || retryable.ValueKind is not (JsonValueKind.True or JsonValueKind.False) ||
                ContainsProtectedContent(result) || ContainsProtectedContent(reasonCode) || ContainsProtectedContent(message)) return false;

            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
            {
                writer.WriteStartObject();
                writer.WriteBoolean("ok", ok.GetBoolean());
                writer.WritePropertyName("result");
                result.WriteTo(writer);
                writer.WritePropertyName("reasonCode");
                reasonCode.WriteTo(writer);
                writer.WritePropertyName("message");
                message.WriteTo(writer);
                writer.WriteBoolean("retryable", retryable.GetBoolean());
                writer.WriteEndObject();
            }
            protectedEnvelope = System.Text.Encoding.UTF8.GetString(stream.ToArray());
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool NullableBoundedString(JsonElement value, int maximum) => value.ValueKind == JsonValueKind.Null ||
        value.ValueKind == JsonValueKind.String && value.GetString() is { } text && text.Length <= maximum;

    private static bool ContainsProtectedContent(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Object => value.EnumerateObject().Any(property => IsProtectedName(property.Name) || ContainsProtectedContent(property.Value)),
        JsonValueKind.Array => value.EnumerateArray().Any(ContainsProtectedContent),
        JsonValueKind.String => ContainsCredentialText(value.GetString() ?? string.Empty),
        _ => false
    };

    private static bool IsProtectedName(string name)
    {
        var normalized = string.Concat(name.Where(char.IsLetterOrDigit)).ToLowerInvariant();
        return normalized is "sourceoriginalpath" or "decodedcursor" or "confirmationcontent" or "confirmationsecret" or
            "password" or "pwd" or "passphrase" or "accesstoken" or "refreshtoken" or "idtoken" or "token" or "apikey" or
            "clientsecret" or "connectionstring" or "authorization" or "cookie" or "setcookie" or "privatekey" ||
            normalized.Contains("secret", StringComparison.Ordinal) ||
            normalized.Contains("accesstoken", StringComparison.Ordinal) || normalized.Contains("refreshtoken", StringComparison.Ordinal) || normalized.Contains("clientsecret", StringComparison.Ordinal);
    }

    private static bool ContainsCredentialText(string value, int encodedDepth = 0) => value.Contains("secret-content-sentinel", StringComparison.Ordinal) ||
        PrivateKeyEnvelopePattern().IsMatch(value) || CredentialUriPattern().IsMatch(value) || SecretAssignmentPattern().IsMatch(value) || CredentialHeaderPattern().IsMatch(value) ||
        BareBearerPattern().IsMatch(value) || ContainsEmbeddedJsonCredential(value) || ContainsEncodedCredential(value, encodedDepth);

    private static bool ContainsEncodedCredential(string value, int encodedDepth)
    {
        if (value.Length > NativeV1ContractLimits.MaximumResponseBytes) return true;

        var normalised = string.Concat(value.Where(character => !char.IsWhiteSpace(character)));
        if (normalised.Length < 12 || !normalised.All(character => char.IsLetterOrDigit(character) || character is '+' or '/' or '-' or '_' or '=')) return false;

        normalised = normalised.Replace('-', '+').Replace('_', '/');
        normalised = normalised.PadRight(normalised.Length + (4 - normalised.Length % 4) % 4, '=');
        try
        {
            var decoded = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(normalised));
            if (encodedDepth >= 2) return true;
            return ContainsCredentialText(decoded, encodedDepth + 1);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static bool ContainsEmbeddedJsonCredential(string value)
    {
        if (value.Length > NativeV1ContractLimits.MaximumResponseBytes) return true;
        try
        {
            using var document = JsonDocument.Parse(value, new JsonDocumentOptions { MaxDepth = 16 });
            return ContainsProtectedContent(document.RootElement);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    [GeneratedRegex(@"-{5}BEGIN[ \t]+(?:[A-Z0-9][A-Z0-9 -]{0,63}[ \t]+)?PRIVATE[ \t]+KEY(?:[ \t]+BLOCK)?-{5}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex PrivateKeyEnvelopePattern();

    [GeneratedRegex(@"\b[a-z][a-z0-9+.-]{0,31}://[^\s/?#:@]+:[^\s/?#@]+@", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex CredentialUriPattern();

    [GeneratedRegex(@"\b(?:password|pwd|access[_-]?token|api[_-]?key|client[_-]?secret|connection\s*string)\s*[:=]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex SecretAssignmentPattern();

    [GeneratedRegex(@"\b(?:authorization|cookie|set-cookie)\s*:", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex CredentialHeaderPattern();

    [GeneratedRegex(@"\bbearer\s+[A-Za-z0-9._~+/-]+={0,2}\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex BareBearerPattern();
}
