using System.Text.Json;
using Cloud.Unum.USearch;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Operations;
using FluxKnowledge.Application.Indexing;
using Microsoft.Extensions.Logging;

namespace FluxKnowledge.Infrastructure.Usearch;

public sealed class RecoveryCandidatePlacementException(string path, Exception innerException)
    : Exception("Recovery candidate validation failed after placement.", innerException)
{
    public string Path { get; } = path;
}

public sealed class UsearchGenerationBuilder : IIndexGenerationPublisher
{
    private readonly IIndexGenerationStore store;
    private readonly UsearchIndexOptions options;
    private readonly UsearchGenerationValidator validator;
    private readonly LiveRootStorageSafety? _storageSafety;
    private readonly IUsearchDirectoryCreator _directoryCreator;
    private readonly ILogger<UsearchGenerationBuilder>? _logger;

    public UsearchGenerationBuilder(
        IIndexGenerationStore store,
        UsearchIndexOptions options,
        UsearchGenerationValidator validator,
        ILogger<UsearchGenerationBuilder>? logger = null)
        : this(
            store,
            options,
            validator,
            storageSafety: null,
            FileSystemUsearchDirectoryCreator.Instance, logger)
    {
    }

    internal UsearchGenerationBuilder(
        IIndexGenerationStore store,
        UsearchIndexOptions options,
        UsearchGenerationValidator validator,
        LiveRootStorageSafety? storageSafety,
        IUsearchDirectoryCreator directoryCreator,
        ILogger<UsearchGenerationBuilder>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(validator);
        ArgumentNullException.ThrowIfNull(directoryCreator);
        this.store = store;
        this.options = options;
        this.validator = validator;
        _storageSafety = storageSafety;
        _directoryCreator = directoryCreator;
        _logger = logger;
    }

    public ValueTask<IndexGenerationDescriptor> BuildRecoveryCandidateAsync(
        IndexGenerationDescriptor generation,
        IReadOnlyList<CanonicalVector> membership,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var fileSystem = new DerivedIndexFileSystem(options, null, _storageSafety, _directoryCreator);
        if (!fileSystem.TryCreateRecoveryStagingDirectory(out var staging))
        {
            throw new InvalidOperationException("The recovery staging directory cannot be created safely.");
        }
        string? placedPath = null;
        try
        {
            SaveCandidate(staging, generation, membership);
            validator.Validate(staging, generation, membership);
            var path = AtomicGenerationPlacement.PlaceRecovery(
                options,
                generation.Id,
                staging,
                _storageSafety,
                _directoryCreator);
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
            fileSystem.TryQuarantine(staging, []);
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

        EnsureStorageSafe(existing.IndexPath);
        if (Directory.Exists(existing.IndexPath))
        {
            validator.Validate(existing.IndexPath, existing, vectors);
            return existing;
        }

        var fileSystem = new DerivedIndexFileSystem(options, null, _storageSafety, _directoryCreator);
        if (!fileSystem.TryCreateRecoveryStagingDirectory(out var staging))
        {
            throw new InvalidOperationException("The recovery staging directory cannot be created safely.");
        }
        try
        {
            SaveCandidate(staging, existing, vectors);
            validator.Validate(staging, existing, vectors);
            var finalPath = AtomicGenerationPlacement.Place(
                options,
                existing.Id,
                staging,
                _storageSafety,
                _directoryCreator);
            var rebuilt = existing with { IndexPath = finalPath };
            await store.UpdateGenerationMetadataAsync(rebuilt, cancellationToken);
            return rebuilt;
        }
        catch
        {
            fileSystem.TryQuarantine(staging, []);
            throw;
        }
    }

    public async ValueTask<IndexGenerationCandidateSnapshot> BuildAndPlaceAsync(
        Guid indexGenerationId,
        CancellationToken cancellationToken)
    {
        var phase = "snapshot-vector-loading";
        try
        {
            return await BuildAndPlaceCoreAsync(indexGenerationId, () => phase = "placement", cancellationToken);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            PublicationDiagnostics.Failure(_logger, indexGenerationId, phase, exception);
            throw;
        }
    }

    private async ValueTask<IndexGenerationCandidateSnapshot> BuildAndPlaceCoreAsync(Guid indexGenerationId, Action placing, CancellationToken cancellationToken)
    {
        var publication = await store.ReadPublicationSnapshotAsync(indexGenerationId, cancellationToken);
        var vectors = publication.Vectors;
        if (vectors.Count == 0)
        {
            throw new NoEligibleVectorsException();
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
        var candidateId = UsearchGenerationValidator.DeterministicGenerationId(membershipChecksum, publication.PublicationStamp);
        var finalDirectory = Path.Combine(options.RootPath, "generations", candidateId.ToString("N"));
        var candidate = new IndexGenerationDescriptor(candidateId, fingerprint, dimensions,
            finalDirectory, membershipChecksum, vectors.Count, publication.PublicationStamp);
        placing();
        EnsureStorageSafe(finalDirectory);
        if (Directory.Exists(finalDirectory))
        {
            validator.Validate(finalDirectory, candidate, vectors);
            return new IndexGenerationCandidateSnapshot(candidate, vectors, publication.ExpectedCorpusStamp);
        }

        var staging = Path.Combine(options.RootPath, "staging", candidateId.ToString("N"), Guid.NewGuid().ToString("N"));
        EnsureStorageSafe(staging);
        _directoryCreator.CreateDirectory(staging);
        try
        {
            SaveCandidate(staging, candidate, vectors);
            validator.Validate(staging, candidate, vectors);
            try
            {
                var finalPath = AtomicGenerationPlacement.Place(
                    options,
                    candidateId,
                    staging,
                    _storageSafety,
                    _directoryCreator);
                candidate = candidate with { IndexPath = finalPath };
                return new IndexGenerationCandidateSnapshot(candidate, vectors, publication.ExpectedCorpusStamp);
            }
            catch (IOException) when (Directory.Exists(finalDirectory))
            {
                validator.Validate(finalDirectory, candidate, vectors);
                if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
                return new IndexGenerationCandidateSnapshot(candidate, vectors, publication.ExpectedCorpusStamp);
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

    private void SaveCandidate(
        string staging,
        IndexGenerationDescriptor candidate,
        IReadOnlyList<CanonicalVector> vectors)
    {
        EnsureStorageSafe(staging);
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
                candidate.VectorCount, candidate.MetadataChecksum, candidate.CorpusStamp)));
    }

    private void EnsureStorageSafe(string path) => _storageSafety?.ValidateBeforeIo(path);
}
