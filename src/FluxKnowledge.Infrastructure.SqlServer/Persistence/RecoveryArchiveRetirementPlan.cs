using FluxKnowledge.Application.Gpu;

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence;

/// <summary>Explicit privileged disposition of rollback archives superseded by a verified running release.</summary>
public sealed record RecoveryArchiveRetirementPlan(
    int Version, Guid OperationId, Guid DatabaseIncarnation, string RecoveryRoot, string IndexRoot,
    string ApplicationEntry, string ApplicationSha256, string RetainedApplicationEntry, string QuarantineRoot,
    string RecoveryIdentity, Guid CorpusEpoch, long CorpusVersion, Guid? ActiveGeneration,
    Guid? CompletedRebuildId, string? CompletedRebuildManifestHash,
    IReadOnlyList<GpuInteractiveOwnerIdentity> ServiceOwners,
    IReadOnlyList<RecoveryWriterDisposition> Writers, IReadOnlyList<RecoveryArchiveMove> Moves,
    IReadOnlyList<string> RetainedPackets, IReadOnlyList<RecoveryRuntimeSnapshot>? RuntimeSnapshots = null,
    IReadOnlyList<string>? RequiredAbsentPaths = null);

public sealed record RecoveryWriterDisposition(Guid Id, string RecoveryRoot, int OwnerProcessId,
    DateTimeOffset OwnerStartedAtUtc, string OwnerMachineFingerprint, bool OwnerExitSufficient, DateTimeOffset CreatedAtUtc);

public sealed record RecoveryArchiveMove(string RelativePath, string DirectoryIdentity);

public sealed record RecoveryRuntimeSnapshot(string RootPath, IReadOnlyList<RecoveryRuntimeFile> Files);
public sealed record RecoveryRuntimeFile(string RelativePath, long Bytes, string Sha256);
