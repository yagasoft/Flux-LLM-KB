using FluxKnowledge.Domain.Common;
using FluxKnowledge.Domain.Pipeline;

namespace FluxKnowledge.Application.Workers;

public sealed record ClaimedDispatchMessage(
    DispatchMessageId DispatchMessageId,
    PipelineRecordId PipelineRecordId,
    long SourceRevision,
    PipelineStage Stage,
    string Operation,
    long DispatchGeneration,
    string IdempotencyKey,
    DateTimeOffset DueAtUtc,
    string LeaseOwner,
    DateTimeOffset LeaseExpiresAtUtc,
    long LeaseGeneration);

public interface IOutboxPump
{
    ValueTask<int> PumpOnceAsync(CancellationToken cancellationToken);
}

public interface IOutboxWakeSignal
{
    void Notify();

    ValueTask WaitAsync(CancellationToken cancellationToken);
}
