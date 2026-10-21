using System.Security.Cryptography;
using System.Text.Json;
using FluxKnowledge.Application.Ports;

namespace FluxKnowledge.Infrastructure.Usearch;

/// <summary>A small flat manifest is the publication unit; it never contains vector payloads.</summary>
public sealed class UsearchManifestValidator(UsearchIndexOptions options, IIndexSegmentReadLeaseFactory paths)
{
    public IndexManifestDescriptor Place(CorpusPublicationStamp stamp, string profile, int dimensions,
        IReadOnlyList<FluxKnowledge.Application.Indexing.IndexSegmentDescriptor> segments)
    {
        var content = new ManifestFile(2, stamp, profile, dimensions, segments.OrderBy(value => value.Id, StringComparer.Ordinal).ToArray());
        var bytes = JsonSerializer.SerializeToUtf8Bytes(content);
        var checksum = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var id = new Guid(Convert.FromHexString(checksum)[..16]);
        var directory = Path.Combine(options.RootPath, "manifests", id.ToString("N"));
        var result = new IndexManifestDescriptor(new(id, profile, dimensions, directory, checksum,
            segments.Sum(value => value.VectorCount), stamp, 2, segments.Count), content.Segments);
        using var parent = paths.AcquireDirectory(Path.GetDirectoryName(directory)!, create: true);
        if (Directory.Exists(directory)) { Validate(result); return result; }
        var staging = Path.Combine(options.RootPath, "staging", "manifests", Guid.NewGuid().ToString("N"));
        try
        {
            using (paths.AcquireDirectory(staging, create: true))
            using (var file = new FileStream(Path.Combine(staging, "manifest.json"), FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { file.Write(bytes); file.Flush(true); }
            try { Directory.Move(staging, directory); }
            catch (IOException) when (Directory.Exists(directory)) { /* Exact concurrent placement is validated below. */ }
            Validate(result);
            return result;
        }
        finally
        {
            if (Directory.Exists(staging))
            {
                using (paths.AcquireDirectory(staging))
                    File.Delete(Path.Combine(staging, "manifest.json"));
                Directory.Delete(staging, false);
            }
        }
    }

    public void Validate(IndexManifestDescriptor manifest)
    {
        var generation = manifest.Generation;
        var expectedPath = Path.Combine(options.RootPath, "manifests", generation.Id.ToString("N"));
        if (generation.FormatVersion != 2 || generation.CorpusStamp is null || generation.Dimensions <= 0 ||
            !string.Equals(generation.IndexPath, expectedPath, StringComparison.OrdinalIgnoreCase) ||
            generation.SegmentCount != manifest.Segments.Count || generation.VectorCount != manifest.Segments.Sum(value => value.VectorCount) ||
            manifest.Segments.Select(value => value.Id).Distinct(StringComparer.Ordinal).Count() != manifest.Segments.Count ||
            manifest.Segments.Any(value => value.ModelFingerprint != generation.ModelFingerprint || value.Dimensions != generation.Dimensions || value.FormatVersion != 2))
            throw new IndexGenerationValidationException("manifest-descriptor-invalid");
        using var held = paths.AcquireManifest(generation.IndexPath);
        var expected = JsonSerializer.SerializeToUtf8Bytes(new ManifestFile(2, generation.CorpusStamp,
            generation.ModelFingerprint, generation.Dimensions, manifest.Segments));
        using var file = File.OpenRead(Path.Combine(generation.IndexPath, "manifest.json"));
        if (file.Length != expected.Length || generation.MetadataChecksum != Convert.ToHexStringLower(SHA256.HashData(expected)) ||
            new Guid(Convert.FromHexString(generation.MetadataChecksum)[..16]) != generation.Id ||
            generation.MetadataChecksum != Convert.ToHexStringLower(SHA256.HashData(file)))
            throw new IndexGenerationValidationException("manifest-file-proof-mismatch");
    }

    private sealed record ManifestFile(int FormatVersion, CorpusPublicationStamp CorpusStamp, string ModelFingerprint,
        int Dimensions, IReadOnlyList<FluxKnowledge.Application.Indexing.IndexSegmentDescriptor> Segments);
}
