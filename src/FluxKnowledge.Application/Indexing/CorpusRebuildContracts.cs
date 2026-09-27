using FluxKnowledge.Application.Ports;

namespace FluxKnowledge.Application.Indexing;

public sealed record CorpusRebuildInput(Guid PipelineRecordId, long SourceRevision, Guid CanonicalArtifactId,
    string ContentHash, string MetadataHash, string SourceBindingHash, Guid EmbeddingJobId, Guid DispatchMessageId);

public sealed record CorpusRebuildArtifact(Guid Id, int Stage, string ContentHash, string ContentType);

public sealed record CorpusRebuildGeneration(Guid Id, string IndexPath, string MetadataChecksum,
    string ModelFingerprint, int Dimensions, long VectorCount);

public sealed record CorpusRebuildPlan(Guid OperationId, string DatabaseServer, string DatabaseName, CorpusPublicationStamp PreviousStamp,
    Guid TargetEpoch, EmbeddingProfile Profile, string PassagePolicyFingerprint,
    IReadOnlyList<CorpusRebuildInput> Inputs, IReadOnlyList<CorpusRebuildArtifact> ProjectionArtifacts,
    IReadOnlyList<CorpusRebuildGeneration> Generations, IReadOnlyList<Guid> SettledEmbeddingRequestIds,
    long ChunkCount, long VectorCount, long MembershipCount,
    string ManifestHash);

public sealed class CorpusRebuildRefusalException(string code) : InvalidOperationException(code);

public sealed record CorpusRebuildReceipt(Guid OperationId, Guid TargetEpoch, string ManifestHash, bool AlreadyCommitted);

public sealed record CorpusRebuildPreparation(Guid PipelineRecordId, Guid? EmbeddingJobId,
    Guid? DispatchMessageId, int PassageCount, bool AlreadyPrepared);
