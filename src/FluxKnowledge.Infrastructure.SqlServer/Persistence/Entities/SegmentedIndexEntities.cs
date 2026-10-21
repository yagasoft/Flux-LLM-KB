namespace FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;

public sealed class IndexSegmentEntity
{
    public string Id { get; set; } = "";
    public string ModelFingerprint { get; set; } = "";
    public int Dimensions { get; set; }
    public string NativeFormat { get; set; } = "";
    public string DirectoryPath { get; set; } = "";
    public string MembershipChecksum { get; set; } = "";
    public string FileChecksum { get; set; } = "";
    public long FileLength { get; set; }
    public long VectorCount { get; set; }
    // 0=placed, 1=sealed, 2=retirement requested, 3=reclaimed.
    public int State { get; set; }
    public Guid? ProducingCandidateId { get; set; }
    public Guid? RetirementOperationId { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset? SealedAtUtc { get; set; }
    public DateTimeOffset? ReclaimedAtUtc { get; set; }
    public byte[] RowVersion { get; set; } = [];
}

public sealed class IndexSegmentVectorEntity
{
    public string SegmentId { get; set; } = "";
    public long VectorId { get; set; }
}

public sealed class IndexGenerationSegmentEntity
{
    public Guid GenerationId { get; set; }
    public string SegmentId { get; set; } = "";
    public int Ordinal { get; set; }
}

public sealed class CurrentIndexMemberEntity
{
    public Guid CorpusEpoch { get; set; }
    public long VectorId { get; set; }
    public string SegmentId { get; set; } = "";
    public Guid SourceIdentityId { get; set; }
    public Guid? OwnerSourceRevisionId { get; set; }
    public Guid? SourceRootId { get; set; }
}

public sealed class IndexProjectionChangeEntity
{
    public Guid CorpusEpoch { get; set; }
    public long CorpusVersion { get; set; }
    // 1=source identity, 2=owner revision, 3=whole root, 4=unknown/full repair required.
    public int ScopeKind { get; set; }
    public Guid ScopeId { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
}

public sealed class IndexPublicationCandidateEntity
{
    public Guid Id { get; set; }
    // 0=ordinary delta, 1=explicit full format2 build, 2=explicit full format1 rollback.
    public int BuildPurpose { get; set; }
    public int ExpectedIndexFormat { get; set; } = 2;
    public Guid CorpusEpoch { get; set; }
    public long ExpectedCorpusVersion { get; set; }
    public long PublicationCorpusVersion { get; set; }
    public Guid? BaseGenerationId { get; set; }
    public Guid? DraftGenerationId { get; set; }
    public Guid? OwnerJobId { get; set; }
    // Terminal provenance survives removal of the source/job graph without retaining those FKs.
    public Guid? RetiredOwnerJobId { get; set; }
    public Guid? RetiredDraftGenerationId { get; set; }
    public long OwnerLeaseGeneration { get; set; }
    public string? OwnerLeaseOwner { get; set; }
    public Guid? ResultGenerationId { get; set; }
    public string AffectedScopesJson { get; set; } = "[]";
    public string RepackedSegmentsJson { get; set; } = "[]";
    public string BeforeChecksum { get; set; } = "";
    public string AfterChecksum { get; set; } = "";
    public long BeforeCount { get; set; }
    public long AfterCount { get; set; }
    public long LastAppendOrdinal { get; set; } = -1;
    public string LastPageChecksum { get; set; } = new('0', 64);
    public bool WorklistComplete { get; set; }
    public Guid OwnerInstanceId { get; set; }
    public int OwnerProcessId { get; set; }
    public DateTimeOffset OwnerStartedAtUtc { get; set; }
    public string OwnerMachineFingerprint { get; set; } = "";
    // 0=building/pins held, 1=sealed, 2=committed, 3=abandoned after ownership proof.
    public int State { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset? CompletedAtUtc { get; set; }
    public byte[] RowVersion { get; set; } = [];
}

/// <summary>Untrusted preview metadata until sealed. Deliberately has no FK to preview-locked vector/owner rows.</summary>
public sealed class IndexCandidateMemberEntity
{
    public Guid CandidateId { get; set; }
    public int Phase { get; set; }
    public long VectorId { get; set; }
    public string PayloadChecksum { get; set; } = "";
    public Guid SourceIdentityId { get; set; }
    public Guid? OwnerSourceRevisionId { get; set; }
    public Guid? SourceRootId { get; set; }
}

public sealed class IndexSegmentOwnerEntity
{
    public Guid Id { get; set; }
    public string SegmentId { get; set; } = "";
    public Guid OwnerInstanceId { get; set; }
    public int OwnerProcessId { get; set; }
    public DateTimeOffset OwnerStartedAtUtc { get; set; }
    public string OwnerMachineFingerprint { get; set; } = "";
    // Opening/open/disposing/uncertain retain ownership until disposal or proved process exit.
    public int State { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public byte[] RowVersion { get; set; } = [];
}
