using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Cloud.Unum.USearch;
using FluxKnowledge.Application.Ports;

namespace FluxKnowledge.Infrastructure.Usearch;

public sealed class IndexGenerationValidationException(string message) : Exception(message);

public class UsearchGenerationValidator
{
    public const string IndexFileName = "index.usearch";
    public const string MetadataFileName = "metadata.json";

    public static string ComputeChecksum(string modelFingerprint, int dimensions, IReadOnlyList<CanonicalVector> vectors)
    {
        var data = $"{modelFingerprint}|cos|{dimensions}|{string.Join(',', vectors.Select(vector => $"{vector.VectorId}:{vector.ContentHash}"))}";
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(data)));
    }

    public static Guid DeterministicGenerationId(string checksum)
    {
        var bytes = Convert.FromHexString(checksum)[..16];
        return new Guid(bytes);
    }

    public virtual void Validate(string directory, IndexGenerationDescriptor expected, IReadOnlyList<CanonicalVector> vectors)
    {
        if (vectors.Any(vector =>
                !string.Equals(
                    vector.ContentHash,
                    Convert.ToHexStringLower(SHA256.HashData(vector.Values)),
                    StringComparison.Ordinal)) ||
            !string.Equals(
                expected.MetadataChecksum,
                ComputeChecksum(expected.ModelFingerprint, expected.Dimensions, vectors),
                StringComparison.Ordinal))
        {
            throw new IndexGenerationValidationException("SQL vector bytes do not match the candidate content identity.");
        }
        var metadataPath = Path.Combine(directory, MetadataFileName);
        var metadata = JsonSerializer.Deserialize<Metadata>(File.ReadAllText(metadataPath))
            ?? throw new IndexGenerationValidationException("Generation metadata cannot be read.");
        if (metadata.GenerationId != expected.Id || metadata.ModelFingerprint != expected.ModelFingerprint ||
            metadata.Metric != "cos" || metadata.Dimensions != expected.Dimensions ||
            metadata.VectorCount != expected.VectorCount ||
            !string.Equals(metadata.Checksum, expected.MetadataChecksum, StringComparison.Ordinal))
        {
            throw new IndexGenerationValidationException("Generation metadata does not match the SQL candidate.");
        }

        using var index = new USearchIndex(Path.Combine(directory, IndexFileName), false);
        if (index.Dimensions() != (ulong)expected.Dimensions || index.Size() != (ulong)expected.VectorCount)
        {
            throw new IndexGenerationValidationException("USearch generation dimensions or vector count is invalid.");
        }

        foreach (var vector in vectors)
        {
            if (!index.Contains((ulong)vector.VectorId))
            {
                throw new IndexGenerationValidationException("USearch generation does not contain every SQL vector ID.");
            }

            var found = index.Get((ulong)vector.VectorId, out float[] persisted);
            var expectedValues = ToFloatValues(vector.Values);
            if (found != 1 || persisted.Length != expectedValues.Length ||
                !persisted.Zip(expectedValues, static (actual, expected) =>
                    BitConverter.SingleToInt32Bits(actual) == BitConverter.SingleToInt32Bits(expected)).All(static equal => equal))
            {
                throw new IndexGenerationValidationException("USearch generation vector payload does not match SQL bytes.");
            }
        }

        VerifyCosineMetric(index, vectors);
    }

    private static void VerifyCosineMetric(
        USearchIndex index,
        IReadOnlyList<CanonicalVector> vectors)
    {
        var vector = vectors[0];
        var stored = ToFloatValues(vector.Values);
        if (stored.All(static value => value == 0F))
        {
            throw new IndexGenerationValidationException("USearch metric cannot be validated against a zero vector.");
        }

        var query = stored.Select(static value => value * 0.5F).ToArray();
        var count = index.Search(query, Math.Max(1, vectors.Count), out var keys, out var distances);
        var position = Array.IndexOf(keys, (ulong)vector.VectorId, 0, count);
        if (position < 0)
        {
            throw new IndexGenerationValidationException("USearch metric probe did not return the stored vector.");
        }

        var expectedDistance = CosineDistance(query, stored);
        if (MathF.Abs(distances[position] - expectedDistance) > 0.001F)
        {
            throw new IndexGenerationValidationException("The reopened USearch index metric is not cosine distance.");
        }
    }

    private static float[] ToFloatValues(byte[] bytes)
    {
        var values = new float[bytes.Length / sizeof(float)];
        Buffer.BlockCopy(bytes, 0, values, 0, bytes.Length);
        return values;
    }

    private static float CosineDistance(float[] left, float[] right)
    {
        var dot = left.Zip(right, static (a, b) => a * b).Sum();
        var leftNorm = MathF.Sqrt(left.Sum(static value => value * value));
        var rightNorm = MathF.Sqrt(right.Sum(static value => value * value));
        return 1F - dot / (leftNorm * rightNorm);
    }

    public sealed record Metadata(
        Guid GenerationId,
        string ModelFingerprint,
        string Metric,
        int Dimensions,
        long VectorCount,
        string Checksum);
}
