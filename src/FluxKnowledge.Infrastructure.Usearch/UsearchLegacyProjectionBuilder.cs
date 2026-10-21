using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Cloud.Unum.USearch;
using FluxKnowledge.Application.Ports;

namespace FluxKnowledge.Infrastructure.Usearch;

public sealed class UsearchLegacyProjectionBuilder(UsearchIndexOptions options, IIndexGenerationReadLeaseFactory paths)
{
    public async ValueTask<IndexGenerationDescriptor> BuildAsync(IndexProjectionRebuildPlan plan, Func<IAsyncEnumerable<CanonicalVector>> vectors, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (plan.OperationId == Guid.Empty || plan.TargetFormat != 1 || plan.Dimensions <= 0)
            throw new ArgumentException("index-legacy-rebuild-plan-invalid");
        var parent = Path.Combine(options.RootPath, "staging", "legacy");
        using var sourceParent = paths.AcquireBuildDirectory(parent, create: true);
        using var destinationParent = paths.AcquireBuildDirectory(Path.Combine(options.RootPath, "generations"), create: true);
        var staging = Path.Combine(parent, Guid.NewGuid().ToString("N"));
        try
        {
            IndexGenerationDescriptor result;
            using (paths.AcquireBuildDirectory(staging, create: true))
            {
                long count = 0, last = 0;
                using var hash = BeginChecksum(plan.ModelFingerprint, plan.Dimensions);
                using (var native = new USearchIndex(MetricKind.Cos, ScalarKind.Float32, (ulong)plan.Dimensions, 0, 0, 0, false))
                {
                    await foreach (var vector in vectors().WithCancellation(token))
                    {
                        UsearchSegmentBuilder.ValidateInput(vector, plan.ModelFingerprint, plan.Dimensions, last);
                        AppendChecksum(hash, vector, count++ == 0); last = vector.VectorId;
                        var values = new float[plan.Dimensions]; Buffer.BlockCopy(vector.Values, 0, values, 0, vector.Values.Length);
                        native.Add((ulong)vector.VectorId, values);
                    }
                    if (count == 0) throw new InvalidOperationException("index-legacy-rollback-has-no-eligible-vectors-keep-current-format");
                    native.Save(Path.Combine(staging, "index.usearch"));
                }
                var checksum = Convert.ToHexStringLower(hash.GetHashAndReset());
                var id = new Guid(SHA256.HashData(Encoding.UTF8.GetBytes(FormattableString.Invariant(
                    $"legacy-rebuild/v1|{plan.OperationId:N}|{plan.Stamp.CorpusEpoch:N}|{plan.Stamp.CorpusVersion}|{checksum}")))[..16]);
                result = new(id, plan.ModelFingerprint, plan.Dimensions, Path.Combine(options.RootPath, "generations", id.ToString("N")), checksum, count, plan.Stamp);
                using var file = new FileStream(Path.Combine(staging, "metadata.json"), FileMode.CreateNew, FileAccess.Write, FileShare.None);
                JsonSerializer.Serialize(file, new UsearchGenerationValidator.Metadata(id, plan.ModelFingerprint, "cos", plan.Dimensions, count, checksum, plan.Stamp));
                file.Flush(true);
            }
            await ValidateAtAsync(staging, result, vectors, token);
            token.ThrowIfCancellationRequested();
            if (!Directory.Exists(result.IndexPath))
            {
                try { Directory.Move(staging, result.IndexPath); }
                catch (IOException) when (Directory.Exists(result.IndexPath)) { /* Validate an exact concurrent placement below. */ }
            }
            await ValidateAsync(result, vectors, token);
            return result;
        }
        finally
        {
            if (Directory.Exists(staging))
            {
                using (paths.AcquireBuildDirectory(staging))
                    foreach (var filename in new[] { "index.usearch", "metadata.json" })
                    {
                        var file = Path.Combine(staging, filename);
                        if (File.Exists(file)) File.Delete(file);
                    }
                Directory.Delete(staging, false);
            }
        }
    }

    public ValueTask ValidateAsync(IndexGenerationDescriptor generation, Func<IAsyncEnumerable<CanonicalVector>> vectors, CancellationToken token)
    {
        var expected = Path.Combine(options.RootPath, "generations", generation.Id.ToString("N"));
        if (generation.FormatVersion != 1 || generation.CorpusStamp is null || !string.Equals(expected, generation.IndexPath, StringComparison.OrdinalIgnoreCase))
            throw new IndexGenerationValidationException("index-legacy-rebuild-descriptor-invalid");
        return ValidateAtAsync(generation.IndexPath, generation, vectors, token);
    }

    private async ValueTask ValidateAtAsync(string directory, IndexGenerationDescriptor expected, Func<IAsyncEnumerable<CanonicalVector>> vectors, CancellationToken token)
    {
        using var held = paths.Acquire(directory);
        var metadataPath = Path.Combine(directory, "metadata.json");
        if (new FileInfo(metadataPath).Length > 65536) throw new IndexGenerationValidationException("index-legacy-rebuild-metadata-too-large");
        var metadata = JsonSerializer.Deserialize<UsearchGenerationValidator.Metadata>(await File.ReadAllTextAsync(metadataPath, token));
        if (metadata != new UsearchGenerationValidator.Metadata(expected.Id, expected.ModelFingerprint, "cos", expected.Dimensions,
            expected.VectorCount, expected.MetadataChecksum, expected.CorpusStamp))
            throw new IndexGenerationValidationException("index-legacy-rebuild-metadata-invalid");
        using var native = new USearchIndex(Path.Combine(directory, "index.usearch"), false);
        if (native.Size() != (ulong)expected.VectorCount || native.Dimensions() != (ulong)expected.Dimensions)
            throw new IndexGenerationValidationException("index-legacy-rebuild-native-profile-invalid");
        using var hash = BeginChecksum(expected.ModelFingerprint, expected.Dimensions);
        long count = 0, last = 0; CanonicalVector? sample = null;
        await foreach (var vector in vectors().WithCancellation(token))
        {
            UsearchSegmentBuilder.ValidateInput(vector, expected.ModelFingerprint, expected.Dimensions, last);
            AppendChecksum(hash, vector, count++ == 0); last = vector.VectorId; sample ??= vector;
            if (!native.Contains((ulong)vector.VectorId) || native.Get((ulong)vector.VectorId, out float[] stored) != 1 || stored.Length != expected.Dimensions)
                throw new IndexGenerationValidationException("index-legacy-rebuild-vector-missing");
            var bytes = new byte[vector.Values.Length]; Buffer.BlockCopy(stored, 0, bytes, 0, bytes.Length);
            if (!bytes.AsSpan().SequenceEqual(vector.Values)) throw new IndexGenerationValidationException("index-legacy-rebuild-payload-mismatch");
        }
        if (count != expected.VectorCount || Convert.ToHexStringLower(hash.GetHashAndReset()) != expected.MetadataChecksum || sample is null)
            throw new IndexGenerationValidationException("index-legacy-rebuild-membership-invalid");
        UsearchGenerationValidator.VerifyCosineMetric(native, [sample], (int)Math.Min(int.MaxValue, count));
    }

    private static IncrementalHash BeginChecksum(string profile, int dimensions)
    {
        var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.UTF8.GetBytes(FormattableString.Invariant($"{profile}|cos|{dimensions}|")));
        return hash;
    }

    private static void AppendChecksum(IncrementalHash hash, CanonicalVector vector, bool first) =>
        hash.AppendData(Encoding.UTF8.GetBytes((first ? "" : ",") + FormattableString.Invariant($"{vector.VectorId}:{vector.PayloadChecksum}")));
}
