using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Application.Sources;

namespace FluxKnowledge.Application.Ports;

public interface IRetainedProcessorBranchStore
{
    /// <summary>
    /// Reports whether the applied SQL schema and immutable C# completion writer
    /// contract are both present. A false result is an inert startup state.
    /// </summary>
    ValueTask<bool> IsRetainedCsharpCodeWriterReadyAsync(
        CancellationToken cancellationToken) => ValueTask.FromResult(false);

    /// <summary>Claims only durable C# CodeParsing branches and materialises the persisted attempt identity.</summary>
    ValueTask<IReadOnlyList<RetainedCsharpCodeClaim>> ClaimCsharpCodeAsync(
        string leaseOwner,
        int maximumCount,
        string processorFingerprint,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult<IReadOnlyList<RetainedCsharpCodeClaim>>([]);

    /// <summary>Receipt-first serialisable C# completion writer; never creates generic retained members.</summary>
    ValueTask<RetainedCsharpCodeCompletionWriteResult> CompleteRetainedCsharpCodeAsync(
        RetainedCsharpCodeClaim claim,
        RetainedCsharpCodeCompletion completion,
        CancellationToken cancellationToken) => throw new NotSupportedException();
    /// <summary>Writes or replays one local triage receipt; the operation identity is resolved before the action head.</summary>
    ValueTask<OperatorActionIgnoreReceipt> SetOperatorActionIgnoreAsync(
        OperatorActionIgnoreCommand command,
        CancellationToken cancellationToken) => throw new NotSupportedException();

    /// <summary>Lists exact current supported OOXML blocked action versions and open non-forceable receipts; never reads source bytes.</summary>
    ValueTask<IReadOnlyList<OoxmlForceActionSummary>> ListForceEligibleOoxmlActionsAsync(
        int maximumCount,
        CancellationToken cancellationToken) => ValueTask.FromResult<IReadOnlyList<OoxmlForceActionSummary>>([]);

    /// <summary>Creates or replays one durable OOXML force receipt; no transport is exposed by this contract.</summary>
    ValueTask<OoxmlForceRequestReceipt> RequestForceAsync(
        OoxmlForceRequestCommand command,
        CancellationToken cancellationToken) => throw new NotSupportedException();

    /// <summary>Creates or replays one exact capability-bound policy override; no transport is exposed by this contract.</summary>
    ValueTask<OoxmlForceRequestReceipt> RequestPolicyOverrideAsync(
        OoxmlForceRequestCommand command,
        CancellationToken cancellationToken) => throw new NotSupportedException();

    /// <summary>Claims requested force work only; normal claims must never consume it.</summary>
    ValueTask<IReadOnlyList<RetainedProcessorClaim>> ClaimForceAsync(
        string leaseOwner,
        int maximumCount,
        string processorFingerprint,
        CancellationToken cancellationToken) => ValueTask.FromResult<IReadOnlyList<RetainedProcessorClaim>>([]);

    /// <summary>Converges durable force requests against SQL Server current UTC without source reads.</summary>
    ValueTask<int> ReconcileForceRequestsAsync(CancellationToken cancellationToken) =>
        ReconcileForceRequestsAsync(ooxmlDescriptorEnabled: true, cancellationToken);

    /// <summary>Uses current activation configuration only to cancel already-durable requests for a disabled descriptor.</summary>
    ValueTask<int> ReconcileForceRequestsAsync(bool ooxmlDescriptorEnabled, CancellationToken cancellationToken) => ValueTask.FromResult(0);

    ValueTask<IReadOnlyList<RetainedProcessorPromotionCandidate>> ReadPromotionCandidatesAsync(
        int maximumCount,
        CancellationToken cancellationToken);

    ValueTask<IReadOnlyList<RetainedProcessorPromotionCandidate>> ReadPromotionCandidatesAsync(
        int maximumCount,
        SourceCapabilityDescriptor capability,
        CancellationToken cancellationToken) => ReadPromotionCandidatesAsync(maximumCount, cancellationToken);

    ValueTask<bool> PromoteAsync(
        RetainedProcessorPromotionCandidate candidate,
        SourceCapabilityDescriptor capability,
        CancellationToken cancellationToken);

    ValueTask<bool> BlockPromotionAsync(
        RetainedProcessorPromotionCandidate candidate,
        string outcomeCode,
        CancellationToken cancellationToken);

    /// <summary>
    /// Returns generic deferred rows which may be safely redesignated as legacy Office
    /// parser-unavailable work. This is intentionally not a runnable capability selector.
    /// </summary>
    ValueTask<IReadOnlyList<RetainedProcessorPromotionCandidate>> ReadLegacyOfficeDesignationCandidatesAsync(
        int maximumCount,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult<IReadOnlyList<RetainedProcessorPromotionCandidate>>([]);

    /// <summary>Writes the durable legacy-parser-unavailable successor and supersession relation; never creates a branch.</summary>
    ValueTask<bool> DesignateLegacyOfficeAsync(
        RetainedProcessorPromotionCandidate candidate,
        CancellationToken cancellationToken) => ValueTask.FromResult(false);

    ValueTask<IReadOnlyList<RetainedProcessorClaim>> ClaimAsync(
        string leaseOwner,
        int maximumCount,
        CancellationToken cancellationToken);

    ValueTask<IReadOnlyList<RetainedProcessorClaim>> ClaimAsync(
        string leaseOwner,
        int maximumCount,
        string processorFingerprint,
        CancellationToken cancellationToken) => ClaimAsync(leaseOwner, maximumCount, cancellationToken);

    ValueTask<bool> CommitAsync(
        RetainedProcessorClaim claim,
        RetainedProcessorCompletion completion,
        CancellationToken cancellationToken);

    ValueTask<bool> RetryAsync(
        RetainedProcessorClaim claim,
        string outcomeCode,
        CancellationToken cancellationToken);

    ValueTask<bool> FailAsync(
        RetainedProcessorClaim claim,
        RetainedProcessorFailure failure,
        CancellationToken cancellationToken);
}

public sealed record RetainedProcessorPromotionCandidate(
    Guid LegacyActivityId,
    SourceRevisionId SourceRevisionId,
    string InputSha256,
    string Extension = "");

/// <summary>Processor-derived child manifest. Values are opaque and never use a source locator.</summary>
public record RetainedProcessorDerivedChild(
    string MemberFingerprint,
    string SyntheticLocator,
    string StableSourceIdentity,
    string ContentSha256,
    string StoreRelativePath,
    long ByteLength,
    string Classification,
    int OriginKind,
    string Extension)
{
    // Compatibility projection for archive regressions; all values remain manifest-derived and opaque.
    public ArchiveMemberIdentity Identity => new(MemberFingerprint, SyntheticLocator, StableSourceIdentity);

    public static RetainedProcessorDerivedChild ArchiveMember(
        ArchiveMemberIdentity identity,
        string contentSha256,
        string storeRelativePath,
        long byteLength,
        string classification) => new(identity.MemberFingerprint, $"C:\\retained-archive-members\\{identity.MemberFingerprint}", identity.StableSourceIdentity,
            contentSha256, storeRelativePath, byteLength, classification, OriginKind: 1, Extension: ".txt");
}

/// <summary>Compatibility constructor for archive callers; new processors use the generic manifest.</summary>
public sealed record RetainedProcessorMember(
    ArchiveMemberIdentity Identity,
    string ContentSha256,
    string StoreRelativePath,
    long ByteLength,
    string Classification)
    : RetainedProcessorDerivedChild(Identity.MemberFingerprint, Identity.SyntheticLocator, Identity.StableSourceIdentity,
        ContentSha256, StoreRelativePath, ByteLength, Classification, OriginKind: 1, Extension: ".txt");

public sealed record RetainedProcessorCompletion(
    IReadOnlyList<RetainedProcessorDerivedChild> Members,
    string ReceiptFingerprint);

public sealed record RetainedProcessorMemberOutcome(
    string MemberFingerprint,
    long ByteLength,
    string Disposition,
    string ReasonCode);

public sealed record RetainedProcessorFailure(
    string OutcomeCode,
    IReadOnlyList<RetainedProcessorMemberOutcome> MemberOutcomes);

public sealed record RetainedCsharpCodeCompletionWriteResult(
    bool IsCommitted,
    bool IsReplay,
    string OutcomeCode,
    string? CompletionFingerprint);
