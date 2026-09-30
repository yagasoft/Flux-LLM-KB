using FluxKnowledge.Application.Contracts;
using FluxKnowledge.Application.Sources;
using FluxKnowledge.Domain.Sources;

namespace FluxKnowledge.Application.Ports;

public sealed record SourceDiscoveredFile(
    string CanonicalPath,
    string RelativePath,
    string StableSourceIdentity,
    byte[] ClassificationBuffer,
    bool HasFullBoundedBuffer,
    string ContentSha256,
    long ByteLength,
    DateTimeOffset LastWriteAtUtc,
    SourceClassificationResult Classification,
    GitInventoryEvidence? GitInventory = null,
    SourceScanOwnership? ScanOwnership = null);

public sealed record SourceScanOwnership(SourceScanRequestId RequestId, SourceScanLease Lease);

public sealed record SourceEnumerationEvidence(string Kind, string RelativePath, string Detail);

public sealed record GitInventoryEvidence(string RepositoryIdentity, string Generation, int TrackedCount, int ExcludedCount);

public interface IAuthoritativeSourceFileEnumerator : ISourceFileEnumerator
{
    GitInventoryEvidence? LastInventory { get; }
    ValueTask<bool> ValidateInventoryAsync(SourceRootConfiguration root, CancellationToken cancellationToken);
}

public interface ISourceFileEnumerator
{
    IReadOnlyList<SourceEnumerationEvidence> LastEvidence { get; }

    IAsyncEnumerable<SourceDiscoveredFile> EnumerateAsync(
        SourceRootConfiguration sourceRoot,
        CancellationToken cancellationToken);
}
