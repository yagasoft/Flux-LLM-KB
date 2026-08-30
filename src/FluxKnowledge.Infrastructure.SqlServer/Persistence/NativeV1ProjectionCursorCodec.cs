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
    private const int CurrentVersion = 1;
    private const int MaximumTokenCharacters = 2048;
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
                FingerprintBinding(binding),
                binding.PageLimit,
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

            var payload = JsonSerializer.Deserialize<CursorPayload>(
                _protector.Unprotect(Base64UrlDecode(cursor)),
                SerializerOptions);
            if (payload is null || payload.Version != CurrentVersion || payload.PageLimit != binding.PageLimit ||
                !IsSha256(payload.BindingFingerprint) ||
                !string.Equals(payload.BindingFingerprint, FingerprintBinding(binding), StringComparison.Ordinal))
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
            binding.CanonicalFilters.Length > 4096)
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

    private static string FingerprintBinding(NativeV1CursorBinding binding)
    {
        var canonical = string.Join("\n", binding.Family, binding.View, binding.CanonicalFilters, binding.Ordering,
            binding.PageLimit.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    private static NativeOperationException InvalidCursor() => new("cursor-invalid");

    private static bool IsSha256(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);

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
        string BindingFingerprint,
        int PageLimit,
        NativeV1CursorPosition Position);
}
