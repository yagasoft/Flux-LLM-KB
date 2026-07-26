using FluxKnowledge.Domain.Common;

namespace FluxKnowledge.Domain.Pipeline;

public sealed record Artifact
{
    public Guid Id { get; private init; }

    public PipelineRecordId PipelineRecordId { get; private init; }

    public long SourceRevision { get; private init; }

    public PipelineStage Stage { get; private init; }

    public string ContentHash { get; private init; }

    public string ContentType { get; private init; }

    public DateTimeOffset CreatedAtUtc { get; private init; }

    public static Artifact Create(
        PipelineRecordId pipelineRecordId,
        long sourceRevision,
        PipelineStage stage,
        string contentHash,
        string contentType) => new(
            Guid.NewGuid(),
            pipelineRecordId,
            sourceRevision,
            stage,
            contentHash,
            contentType,
            DateTimeOffset.UtcNow);

    private Artifact(
        Guid id,
        PipelineRecordId pipelineRecordId,
        long sourceRevision,
        PipelineStage stage,
        string contentHash,
        string contentType,
        DateTimeOffset createdAtUtc)
    {
        Id = id;
        PipelineRecordId = pipelineRecordId;
        SourceRevision = sourceRevision;
        Stage = stage;
        ContentHash = contentHash;
        ContentType = contentType;
        CreatedAtUtc = createdAtUtc;
    }
}
