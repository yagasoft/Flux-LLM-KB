using FluxKnowledge.Application.Contracts;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Workers;

namespace FluxKnowledge.Application.Pipeline;

public interface IStageTransitionStore
{
    ValueTask ValidateRepositorySourceAsync(StageWorkItem work, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    ValueTask<StageTransitionResult> TransitionAsync(
        StageTransitionRequest request,
        CancellationToken cancellationToken);

    ValueTask FailAsync(
        StageFailureRequest request,
        CancellationToken cancellationToken);

    ValueTask RetryAsync(StageRetryRequest request, CancellationToken cancellationToken) =>
        throw new NotSupportedException("This store does not support durable stage retries.");
}

public interface IStageTransitionFailureInjector
{
    ValueTask AfterArtifactWrittenAsync(CancellationToken cancellationToken);
}

public sealed class StageTransitionService(
    IStageTransitionStore store,
    IStatusEventPublisher statusPublisher,
    IOutboxWakeSignal wakeSignal,
    TimeProvider timeProvider)
{
    public ValueTask ValidateRepositorySourceAsync(StageWorkItem work, CancellationToken cancellationToken) =>
        store.ValidateRepositorySourceAsync(work, cancellationToken);
    public async ValueTask RetryAsync(StageRetryRequest request, CancellationToken cancellationToken)
    {
        await store.RetryAsync(request, cancellationToken).ConfigureAwait(false);
        await statusPublisher.PublishAsync(new StatusChanged(request.CurrentJob.PipelineRecordId,
            "pipeline", timeProvider.GetUtcNow()), cancellationToken).ConfigureAwait(false);
        wakeSignal.Notify();
    }

    public async ValueTask<StageTransitionResult> TransitionAsync(
        StageTransitionRequest request,
        CancellationToken cancellationToken)
    {
        var result = await store.TransitionAsync(request, cancellationToken)
            .ConfigureAwait(false);

        await statusPublisher.PublishAsync(
                new StatusChanged(
                    request.CurrentJob.PipelineRecordId,
                    "pipeline",
                    timeProvider.GetUtcNow()),
                cancellationToken)
            .ConfigureAwait(false);
        if (result.NextDispatchMessageId is not null && !result.ExistingTransition)
        {
            wakeSignal.Notify();
        }

        return result;
    }

    public async ValueTask FailAsync(
        StageFailureRequest request,
        CancellationToken cancellationToken)
    {
        await store.FailAsync(request, cancellationToken).ConfigureAwait(false);
        await statusPublisher.PublishAsync(
                new StatusChanged(
                    request.CurrentJob.PipelineRecordId,
                    "pipeline",
                    timeProvider.GetUtcNow()),
                cancellationToken)
            .ConfigureAwait(false);
    }
}
