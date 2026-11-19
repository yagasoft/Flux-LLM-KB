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
    private const string IntentFileName = ".retirement-intent.json";

    internal Action? BeforeUnlink { get; init; }

    // Metadata/identity checks do not read file bodies. Object-count and elapsed
    // slice limits still bound retirement work.
    public long MaximumSegmentVerificationBytes(IndexSegmentDescriptor segment) => 0;
    public long MaximumManifestVerificationBytes(IndexGenerationDescriptor generation) => 0;

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
            if (_files.EnumerateLiteralChildren(directory).Any(value => value != IntentFileName && !proofs.Any(proof => proof.Name == value)))
                throw new UnauthorizedAccessException("index-retirement-unrecognised-child");
            // Snapshot every existing child's physical identity before the first
            // unlink. Final handles must match; an absent child grants no authority.
            var identities = new NativeFileIdentity?[proofs.Length];
            for (var index = 0; index < proofs.Length; index++)
            {
                var proof = proofs[index];
                identities[index] = await _files.VerifyOrDeleteIndexFileAsync(directory, proof.Name, proof.Checksum,
                    proof.Length, proof.MaximumLength, false, cancellationToken);
            }
            var proposed = new RetirementIntent(1, area + "/" + name, directory.Identity,
                proofs.Select((proof, index) => new IntentChild(proof.Name, identities[index])).ToArray());
            using (var intentLease = await _files.OpenIndexRetirementIntentAsync(directory, IntentFileName,
                JsonSerializer.SerializeToUtf8Bytes(proposed), cancellationToken))
            {
                RetirementIntent? intent;
                try { intent = JsonSerializer.Deserialize<RetirementIntent>(intentLease.Content); }
                catch (JsonException exception) { throw new InvalidDataException("index-retirement-intent-invalid", exception); }
                if (intent is null || intent.Version != 1 || intent.ObjectName != proposed.ObjectName ||
                    intent.DirectoryIdentity != directory.Identity || intent.Children is null || intent.Children.Length != proofs.Length ||
                    intent.Children.Where((entry, index) => entry is null || entry.Name != proofs[index].Name).Any())
                    throw new InvalidDataException("index-retirement-intent-invalid");
                BeforeUnlink?.Invoke();
                for (var index = 0; index < proofs.Length; index++)
                {
                    var proof = proofs[index];
                    await _files.VerifyOrDeleteIndexFileAsync(directory, proof.Name, proof.Checksum, proof.Length,
                        proof.MaximumLength, true, cancellationToken, intent.Children[index].Identity);
                }
                if (_files.EnumerateLiteralChildren(directory).Any(value => value != IntentFileName))
                    throw new InvalidDataException("index-retirement-unrecognised-child");
                intentLease.Complete();
            }
        }
        var removed = await _files.DeleteLiteralChildAsync(parent, name, child.Identity, cancellationToken);
        if (!removed.Changed) throw new IOException(removed.Reason ?? "index-retirement-directory-delete-failed");
    }

    private sealed record FileProof(string Name, string Checksum, long? Length, long MaximumLength);
    private sealed record IntentChild(string Name, NativeFileIdentity? Identity);
    private sealed record RetirementIntent(int Version, string ObjectName, NativeFileIdentity DirectoryIdentity, IntentChild[] Children);
}
