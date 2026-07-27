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
        var vectors = await store.ReadEligibleVectorsAsync(cancellationToken);
        if (vectors.Count == 0)
        {
            throw new IndexGenerationValidationException("The current SQL corpus has no eligible vectors.");
        }

        var dimensions = vectors[0].Dimensions;
        var fingerprint = vectors[0].ModelFingerprint;
        if (vectors.Any(vector => vector.Dimensions != dimensions || vector.Values.Length != dimensions * sizeof(float)))
        {
            throw new IndexGenerationValidationException("SQL vectors do not match the candidate dimensions.");
        }

        if (vectors.Any(vector => !string.Equals(vector.ModelFingerprint, fingerprint, StringComparison.Ordinal)))
        {
            throw new IndexGenerationValidationException("The current SQL corpus has incompatible model fingerprints.");
        }

        var membershipChecksum = UsearchGenerationValidator.ComputeChecksum(fingerprint, dimensions, vectors);
        var candidateId = UsearchGenerationValidator.DeterministicGenerationId(membershipChecksum);
        var finalDirectory = Path.Combine(options.RootPath, "generations", candidateId.ToString("N"));
        var candidate = new IndexGenerationDescriptor(candidateId, fingerprint, dimensions,
            finalDirectory, membershipChecksum, vectors.Count);
        if (Directory.Exists(finalDirectory))
        {
            validator.Validate(finalDirectory, candidate, vectors);
            await store.UpdateGenerationMetadataAsync(candidate, cancellationToken);
            return candidate;
        }

        var staging = Path.Combine(options.RootPath, "staging", candidateId.ToString("N"), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
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
                    candidate.Id, candidate.ModelFingerprint, "cos", candidate.Dimensions,
                    candidate.VectorCount, candidate.MetadataChecksum)));
            validator.Validate(staging, candidate, vectors);
            var finalPath = AtomicGenerationPlacement.Place(options, candidateId, staging);
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
