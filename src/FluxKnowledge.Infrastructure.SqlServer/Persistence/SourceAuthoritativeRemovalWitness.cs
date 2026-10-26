namespace FluxKnowledge.Infrastructure.SqlServer.Persistence;

internal sealed record SourceAuthoritativeRemovalWitness(int Version, Guid SourceRootId, Guid SourceRevisionId,
    string RelativePath, long ConfigurationRevision, long BaselineEpoch, long RecoveryGeneration,
    Guid SourceScanRequestId, Guid ManifestId, long LeaseGeneration, string Category, DateTimeOffset SuppressedAtUtc,
    string? JournalId, long CoveredThroughUsn, string? RepositoryIdentity, string? GitInventoryGeneration);
