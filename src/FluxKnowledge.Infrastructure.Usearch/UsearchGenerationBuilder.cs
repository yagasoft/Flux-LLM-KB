using System.Text.Json;
using Cloud.Unum.USearch;
using FluxKnowledge.Application.Ports;

namespace FluxKnowledge.Infrastructure.Usearch;

public sealed class RecoveryCandidatePlacementException(string path, Exception innerException)
    : Exception("Recovery candidate validation failed after placement.", innerException)
{
    public string Path { get; } = path;
}

public sealed class UsearchGenerationBuilder(
    IIndexGenerationStore store,
    UsearchIndexOptions options,
    UsearchGenerationValidator validator) : IIndexGenerationPublisher
{
    public ValueTask<IndexGenerationDescriptor> BuildRecoveryCandidateAsync(
        IndexGenerationDescriptor generation,
        IReadOnlyList<CanonicalVector> membership,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var staging = Path.Combine(options.RootPath, "staging", "recovery", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        string? placedPath = null;
        try
        {
            SaveCandidate(staging, generation, membership);
            validator.Validate(staging, generation, membership);
            var path = AtomicGenerationPlacement.PlaceRecovery(options, generation.Id, staging);
            placedPath = path;
            validator.Validate(path, generation with { IndexPath = path }, membership);
            return ValueTask.FromResult(generation with { IndexPath = path });
        }
        catch (Exception exception) when (placedPath is not null)
        {
            throw new RecoveryCandidatePlacementException(placedPath, exception);
        }
        catch
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
            throw;
        }
    }

    public async ValueTask<IndexGenerationDescriptor> RebuildFromSqlAsync(
        Guid indexGenerationId,
        CancellationToken cancellationToken)
    {
        var existing = await store.GetGenerationAsync(indexGenerationId, cancellationToken)
            ?? throw new InvalidOperationException("The SQL index generation does not exist.");
        var vectors = await store.ReadVectorsAsync(indexGenerationId, cancellationToken);
        if (vectors.Count == 0 || !string.Equals(
                existing.MetadataChecksum,
                UsearchGenerationValidator.ComputeChecksum(existing.ModelFingerprint, existing.Dimensions, vectors),
                StringComparison.Ordinal))
        {
            throw new IndexGenerationValidationException("The immutable SQL generation membership cannot be rebuilt safely.");
        }

        if (Directory.Exists(existing.IndexPath))
        {
            validator.Validate(existing.IndexPath, existing, vectors);
            return existing;
        }

        var staging = Path.Combine(options.RootPath, "staging", existing.Id.ToString("N"), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            SaveCandidate(staging, existing, vectors);
            validator.Validate(staging, existing, vectors);
            var finalPath = AtomicGenerationPlacement.Place(options, existing.Id, staging);
            var rebuilt = existing with { IndexPath = finalPath };
            await store.UpdateGenerationMetadataAsync(rebuilt, cancellationToken);
            return rebuilt;
        }
        catch
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
            throw;
        }
    }

    public async ValueTask<IndexGenerationCandidateSnapshot> BuildAndPlaceAsync(
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
            return new IndexGenerationCandidateSnapshot(candidate, vectors);
        }

        var staging = Path.Combine(options.RootPath, "staging", candidateId.ToString("N"), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            SaveCandidate(staging, candidate, vectors);
            validator.Validate(staging, candidate, vectors);
            try
            {
                var finalPath = AtomicGenerationPlacement.Place(options, candidateId, staging);
                candidate = candidate with { IndexPath = finalPath };
                return new IndexGenerationCandidateSnapshot(candidate, vectors);
            }
            catch (IOException) when (Directory.Exists(finalDirectory))
            {
                validator.Validate(finalDirectory, candidate, vectors);
                if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
                return new IndexGenerationCandidateSnapshot(candidate, vectors);
            }
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

    private static void SaveCandidate(
        string staging,
        IndexGenerationDescriptor candidate,
        IReadOnlyList<CanonicalVector> vectors)
    {
        using (var index = new USearchIndex(MetricKind.Cos, ScalarKind.Float32, (ulong)candidate.Dimensions, 0, 0, 0, false))
        {
            foreach (var vector in vectors)
            {
                var values = new float[candidate.Dimensions];
                Buffer.BlockCopy(vector.Values, 0, values, 0, vector.Values.Length);
                index.Add((ulong)vector.VectorId, values);
            }
            index.Save(Path.Combine(staging, UsearchGenerationValidator.IndexFileName));
        }
        File.WriteAllText(Path.Combine(staging, UsearchGenerationValidator.MetadataFileName),
            JsonSerializer.Serialize(new UsearchGenerationValidator.Metadata(
                candidate.Id, candidate.ModelFingerprint, "cos", candidate.Dimensions,
                candidate.VectorCount, candidate.MetadataChecksum)));
    }
}
