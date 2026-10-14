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
    SourceScanOwnership? ScanOwnership = null,
    SourcePathBinding? SourcePath = null);

/// <summary>Admission identity carried in memory from discovery to retention.</summary>
public sealed record SourcePathBinding(string RootPath, string RootIdentityFingerprint);

public sealed record SourcePreviewLimits(int MaximumEntries, int MaximumFiles, long MaximumReadBytes, TimeSpan MaximumDuration)
{
    public static SourcePreviewLimits Default { get; } = new(256, 16, 16L * 1024 * 1024, TimeSpan.FromSeconds(5));
}

public interface IBoundedSourceFileEnumerator : ISourceFileEnumerator
{
    IAsyncEnumerable<SourceDiscoveredFile> PreviewAsync(SourceRootConfiguration sourceRoot, SourcePreviewLimits limits, CancellationToken cancellationToken);
}

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
