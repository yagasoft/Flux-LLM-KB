using FluxKnowledge.Domain.Common;

namespace FluxKnowledge.Domain.Pipeline;

public sealed record Artifact(
    Guid Id,
    PipelineRecordId PipelineRecordId,
    long SourceRevision,
    PipelineStage Stage,
    string ContentHash,
    string ContentType,
    DateTimeOffset CreatedAtUtc)
{
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
}
