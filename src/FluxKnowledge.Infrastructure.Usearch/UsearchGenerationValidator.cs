using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Cloud.Unum.USearch;
using FluxKnowledge.Application.Ports;

namespace FluxKnowledge.Infrastructure.Usearch;

public sealed class IndexGenerationValidationException(string message) : Exception(message);

public sealed class UsearchGenerationValidator
{
    public const string IndexFileName = "index.usearch";
    public const string MetadataFileName = "metadata.json";

    public static string ComputeChecksum(Guid id, int dimensions, IReadOnlyList<CanonicalVector> vectors)
    {
        var data = $"{id:N}|{dimensions}|{string.Join(',', vectors.Select(vector => vector.VectorId))}";
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(data)));
    }

    public void Validate(string directory, IndexGenerationDescriptor expected, IReadOnlyList<CanonicalVector> vectors)
    {
        var metadataPath = Path.Combine(directory, MetadataFileName);
        var metadata = JsonSerializer.Deserialize<Metadata>(File.ReadAllText(metadataPath))
            ?? throw new IndexGenerationValidationException("Generation metadata cannot be read.");
        if (metadata.GenerationId != expected.Id || metadata.Dimensions != expected.Dimensions ||
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
    }

    public sealed record Metadata(Guid GenerationId, int Dimensions, long VectorCount, string Checksum);
}
