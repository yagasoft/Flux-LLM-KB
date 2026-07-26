using System.Text.Json;
using Cloud.Unum.USearch;
using FluxKnowledge.Application.Ports;

namespace FluxKnowledge.Infrastructure.Usearch;

public sealed class UsearchGenerationBuilder(
    IIndexGenerationStore store,
    UsearchIndexOptions options,
    UsearchGenerationValidator validator) : IIndexGenerationPublisher
{
    public ValueTask<IndexGenerationDescriptor> RebuildFromSqlAsync(
        Guid indexGenerationId,
        CancellationToken cancellationToken) => BuildAndPlaceAsync(indexGenerationId, cancellationToken);

    public async ValueTask<IndexGenerationDescriptor> BuildAndPlaceAsync(
        Guid indexGenerationId,
        CancellationToken cancellationToken)
    {
        var seed = await store.GetGenerationAsync(indexGenerationId, cancellationToken)
            ?? throw new InvalidOperationException("The SQL index generation does not exist.");
        var vectors = await store.ReadVectorsAsync(indexGenerationId, cancellationToken);
        var dimensions = seed.Dimensions;
        if (vectors.Any(vector => vector.Dimensions != dimensions || vector.Values.Length != dimensions * sizeof(float)))
        {
            throw new IndexGenerationValidationException("SQL vectors do not match the candidate dimensions.");
        }

        var staging = Path.Combine(options.RootPath, "staging", indexGenerationId.ToString("N"), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            var checksum = UsearchGenerationValidator.ComputeChecksum(indexGenerationId, dimensions, vectors);
            var candidate = new IndexGenerationDescriptor(indexGenerationId, seed.ModelFingerprint, dimensions,
                Path.Combine(options.RootPath, "generations", indexGenerationId.ToString("N")), checksum, vectors.Count);
            using (var index = new USearchIndex(MetricKind.Cos, ScalarKind.Float32, (ulong)dimensions, 0, 0, 0, false))
            {
                foreach (var vector in vectors)
                {
                    var values = new float[dimensions];
                    Buffer.BlockCopy(vector.Values, 0, values, 0, vector.Values.Length);
                    index.Add((ulong)vector.VectorId, values);
                }

                index.Save(Path.Combine(staging, UsearchGenerationValidator.IndexFileName));
            }

            File.WriteAllText(Path.Combine(staging, UsearchGenerationValidator.MetadataFileName),
                JsonSerializer.Serialize(new UsearchGenerationValidator.Metadata(
                    candidate.Id, candidate.Dimensions, candidate.VectorCount, candidate.MetadataChecksum)));
            validator.Validate(staging, candidate, vectors);
            var finalPath = AtomicGenerationPlacement.Place(options, indexGenerationId, staging);
            candidate = candidate with { IndexPath = finalPath };
            await store.UpdateGenerationMetadataAsync(candidate, cancellationToken);
            return candidate;
        }
        catch
        {
            if (Directory.Exists(staging))
            {
                Directory.Delete(staging, recursive: true);
            }

            throw;
        }
    }
}
