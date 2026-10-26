namespace FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;

public sealed class SourceVolumeJournalCheckpointEntity
{
    public string VolumeIdentity { get; set; } = "";
    public string JournalId { get; set; } = "";
    public long NextUsn { get; set; }
    public long RegistrationGeneration { get; set; }
    public string? LeaseOwner { get; set; }
    public long LeaseGeneration { get; set; }
    public DateTimeOffset? LeaseExpiresAtUtc { get; set; }
    public DateTimeOffset? DueAtUtc { get; set; }
    public string? LastBatchFingerprint { get; set; }
    public DateTimeOffset? UnavailableSinceUtc { get; set; }
    public string? LastFailureCode { get; set; }
    public byte[] RowVersion { get; set; } = [];
}

public sealed class SourceRootScanStateEntity
{
    public Guid SourceRootId { get; set; }
    public string VolumeIdentity { get; set; } = "";
    public string PolicyFingerprint { get; set; } = "";
    public long BaselineEpoch { get; set; }
    public bool RecoveryRequired { get; set; }
    public bool RecoveryInProgress { get; set; }
    public long RecoveryGeneration { get; set; }
    public bool NamespaceComplete { get; set; }
    public long NamespaceGeneration { get; set; }
    public string Mode { get; set; } = "BaselineRequired";
    public string? Reason { get; set; }
    public long BaselineStartUsn { get; set; }
    public long CoveredUsn { get; set; }
    public long Generation { get; set; }
    public long? UnresolvedThroughUsn { get; set; }
    public DateTimeOffset? LastReconciliationAtUtc { get; set; }
    public DateTimeOffset? LastBaselineAtUtc { get; set; }
    public DateTimeOffset? LastVerifiedAtUtc { get; set; }
    public string VerificationPolicyJson { get; set; } = "{}";
    public long VerificationPolicyRevision { get; set; } = 1;
    public bool VerificationPaused { get; set; }
    public string? LastStatisticsJson { get; set; }
    public string? GitInventoryGeneration { get; set; }
    public string? DeferredEvidenceJson { get; set; }
    public string? SealProfileId { get; set; }
    public byte[] RowVersion { get; set; } = [];
}

public sealed class SourceInventoryEntryEntity
{
    public Guid Id { get; set; }
    public Guid SourceRootId { get; set; }
    public string RelativePath { get; set; } = "";
    public string PathFingerprint { get; set; } = "";
    public string FileId { get; set; } = "";
    public string ParentId { get; set; } = "";
    public bool IsDirectory { get; set; }
    public bool IsAncestor { get; set; }
    public long BaselineEpoch { get; set; }
    public Guid? SourceRevisionId { get; set; }
    public Guid? SourceArtifactId { get; set; }
    public string? ObservationJson { get; set; }
    public DateTimeOffset? LastVerifiedAtUtc { get; set; }
    public long? LatestJournalUsn { get; set; }
    public long? LatestJournalReason { get; set; }
    public bool IsSealed { get; set; }
    public string? SealProfileId { get; set; }
    public string? SealFailureCode { get; set; }
    public DateTimeOffset? NextForcedCheckAtUtc { get; set; }
    public byte[] RowVersion { get; set; } = [];
}

public sealed class SourcePendingChangeEntity
{
    public Guid Id { get; set; }
    public Guid SourceRootId { get; set; }
    public string RelativePath { get; set; } = "";
    public string PathFingerprint { get; set; } = "";
    public long ReasonFlags { get; set; }
    public bool IsSubtree { get; set; }
    public long Generation { get; set; }
    public long? RetryGeneration { get; set; }
    public long FirstUsn { get; set; }
    public long LastUsn { get; set; }
    public long? LastContentUsn { get; set; }
    public long? LastUnjournaledGeneration { get; set; }
    public DateTimeOffset? FirstObservedAtUtc { get; set; }
    public DateTimeOffset? NotBeforeUtc { get; set; }
    public byte[] RowVersion { get; set; } = [];
}

public sealed class SourceVerificationRunEntity
{
    public Guid Id { get; set; }
    public Guid SourceRootId { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset? CompletedAtUtc { get; set; }
    public string State { get; set; } = "Pending";
    public long BytesRead { get; set; }
    public bool Active { get; set; }
    public bool Manual { get; set; }
    public string AuthorityJson { get; set; } = "{}";
    public string OriginalPolicyJson { get; set; } = "{}";
    public DateTimeOffset? ScheduledForUtc { get; set; }
    public DateTimeOffset? NextBatchAtUtc { get; set; }
    public long SourceBytesRead { get; set; }
    public long RetainedBytesRead { get; set; }
    public bool ReadCountsIncomplete { get; set; }
    public string? Reason { get; set; }
    public byte[] RowVersion { get; set; } = [];
}

public sealed class SourceVerificationItemEntity
{
    public Guid Id { get; set; }
    public Guid RunId { get; set; }
    public Guid InventoryEntryId { get; set; }
    public Guid? ExpectedRevisionId { get; set; }
    public string State { get; set; } = "Pending";
    public string? Reason { get; set; }
    public DateTimeOffset? AttemptedAtUtc { get; set; }
    public string BindingJson { get; set; } = "{}";
    public string? CandidateBindingJson { get; set; }
    public string? ReceiptJson { get; set; }
    public string? ReceiptSha256 { get; set; }
    public long? ReceiptByteLength { get; set; }
    public DateTimeOffset? CompletedAtUtc { get; set; }
    public int AttemptCount { get; set; }
    public byte[] RowVersion { get; set; } = [];
}
