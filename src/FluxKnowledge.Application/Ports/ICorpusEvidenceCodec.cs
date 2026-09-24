namespace FluxKnowledge.Application.Ports;

public sealed record CorpusEvidenceBinding(
    int Version,
    Guid? RootId,
    Guid? OwnerSourceRevisionId,
    string SourceIdentityHash,
    Guid PipelineRecordId,
    long PipelineRecordRevision,
    Guid ArtifactId,
    string ArtifactHash,
    long ChunkId,
    string ChunkHash,
    int CitedStart,
    int CitedLength);

public interface ICorpusEvidenceCodec
{
    string Encode(CorpusEvidenceBinding binding);
    CorpusEvidenceBinding Decode(string reference);
}
