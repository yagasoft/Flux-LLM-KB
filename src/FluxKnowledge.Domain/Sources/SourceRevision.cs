using FluxKnowledge.Domain.Common;

namespace FluxKnowledge.Domain.Sources;

public sealed record SourceRevisionId(Guid Value)
{
    public static SourceRevisionId New() => new(Guid.NewGuid());
}

public sealed record SourceRevision
{
    public SourceRevisionId Id { get; private init; }

    public SourceRootId SourceRootId { get; private init; }

    public string StableSourceIdentity { get; private init; }

    public long Revision { get; private init; }

    public string ContentSha256 { get; private init; }

    public string CanonicalPath { get; private init; }

    public SourceRevisionId? ParentRevisionId { get; private init; }

    public string Classification { get; private init; }

    public long ByteLength { get; private init; }

    public DateTimeOffset DiscoveredAtUtc { get; private init; }

    public static SourceRevision Create(
        SourceRootId sourceRootId,
        string stableSourceIdentity,
        long revision,
        string contentSha256,
        string canonicalPath,
        SourceRevisionId? parentRevisionId,
        string classification,
        long byteLength)
    {
        ArgumentNullException.ThrowIfNull(sourceRootId);
        ArgumentException.ThrowIfNullOrWhiteSpace(stableSourceIdentity);
        EnsureCanonicalPath(canonicalPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(classification);
        if (revision <= 0 || byteLength < 0)
        {
            throw new DomainInvariantException("Source revision numeric values are invalid.");
        }

        EnsureSha256(contentSha256);
        return new SourceRevision(
            SourceRevisionId.New(), sourceRootId, stableSourceIdentity, revision, contentSha256,
            canonicalPath, parentRevisionId, classification, byteLength, DateTimeOffset.UtcNow);
    }

    internal static void EnsureSha256(string sha256)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sha256);
        if (sha256.Length != 64 || sha256.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new DomainInvariantException("SHA-256 values must contain exactly 64 hexadecimal characters.");
        }
    }

    private static void EnsureCanonicalPath(string canonicalPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(canonicalPath);
        if (!Path.IsPathFullyQualified(canonicalPath) ||
            !string.Equals(Path.GetFullPath(canonicalPath), canonicalPath, StringComparison.Ordinal))
        {
            throw new DomainInvariantException("Source revision path must be a canonical absolute path.");
        }
    }

    private SourceRevision(
        SourceRevisionId id,
        SourceRootId sourceRootId,
        string stableSourceIdentity,
        long revision,
        string contentSha256,
        string canonicalPath,
        SourceRevisionId? parentRevisionId,
        string classification,
        long byteLength,
        DateTimeOffset discoveredAtUtc)
    {
        Id = id;
        SourceRootId = sourceRootId;
        StableSourceIdentity = stableSourceIdentity;
        Revision = revision;
        ContentSha256 = contentSha256;
        CanonicalPath = canonicalPath;
        ParentRevisionId = parentRevisionId;
        Classification = classification;
        ByteLength = byteLength;
        DiscoveredAtUtc = discoveredAtUtc;
    }
}
