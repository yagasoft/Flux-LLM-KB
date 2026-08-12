using FluxKnowledge.Domain.Sources;

namespace FluxKnowledge.Application.Sources;

/// <summary>Trusted-local immutable detail for a retained processor branch; it is never an export projection.</summary>
public sealed record LocalRetainedDetailProjection(
    Guid BranchId,
    Guid SourceActivityId,
    SourceRevisionId SourceRevisionId,
    string LocalPath,
    string ArtifactHash,
    string InputHash,
    long ArtifactByteLength,
    LocalRetainedContentHandle ContentHandle,
    IReadOnlyList<LocalRetainedMemberProjection> Members,
    IReadOnlyList<LocalRetainedAttemptProjection> Attempts);

/// <summary>Opaque local handle used only to request a retained excerpt; it is not a filesystem locator.</summary>
public sealed record LocalRetainedContentHandle(Guid BranchId, SourceRevisionId SourceRevisionId);

/// <summary>Bounded retained-branch child/member provenance.</summary>
public sealed record LocalRetainedMemberProjection(
    Guid MemberId,
    string MemberFingerprint,
    Guid? ChildSourceRevisionId,
    Guid? ChildSourceActivityId,
    string Disposition,
    string? ReasonCode,
    long ByteLength);

/// <summary>Bounded, secret-filtered retained processor attempt evidence.</summary>
public sealed record LocalRetainedAttemptProjection(
    Guid AttemptId,
    long LeaseGeneration,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset? FinishedAtUtc,
    string? OutcomeCode,
    string? Diagnostic,
    bool DiagnosticWithheld,
    string? DiagnosticReasonCode);
