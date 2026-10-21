using System.Security.Cryptography;
using System.Text.Json;
using Cloud.Unum.USearch;
using FluxKnowledge.Application.Indexing;
using FluxKnowledge.Application.Ports;

namespace FluxKnowledge.Infrastructure.Usearch;

/// <summary>Streams new/replacement vectors into bounded immutable native files.</summary>
public sealed class UsearchSegmentBuilder(
    UsearchIndexOptions options, IIndexSegmentReadLeaseFactory paths,
    IndexSegmentBuildOptions? buildOptions = null)
{
    private readonly IndexSegmentBuildOptions _budgets = buildOptions ?? new();
    internal Func<long>? AvailableBytesOverride { get; init; }
    public static string NativeFormat => $"usearch/{typeof(USearchIndex).Assembly.GetName().Version}/f32/cos/1";

    public async ValueTask<IReadOnlyList<IndexSegmentDescriptor>> BuildAsync(string modelFingerprint, int dimensions,
        IAsyncEnumerable<CanonicalVector> vectors, CancellationToken cancellationToken,
        Func<IndexSegmentDescriptor, IReadOnlyList<CanonicalVector>, CancellationToken, ValueTask>? seal = null,
        Guid incarnation = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(vectors);
        _budgets.Validate();
        if (dimensions <= 0 || dimensions > int.MaxValue / sizeof(float) || string.IsNullOrWhiteSpace(modelFingerprint) ||
            modelFingerprint.Length > 256 || modelFingerprint != modelFingerprint.Trim())
            throw new ArgumentException("segment-profile-invalid");
        var result = new List<IndexSegmentDescriptor>();
        var batch = new List<CanonicalVector>();
        long bytes = 0;
        long previous = 0;
        await foreach (var vector in vectors.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            ValidateInput(vector, modelFingerprint, dimensions, previous);
            previous = vector.VectorId;
            if (batch.Count > 0 && (batch.Count >= _budgets.MaximumVectors || bytes + vector.Values.Length > _budgets.MaximumPayloadBytes))
            {
                var segment = BuildOne(modelFingerprint, dimensions, batch, incarnation, cancellationToken);
                if (seal is not null) await seal(segment, batch, cancellationToken).ConfigureAwait(false);
                result.Add(segment);
                batch.Clear();
                bytes = 0;
            }
            // One vector is the minimum unit even when a configured byte budget is smaller.
            batch.Add(vector);
            bytes += vector.Values.Length;
        }
        if (batch.Count > 0)
        {
            var segment = BuildOne(modelFingerprint, dimensions, batch, incarnation, cancellationToken);
            if (seal is not null) await seal(segment, batch, cancellationToken).ConfigureAwait(false);
            result.Add(segment);
        }
        return result;
    }

    private IndexSegmentDescriptor BuildOne(string fingerprint, int dimensions, IReadOnlyList<CanonicalVector> vectors,
        Guid incarnation, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var membership = UsearchGenerationValidator.ComputeChecksum(fingerprint, dimensions, vectors);
        var id = IndexSegmentDescriptor.ComputeId(NativeFormat, membership, incarnation);
        var destination = Path.Combine(options.RootPath, "segments", id);
        using var destinationParent = paths.AcquireDirectory(Path.GetDirectoryName(destination)!, create: true);
        if (Directory.Exists(destination)) return Validate(destination, id, fingerprint, dimensions, membership, vectors, incarnation);
        RequireCapacity(vectors.Sum(vector => (long)vector.Values.Length));
        var staging = Path.Combine(options.RootPath, "staging", "segments", Guid.NewGuid().ToString("N"));
        try
        {
            using (paths.AcquireDirectory(staging, create: true))
            {
                var nativePath = Path.Combine(staging, "index.usearch");
                using (var index = new USearchIndex(MetricKind.Cos, ScalarKind.Float32, (ulong)dimensions, 0, 0, 0, false))
                {
                    foreach (var vector in vectors)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var values = new float[dimensions];
                        Buffer.BlockCopy(vector.Values, 0, values, 0, vector.Values.Length);
                        index.Add((ulong)vector.VectorId, values);
                    }
                    index.Save(nativePath);
                }
                var metadata = new SegmentMetadata(new Guid(Convert.FromHexString(id)[..16]), fingerprint, "cos", dimensions,
                    vectors.Count, membership, 2, id, NativeFormat, HashFile(nativePath), new FileInfo(nativePath).Length);
                using var metadataFile = new FileStream(Path.Combine(staging, "metadata.json"), FileMode.CreateNew, FileAccess.Write, FileShare.None);
                JsonSerializer.Serialize(metadataFile, metadata);
                metadataFile.Flush(true);
            }
            _ = Validate(staging, id, fingerprint, dimensions, membership, vectors, incarnation);
            cancellationToken.ThrowIfCancellationRequested();
            try { Directory.Move(staging, destination); }
            catch (IOException) when (Directory.Exists(destination))
            {
                // Concurrent identical builders reuse the validated first placement, never overwrite it.
                return Validate(destination, id, fingerprint, dimensions, membership, vectors, incarnation);
            }
            return Validate(destination, id, fingerprint, dimensions, membership, vectors, incarnation);
        }
        finally
        {
            if (Directory.Exists(staging))
            {
                using (paths.AcquireDirectory(staging))
                {
                    foreach (var filename in new[] { "index.usearch", "metadata.json" })
                    {
                        var path = Path.Combine(staging, filename);
                        if (File.Exists(path)) File.Delete(path);
                    }
                }
                // Only the exact random staging leaf is removed, without recursive traversal.
                Directory.Delete(staging, false);
            }
        }
    }

    internal void RequireCapacity(long payloadBytes)
    {
        // Headroom is admission, not a disk reservation. Recheck every native unit;
        // other applications can still consume storage after this observation.
        var required = checked(1024L * 1024 * 1024 + 16L * 1024 * 1024 + 4 * payloadBytes);
        var available = AvailableBytesOverride?.Invoke() ??
            new DriveInfo(Path.GetPathRoot(Path.GetFullPath(options.RootPath))!).AvailableFreeSpace;
        if (available < required) throw new IndexStorageCapacityException();
    }

    private IndexSegmentDescriptor Validate(string directory, string id, string fingerprint, int dimensions,
        string membership, IReadOnlyList<CanonicalVector> vectors, Guid incarnation)
    {
        using var held = paths.Acquire(directory);
        var metadataPath = Path.Combine(directory, "metadata.json");
        if (new FileInfo(metadataPath).Length > 65536) throw new IndexGenerationValidationException("segment-metadata-too-large");
        var metadata = JsonSerializer.Deserialize<SegmentMetadata>(File.ReadAllText(metadataPath))
            ?? throw new IndexGenerationValidationException("segment-metadata-invalid");
        var nativePath = Path.Combine(directory, "index.usearch");
        if (metadata.FormatVersion != 2 || metadata.SegmentId != id || metadata.NativeFormat != NativeFormat ||
            metadata.FileLength != new FileInfo(nativePath).Length || metadata.FileChecksum != HashFile(nativePath))
            throw new IndexGenerationValidationException("segment-file-proof-mismatch");
        var descriptor = new IndexGenerationDescriptor(new Guid(Convert.FromHexString(id)[..16]), fingerprint, dimensions,
            directory, membership, vectors.Count);
        new UsearchGenerationValidator().Validate(directory, descriptor, vectors);
        return new(id, fingerprint, dimensions, directory, membership, metadata.FileChecksum,
            metadata.FileLength, vectors.Count, NativeFormat, PhysicalIncarnation: incarnation);
    }

    private static string HashFile(string path)
    {
        using var file = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(file));
    }

    internal static void ValidateInput(CanonicalVector vector, string fingerprint, int dimensions, long previous)
    {
        if (vector.VectorId <= previous || vector.TextChunkId <= 0 || vector.ModelFingerprint != fingerprint ||
            vector.Dimensions != dimensions || vector.Values.Length != dimensions * sizeof(float) ||
            vector.PayloadChecksum != Convert.ToHexStringLower(SHA256.HashData(vector.Values)))
            throw new IndexGenerationValidationException("segment-canonical-vector-invalid");
        var nonzero = false;
        for (var offset = 0; offset < vector.Values.Length; offset += sizeof(float))
        {
            var value = BitConverter.ToSingle(vector.Values, offset);
            if (!float.IsFinite(value)) throw new IndexGenerationValidationException("segment-canonical-vector-nonfinite");
            nonzero |= value != 0;
        }
        if (!nonzero) throw new IndexGenerationValidationException("segment-canonical-vector-zero");
    }

    private sealed record SegmentMetadata(Guid GenerationId, string ModelFingerprint, string Metric, int Dimensions,
        long VectorCount, string Checksum, int FormatVersion, string SegmentId, string NativeFormat, string FileChecksum,
        long FileLength, CorpusPublicationStamp? CorpusStamp = null);
}
