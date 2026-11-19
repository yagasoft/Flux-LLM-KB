using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluxKnowledge.Application.IntegrationV1;
using Microsoft.AspNetCore.DataProtection;

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence;

/// <summary>Authenticated, opaque native-v1 keyset continuations bound to one canonical query.</summary>
public sealed class NativeV1ProjectionCursorCodec : INativeV1CursorCodec
{
    private const string ProtectorPurpose = "FluxKnowledge.NativeV1ProjectionCursor/v1";
    private const int CurrentVersion = 2;
    private const int MaximumTokenCharacters = 65536;
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private readonly IDataProtector? _protector;

    public NativeV1ProjectionCursorCodec(IDataProtectionProvider dataProtectionProvider)
    {
        ArgumentNullException.ThrowIfNull(dataProtectionProvider);
        try
        {
            _protector = dataProtectionProvider.CreateProtector(ProtectorPurpose);
        }
        catch (Exception exception) when (IsProtectionFailure(exception))
        {
            _protector = null;
        }
    }

    public string Encode(NativeV1CursorBinding binding, NativeV1CursorPosition position)
    {
        ValidateBinding(binding);
        ValidatePosition(binding, position);
        if (_protector is null) throw InvalidCursor();
        try
        {
            var payload = new CursorPayload(
                CurrentVersion,
                binding,
                position);
            return Base64UrlEncode(_protector.Protect(JsonSerializer.SerializeToUtf8Bytes(payload, SerializerOptions)));
        }
        catch (NativeOperationException)
        {
            throw;
        }
        catch (Exception exception) when (IsTokenFailure(exception))
        {
            throw InvalidCursor();
        }
    }

    public NativeV1CursorPosition Decode(NativeV1CursorBinding binding, string cursor)
    {
        ValidateBinding(binding);
        try
        {
            if (_protector is null || string.IsNullOrWhiteSpace(cursor) || cursor.Length > MaximumTokenCharacters)
            {
                throw InvalidCursor();
            }

            var bytes = _protector.Unprotect(Base64UrlDecode(cursor));
            var payload = JsonSerializer.Deserialize<CursorPayload>(bytes, SerializerOptions);
            if (payload?.Version == 1)
            {
                var legacy = JsonSerializer.Deserialize<LegacyCursorPayload>(bytes, SerializerOptions);
                if (legacy is null || legacy.PageLimit != binding.PageLimit ||
                    !string.Equals(legacy.BindingFingerprint, LegacyFingerprint(binding), StringComparison.Ordinal)) throw InvalidCursor();
                ValidatePosition(binding, legacy.Position);
                return legacy.Position;
            }
            if (payload is null || payload.Version != CurrentVersion || payload.Binding != binding)
            {
                throw InvalidCursor();
            }
            ValidatePosition(binding, payload.Position);
            return payload.Position;
        }
        catch (NativeOperationException)
        {
            throw;
        }
        catch (Exception exception) when (IsTokenFailure(exception))
        {
            throw InvalidCursor();
        }
    }

    private static void ValidateBinding(NativeV1CursorBinding binding)
    {
        ArgumentNullException.ThrowIfNull(binding);
        if (string.IsNullOrWhiteSpace(binding.Family) || string.IsNullOrWhiteSpace(binding.View) ||
            string.IsNullOrWhiteSpace(binding.Ordering) || binding.PageLimit is < 1 or > 100 ||
            binding.CanonicalFilters is null || binding.CanonicalFilters.Length > 16384 ||
            binding.Family.Length > 128 || binding.View.Length > 128 || binding.Ordering.Length > 128)
        {
            throw InvalidCursor();
        }
    }

    private static void ValidatePosition(NativeV1CursorBinding binding, NativeV1CursorPosition position)
    {
        if (position is null || position.Id == Guid.Empty || position.Ordinal is < 0 ||
            position.Text is { Length: > 256 } || position.SecondaryText is { Length: > 256 } ||
            position.TertiaryText is { Length: > 256 } ||
            position is { Id: null, Timestamp: null, Ordinal: null, Text: null, Sequence: null })
        {
            throw InvalidCursor();
        }

        var validShape = binding.Ordering switch
        {
            "id:asc" => position is { Id: not null, Timestamp: null, Ordinal: null, Text: null, SecondaryText: null, TertiaryText: null, Sequence: null },
            "document-id:asc,ordinal:asc" => position is { Id: not null, Timestamp: null, Ordinal: not null, Text: null, SecondaryText: null, TertiaryText: null, Sequence: null },
            "discovered-at:desc,id:desc" or "updated-at:desc,id:desc" =>
                position is { Id: not null, Timestamp: not null, Ordinal: null, Text: null, SecondaryText: null, TertiaryText: null, Sequence: null },
            "occurred-at:desc,id:desc" =>
                position is { Id: null, Timestamp: not null, Ordinal: null, Text: null, SecondaryText: null, TertiaryText: null, Sequence: not null },
            _ => false
        };
        if (!validShape) throw InvalidCursor();
    }

    private static NativeOperationException InvalidCursor() => new("cursor-invalid");

    // Read-only compatibility for already-issued tokens. New tokens compare
    // complete protected fields and never create these custom fingerprints.
    private static string LegacyFingerprint(NativeV1CursorBinding binding)
    {
        var filters = binding.CanonicalFilters;
        if (binding.Family == "code")
        {
            using var parsed = JsonDocument.Parse(filters);
            if (!parsed.RootElement.TryGetProperty("query", out var query) ||
                !parsed.RootElement.TryGetProperty("branchId", out var branch)) throw InvalidCursor();
            filters = JsonSerializer.Serialize(new
            {
                queryFingerprint = query.ValueKind == JsonValueKind.Null ? null : LegacyHash(query.GetString()!),
                branchId = branch.GetString()
            });
        }
        return LegacyHash(string.Join("\n", binding.Family, binding.View, filters, binding.Ordering,
            binding.PageLimit.ToString(System.Globalization.CultureInfo.InvariantCulture)));
    }

    private static string LegacyHash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private sealed record LegacyCursorPayload(int Version, string BindingFingerprint, int PageLimit, NativeV1CursorPosition Position);

    private static string Base64UrlEncode(byte[] value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] Base64UrlDecode(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        padded += (padded.Length % 4) switch
        {
            0 => string.Empty,
            2 => "==",
            3 => "=",
            _ => throw new FormatException("Invalid base64url length.")
        };
        var decoded = Convert.FromBase64String(padded);
        if (!string.Equals(Base64UrlEncode(decoded), value, StringComparison.Ordinal))
        {
            throw new FormatException("Non-canonical base64url encoding.");
        }
        return decoded;
    }

    private static bool IsProtectionFailure(Exception exception) =>
        exception is CryptographicException or IOException or InvalidOperationException or
            PlatformNotSupportedException or SecurityException or UnauthorizedAccessException;

    private static bool IsTokenFailure(Exception exception) =>
        IsProtectionFailure(exception) ||
        exception is FormatException or JsonException or ArgumentException or OverflowException;

    private sealed record CursorPayload(
        int Version,
        NativeV1CursorBinding Binding,
        NativeV1CursorPosition Position);
}
