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
        }

        VerifyCosineMetric(index, vectors, expected.Dimensions);
    }

    private static void VerifyCosineMetric(
        USearchIndex index,
        IReadOnlyList<CanonicalVector> vectors,
        int dimensions)
    {
        if (vectors.Count < 2)
        {
            return;
        }

        var query = new float[dimensions];
        Buffer.BlockCopy(vectors[0].Values, 0, query, 0, vectors[0].Values.Length);
        var count = index.Search(query, Math.Min(2, vectors.Count), out var keys, out var distances);
        var second = Enumerable.Range(0, count).FirstOrDefault(position => keys[position] != (ulong)vectors[0].VectorId);
        if (count < 2)
        {
            throw new IndexGenerationValidationException("USearch metric probe did not return two vectors.");
        }

        var other = vectors.Single(vector => vector.VectorId == (long)keys[second]);
        var expectedDistance = 1F - Dot(query, other.Values);
        if (MathF.Abs(distances[second] - expectedDistance) > 0.001F)
        {
            throw new IndexGenerationValidationException("The reopened USearch index metric is not cosine distance.");
        }
    }

    private static float Dot(float[] left, byte[] rightBytes)
    {
        var right = new float[left.Length];
        Buffer.BlockCopy(rightBytes, 0, right, 0, rightBytes.Length);
        return left.Zip(right, static (a, b) => a * b).Sum();
    }

    public sealed record Metadata(
        Guid GenerationId,
        string ModelFingerprint,
        string Metric,
        int Dimensions,
        long VectorCount,
        string Checksum);
}
