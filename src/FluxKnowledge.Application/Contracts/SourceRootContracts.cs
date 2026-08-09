using FluxKnowledge.Domain.Common;
using FluxKnowledge.Domain.Sources;

namespace FluxKnowledge.Application.Contracts;

public enum ScanStartIntent
{
    SaveOnly,
    SaveAndScan
}

public sealed record SourceRootCreateRequest(
    string FullPath,
    string DisplayName,
    bool Recursive,
    IReadOnlyList<string> IncludePatterns,
    IReadOnlyList<string> ExcludePatterns,
    bool FollowLinks,
    long MaximumFileBytes,
    IReadOnlyList<string> AllowedClassifications,
    TimeSpan ReconciliationCadence,
    string RequestedBy,
    SourceRootPathValidation? PathValidation = null);

public sealed record SourceRootPathValidation(
    string CanonicalPath,
    SourceRootPhysicalIdentity PhysicalIdentity,
    SourceRootPermissionEvidence PermissionEvidence)
{
    public string PermissionEvidenceJson => PermissionEvidence.SanitisedJson;
}

public sealed record SourceRootPhysicalIdentity(
    string CanonicalPath,
    string VolumeRoot,
    bool IsFixedNtfs,
    string IdentityFingerprint);

public sealed record SourceRootPermissionEvidence(
    bool CanEnumerate,
    string PathFingerprint,
    string SanitisedJson);

public interface ISourceRootPathPolicy
{
    SourceRootPathValidation ValidateAndCanonicalise(SourceRootCreateRequest request);
}

public sealed record SourceRootReceipt(
    SourceRootId SourceRootId,
    SourceScanRequestId SourceScanRequestId,
    JobId ControlJobId,
    DispatchMessageId OutboxId,
    bool IsHeld);

public sealed record SourceActivityDraft(
    SourceRevisionId SourceRevisionId,
    SourceActivityKind ActivityKind,
    ExecutionClass ExecutionClass,
    string ProcessorVersion,
    string InputFingerprint,
    string? RequiredCapability,
    string? Reason,
    SourceActivityState? InitialState = null);

public sealed record SourceArtifactMetadata(
    string ContentSha256,
    string ContentType,
    long ByteLength);

public sealed record SourceArtifactReceipt(
    SourceArtifactId SourceArtifactId,
    string ContentSha256,
    string StoreRelativePath,
    long ByteLength,
    bool ExistingArtifact);

public sealed record SourceRetentionConvergence(
    SourceRevisionId SourceRevisionId,
    bool IsRetentionBlocked);

public sealed record SourceScanResult(
    SourceRootId SourceRootId,
    SourceScanRequestId SourceScanRequestId,
    int DiscoveredCount,
    int IndexedCount,
    int DeferredCount,
    int BlockedCount);
