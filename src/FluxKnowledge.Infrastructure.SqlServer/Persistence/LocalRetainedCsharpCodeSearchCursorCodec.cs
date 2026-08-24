using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluxKnowledge.Application.Sources;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence;

internal sealed record LocalRetainedCsharpCodeSearchCursorPosition(
    Guid BranchId,
    LocalRetainedCsharpCodeSearchFactKind FactKind,
    int Ordinal);

/// <summary>
/// Produces authenticated, versioned continuations bound to a canonical query and one
/// immutable durable fact. All token and key failures collapse to the fixed safe cursor error.
/// </summary>
public sealed class LocalRetainedCsharpCodeSearchCursorCodec
{
    private const string ProtectorPurpose = "FluxKnowledge.LocalRetainedCsharpCodeSearchCursor/v1";
    private const int CurrentVersion = 1;
    private const int MaximumTokenCharacters = 1024;
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private readonly IDataProtector? _protector;

    public LocalRetainedCsharpCodeSearchCursorCodec(IDataProtectionProvider dataProtectionProvider)
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

    internal async ValueTask<LocalRetainedCsharpCodeSearchCursor> CreateAsync(
        FluxKnowledgeDbContext context,
        string canonicalQuery,
        Guid branchId,
        LocalRetainedCsharpCodeSearchFactKind factKind,
        int ordinal,
        CancellationToken cancellationToken)
    {
        var factFingerprint = await ReadFactFingerprintAsync(
                context,
                branchId,
                factKind,
                ordinal,
                canonicalQuery,
                cancellationToken)
            .ConfigureAwait(false);
        if (factFingerprint is null || _protector is null)
        {
            throw new LocalRetainedCsharpCodeSearchCursorException();
        }

        var payload = new CursorPayload(
            CurrentVersion,
            FingerprintQuery(canonicalQuery),
            branchId,
            (int)factKind,
            ordinal,
            factFingerprint);
        try
        {
            var payloadBytes = JsonSerializer.SerializeToUtf8Bytes(payload, SerializerOptions);
            return new LocalRetainedCsharpCodeSearchCursor(Base64UrlEncode(_protector.Protect(payloadBytes)));
        }
        catch (Exception exception) when (IsTokenFailure(exception))
        {
            throw new LocalRetainedCsharpCodeSearchCursorException();
        }
    }

    internal async ValueTask<LocalRetainedCsharpCodeSearchCursorPosition> ValidateAsync(
        FluxKnowledgeDbContext context,
        string canonicalQuery,
        LocalRetainedCsharpCodeSearchCursor cursor,
        CancellationToken cancellationToken)
    {
        var payload = Decode(cursor, canonicalQuery);
        var factKind = (LocalRetainedCsharpCodeSearchFactKind)payload.FactKind;
        var persistedFingerprint = await ReadFactFingerprintAsync(
                context,
                payload.BranchId,
                factKind,
                payload.Ordinal,
                canonicalQuery,
                cancellationToken)
            .ConfigureAwait(false);
        if (!string.Equals(persistedFingerprint, payload.FactFingerprint, StringComparison.Ordinal))
        {
            throw new LocalRetainedCsharpCodeSearchCursorException();
        }

        return new LocalRetainedCsharpCodeSearchCursorPosition(
            payload.BranchId,
            factKind,
            payload.Ordinal);
    }

    internal static string CanonicaliseQuery(string query) =>
        query.Normalize(NormalizationForm.FormC);

    private CursorPayload Decode(LocalRetainedCsharpCodeSearchCursor cursor, string canonicalQuery)
    {
        try
        {
            if (_protector is null || string.IsNullOrWhiteSpace(cursor.Token) ||
                cursor.Token.Length > MaximumTokenCharacters)
            {
                throw new LocalRetainedCsharpCodeSearchCursorException();
            }

            var payloadBytes = _protector.Unprotect(Base64UrlDecode(cursor.Token));
            var payload = JsonSerializer.Deserialize<CursorPayload>(payloadBytes, SerializerOptions);
            if (payload is null || payload.Version != CurrentVersion || payload.BranchId == Guid.Empty ||
                payload.Ordinal < 0 || !IsSha256(payload.QueryFingerprint) ||
                !IsSha256(payload.FactFingerprint) ||
                payload.FactKind is not (int)LocalRetainedCsharpCodeSearchFactKind.Symbol and
                    not (int)LocalRetainedCsharpCodeSearchFactKind.Reference ||
                !string.Equals(payload.QueryFingerprint, FingerprintQuery(canonicalQuery), StringComparison.Ordinal))
            {
                throw new LocalRetainedCsharpCodeSearchCursorException();
            }

            return payload;
        }
        catch (LocalRetainedCsharpCodeSearchCursorException)
        {
            throw;
        }
        catch (Exception exception) when (IsTokenFailure(exception))
        {
            throw new LocalRetainedCsharpCodeSearchCursorException();
        }
    }

    private static async ValueTask<string?> ReadFactFingerprintAsync(
        FluxKnowledgeDbContext context,
        Guid branchId,
        LocalRetainedCsharpCodeSearchFactKind factKind,
        int ordinal,
        string canonicalQuery,
        CancellationToken cancellationToken)
    {
        if (branchId == Guid.Empty || ordinal < 0)
        {
            return null;
        }

        return factKind switch
        {
            LocalRetainedCsharpCodeSearchFactKind.Symbol => await context.SourceProcessorCodeSymbols.AsNoTracking()
                .Where(value => value.DocumentId == branchId && value.Ordinal == ordinal)
                .Where(value => value.LocalName.Contains(canonicalQuery) ||
                                value.QualifiedName.Contains(canonicalQuery) ||
                                value.RenderedSignature.Contains(canonicalQuery))
                .Select(value => value.SymbolFingerprint)
                .SingleOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false),
            LocalRetainedCsharpCodeSearchFactKind.Reference => await context.SourceProcessorCodeReferences.AsNoTracking()
                .Where(value => value.DocumentId == branchId && value.Ordinal == ordinal)
                .Where(value => value.TargetDisplay.Contains(canonicalQuery))
                .Select(value => value.ReferenceFingerprint)
                .SingleOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false),
            _ => null
        };
    }

    private static string FingerprintQuery(string canonicalQuery) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalQuery)));

    private static bool IsSha256(string? value) =>
        value is { Length: 64 } && value.All(Uri.IsHexDigit);

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
        string QueryFingerprint,
        Guid BranchId,
        int FactKind,
        int Ordinal,
        string FactFingerprint);
}
