using FluxKnowledge.Domain.Common;

namespace FluxKnowledge.Domain.Sources;

public sealed record SourceArtifactId(Guid Value)
{
    public static SourceArtifactId New() => new(Guid.NewGuid());
}

public sealed record SourceArtifact
{
    public SourceArtifactId Id { get; private init; }

    public SourceRevisionId SourceRevisionId { get; private init; }

    public string ContentSha256 { get; private init; }

    public string StoreRelativePath { get; private init; }

    public long ByteLength { get; private init; }

    public DateTimeOffset ChecksumVerifiedAtUtc { get; private init; }

    public static SourceArtifact Create(
        SourceRevisionId sourceRevisionId,
        string contentSha256,
        string storeRelativePath,
        long byteLength,
        DateTimeOffset checksumVerifiedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(sourceRevisionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(storeRelativePath);
        SourceRevision.EnsureSha256(contentSha256);
        if (byteLength < 0)
        {
            throw new DomainInvariantException("Source artifact byte length cannot be negative.");
        }

        return new SourceArtifact(
            SourceArtifactId.New(), sourceRevisionId, contentSha256, storeRelativePath, byteLength, checksumVerifiedAtUtc);
    }

    private SourceArtifact(
        SourceArtifactId id,
        SourceRevisionId sourceRevisionId,
        string contentSha256,
        string storeRelativePath,
        long byteLength,
        DateTimeOffset checksumVerifiedAtUtc)
    {
        Id = id;
        SourceRevisionId = sourceRevisionId;
        ContentSha256 = contentSha256;
        StoreRelativePath = storeRelativePath;
        ByteLength = byteLength;
        ChecksumVerifiedAtUtc = checksumVerifiedAtUtc;
    }
}
