using System.Security.Cryptography;
using System.Text.Json;
using FluxKnowledge.Application.Indexing;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Integrations.Windows.NativeGoLive;

namespace FluxKnowledge.Integrations.Files;

/// <summary>Exact sealed index objects only; SQL ownership and retirement are checked by the caller.</summary>
public sealed class WindowsIndexProjectionFileStore(string indexRoot) : IIndexProjectionFileStore
{
    private readonly string _root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(indexRoot));
    private readonly HandleRelativeNativeFileSystem _files = new();
    private const long MaximumManifestLength = 16 * 1024 * 1024;

    internal Action? BeforeUnlink { get; init; }

    // Every existing constituent is read twice: once before any unlink, then
    // again on the exact deletion handle. Reserve the full cost even on failure.
    public long MaximumSegmentVerificationBytes(IndexSegmentDescriptor segment) =>
        checked(2 * (segment.FileLength + SegmentMetadata(segment).LongLength));
    public long MaximumManifestVerificationBytes(IndexGenerationDescriptor generation) => 2 * MaximumManifestLength;

    public ValueTask DeleteSegmentAsync(IndexSegmentDescriptor segment, CancellationToken cancellationToken)
    {
        if (segment.Id.Length != 64 || segment.Id.Any(value => !char.IsAsciiHexDigitLower(value)) || segment.FormatVersion != 2 ||
            segment.VectorCount <= 0 || segment.FileLength <= 0 ||
            segment.Id != IndexSegmentDescriptor.ComputeId(segment.NativeFormat, segment.MembershipChecksum, segment.PhysicalIncarnation))
            throw new UnauthorizedAccessException("index-retirement-segment-identity-invalid");
        var metadata = SegmentMetadata(segment);
        return DeleteObjectAsync("segments", segment.Id, segment.DirectoryPath,
            [new("index.usearch", segment.FileChecksum, segment.FileLength, segment.FileLength),
             new("metadata.json", Convert.ToHexStringLower(SHA256.HashData(metadata)), metadata.LongLength, metadata.LongLength)], cancellationToken);
    }

    public ValueTask DeleteManifestAsync(IndexGenerationDescriptor generation, CancellationToken cancellationToken)
    {
        if (generation.FormatVersion != 2 || generation.CorpusStamp is null || generation.Id == Guid.Empty ||
            generation.MetadataChecksum.Length != 64 || generation.Id != new Guid(Convert.FromHexString(generation.MetadataChecksum)[..16]))
            throw new UnauthorizedAccessException("index-retirement-manifest-identity-invalid");
        return DeleteObjectAsync("manifests", generation.Id.ToString("N"), generation.IndexPath,
            [new("manifest.json", generation.MetadataChecksum, null, MaximumManifestLength)], cancellationToken);
    }

    private static byte[] SegmentMetadata(IndexSegmentDescriptor segment) => JsonSerializer.SerializeToUtf8Bytes(new
    {
        GenerationId = new Guid(Convert.FromHexString(segment.Id)[..16]), segment.ModelFingerprint, Metric = "cos", segment.Dimensions,
        segment.VectorCount, Checksum = segment.MembershipChecksum, FormatVersion = 2, SegmentId = segment.Id, segment.NativeFormat,
        segment.FileChecksum, segment.FileLength, CorpusStamp = (CorpusPublicationStamp?)null
    });

    private async ValueTask DeleteObjectAsync(string area, string name, string expectedDirectory, FileProof[] proofs, CancellationToken cancellationToken)
    {
        var expected = Path.Combine(_root, area, name);
        if (_root.StartsWith(@"\\", StringComparison.Ordinal) || !string.Equals(expectedDirectory, expected, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("index-retirement-path-outside-layout");
        // Keep every ancestor in place through the last unlink, including the area directory.
        // Missing ancestors are a storage fault, never proof that this exact object was reclaimed.
        using var ancestors = new WindowsIndexSegmentReadLeaseFactory(_root).AcquireDirectory(Path.Combine(_root, area));
        using var root = _files.OpenReadOnlyDirectory(_root);
        using var parent = _files.OpenReadOnlyDirectory(root, area);
        NativeLiteralChild child;
        try { child = _files.InspectLiteralChild(parent, name); }
        catch (FileNotFoundException) { return; }
        catch (DirectoryNotFoundException) { return; }
        if (!child.IsDirectory) throw new UnauthorizedAccessException("index-retirement-not-directory");
        using (var directory = _files.OpenReadOnlyDirectory(parent, name))
        {
            if (directory.Identity != child.Identity) throw new UnauthorizedAccessException("index-retirement-directory-changed");
            if (_files.EnumerateLiteralChildren(directory).Any(value => !proofs.Any(proof => proof.Name == value)))
                throw new UnauthorizedAccessException("index-retirement-unrecognised-child");
            // Validate all existing bytes before the first unlink. Each actual unlink revalidates on its own held handle.
            foreach (var proof in proofs)
                await _files.VerifyOrDeleteIndexFileAsync(directory, proof.Name, proof.Checksum, proof.Length, proof.MaximumLength, false, cancellationToken);
            BeforeUnlink?.Invoke();
            foreach (var proof in proofs)
                await _files.VerifyOrDeleteIndexFileAsync(directory, proof.Name, proof.Checksum, proof.Length, proof.MaximumLength, true, cancellationToken);
        }
        var removed = await _files.DeleteLiteralChildAsync(parent, name, child.Identity, cancellationToken);
        if (!removed.Changed) throw new IOException(removed.Reason ?? "index-retirement-directory-delete-failed");
    }

    private sealed record FileProof(string Name, string Checksum, long? Length, long MaximumLength);
}
