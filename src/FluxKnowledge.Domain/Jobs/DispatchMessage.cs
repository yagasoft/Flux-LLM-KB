using FluxKnowledge.Domain.Common;
using FluxKnowledge.Domain.Pipeline;

namespace FluxKnowledge.Domain.Jobs;

public sealed record DispatchMessage
{
    public DispatchMessageId Id { get; private init; }

    public PipelineRecordId PipelineRecordId { get; private init; }

    public long SourceRevision { get; private init; }

    public PipelineStage Stage { get; private init; }

    public string Operation { get; private init; }

    public long DispatchGeneration { get; private init; }

    public string IdempotencyKey { get; private init; }

    public DateTimeOffset DueAtUtc { get; private init; }

    public DateTimeOffset CreatedAtUtc { get; private init; }

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

    private DispatchMessage(
        DispatchMessageId id,
        PipelineRecordId pipelineRecordId,
        long sourceRevision,
        PipelineStage stage,
        string operation,
        long dispatchGeneration,
        string idempotencyKey,
        DateTimeOffset dueAtUtc,
        DateTimeOffset createdAtUtc)
    {
        Id = id;
        PipelineRecordId = pipelineRecordId;
        SourceRevision = sourceRevision;
        Stage = stage;
        Operation = operation;
        DispatchGeneration = dispatchGeneration;
        IdempotencyKey = idempotencyKey;
        DueAtUtc = dueAtUtc;
        CreatedAtUtc = createdAtUtc;
    }
}
