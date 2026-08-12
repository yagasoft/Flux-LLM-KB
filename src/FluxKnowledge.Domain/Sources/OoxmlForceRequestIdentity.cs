using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace FluxKnowledge.Domain.Sources;

/// <summary>Canonical opaque identities for one immutable blocked OOXML branch version.</summary>
public static class OoxmlForceRequestIdentity
{
    public const string ForceProcessRoute = "/api/operator-actions/{actionId}/force-process";

    public static string CreateActionId(
        Guid branchId,
        Guid descriptorId,
        string descriptorFingerprint,
        ReadOnlySpan<byte> blockedRowVersion)
    {
        if (branchId == Guid.Empty || descriptorId == Guid.Empty)
        {
            throw new ArgumentException("A force action requires non-empty branch and descriptor identities.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(descriptorFingerprint);
        EnsureRowVersion(blockedRowVersion);
        return HashTuple(
            "ooxml-force-action:v1",
            branchId.ToString("D"),
            descriptorId.ToString("D"),
            descriptorFingerprint,
            Convert.ToHexStringLower(blockedRowVersion));
    }

    public static string CreateRequestFingerprint(string actionId, string expectedBlockedRowVersion) =>
        HashTuple("ooxml-force-request:v1", ForceProcessRoute, RequireSha256(actionId, nameof(actionId)),
            EncodeBlockedRowVersion(DecodeBlockedRowVersion(expectedBlockedRowVersion)));

    public static string CreateTerminalReceiptFingerprint(Guid requestId, string terminalReasonCode)
    {
        if (requestId == Guid.Empty) throw new ArgumentException("A terminal receipt requires a request identity.", nameof(requestId));
        ArgumentException.ThrowIfNullOrWhiteSpace(terminalReasonCode);
        return HashTuple("ooxml-force-receipt:v1", requestId.ToString("D"), terminalReasonCode);
    }

    public static string EncodeBlockedRowVersion(ReadOnlySpan<byte> rowVersion)
    {
        EnsureRowVersion(rowVersion);
        return Convert.ToBase64String(rowVersion);
    }

    public static byte[] DecodeBlockedRowVersion(string token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        try
        {
            var rowVersion = Convert.FromBase64String(token);
            EnsureRowVersion(rowVersion);
            return rowVersion;
        }
        catch (FormatException exception)
        {
            throw new ArgumentException("The blocked row-version token is invalid.", nameof(token), exception);
        }
    }

    public static string RequireSha256(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (value.Length != 64 || value.Any(character => !((character >= '0' && character <= '9') || (character >= 'a' && character <= 'f'))))
        {
            throw new ArgumentException("The value must be a lower-case SHA-256 hex value.", parameterName);
        }

        return value;
    }

    private static string HashTuple(params string[] values)
    {
        using var stream = new MemoryStream();
        Span<byte> length = stackalloc byte[sizeof(int)];
        foreach (var value in values)
        {
            ArgumentNullException.ThrowIfNull(value);
            var bytes = Encoding.UTF8.GetBytes(value);
            BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
            stream.Write(length);
            stream.Write(bytes);
        }

        return Convert.ToHexStringLower(SHA256.HashData(stream.GetBuffer().AsSpan(0, checked((int)stream.Length))));
    }

    private static void EnsureRowVersion(ReadOnlySpan<byte> rowVersion)
    {
        if (rowVersion.Length != sizeof(long))
        {
            throw new ArgumentException("A SQL Server row-version token must be exactly eight bytes.", nameof(rowVersion));
        }
    }
}
