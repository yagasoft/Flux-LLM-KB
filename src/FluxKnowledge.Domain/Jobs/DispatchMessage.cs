using FluxKnowledge.Domain.Common;
using FluxKnowledge.Domain.Pipeline;

namespace FluxKnowledge.Domain.Jobs;

public sealed record DispatchMessage(
    DispatchMessageId Id,
    PipelineRecordId PipelineRecordId,
    long SourceRevision,
    PipelineStage Stage,
    string Operation,
    long DispatchGeneration,
    string IdempotencyKey,
    DateTimeOffset DueAtUtc,
    DateTimeOffset CreatedAtUtc)
{
    public static DispatchMessage Create(
        PipelineRecordId pipelineRecordId,
        long sourceRevision,
        PipelineStage stage,
        string operation,
        long dispatchGeneration,
        string idempotencyKey,
        DateTimeOffset? dueAtUtc = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);
        if (sourceRevision <= 0 || dispatchGeneration < 0)
        {
            throw new DomainInvariantException("Source revision and dispatch generation must be non-negative valid values.");
        }

        var now = DateTimeOffset.UtcNow;
        return new DispatchMessage(
            DispatchMessageId.New(), pipelineRecordId, sourceRevision, stage, operation,
            dispatchGeneration, idempotencyKey, dueAtUtc ?? now, now);
    }
}
