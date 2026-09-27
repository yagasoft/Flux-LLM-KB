using FluxKnowledge.Domain.Gpu;

namespace FluxKnowledge.Application.Gpu;

/// <summary>Recovers opaque ownership after proven process exit; private work is never replayed.</summary>
public sealed class GpuInteractiveOwnerRecovery(IGpuInteractiveRequestStore requests, IGpuSchedulerStore scheduler,
    IGpuExecutorDispatchStore dispatches, IGpuInteractiveOwnerProbe probe, IGpuSchedulerWakeSignal wakeSignal, TimeProvider clock)
{
    public async ValueTask<int> RecoverAsync(CancellationToken cancellationToken)
    {
        var recovered = 0;
        foreach (var work in await requests.ReadInteractiveRecoveryAsync(cancellationToken).ConfigureAwait(false))
        {
            if (probe.Observe(work.Owner) != GpuInteractiveOwnerObservation.Exited) continue;
            if (work.Handle is null)
            {
                if (await requests.CancelLostInteractiveReadyAsync(work.RequestId, work.Owner, cancellationToken).ConfigureAwait(false)) recovered++;
                continue;
            }
            // A stale rowversion may mean the native owner finished concurrently; reread next round.
            if (work.Reservation is not null && !(await scheduler.MarkCapacityUncertainAsync(
                Guid.NewGuid(), work.Reservation, cancellationToken).ConfigureAwait(false)).Committed) continue;
            var outcomeEvidence = new GpuExecutorTrustedEvidence(Guid.NewGuid(), work.Handle, "retrieval-owner-process-exit",
                clock.GetUtcNow(), GpuExecutorEvidenceClass.TaskOutcomeUncertainConfirmed);
            var capacityEvidence = outcomeEvidence with { OperationId = Guid.NewGuid(), EvidenceClass = GpuExecutorEvidenceClass.CapacityReleaseConfirmed };
            if (!(await dispatches.RecordTrustedEvidenceAsync(outcomeEvidence, cancellationToken).ConfigureAwait(false)).Committed ||
                !(await dispatches.RecordTrustedEvidenceAsync(capacityEvidence, cancellationToken).ConfigureAwait(false)).Committed) continue;
            var outcome = await scheduler.ReconcileTaskOutcomeAsync(Guid.NewGuid(),
                new(work.Handle, outcomeEvidence.OperationId, work.RequestId), cancellationToken).ConfigureAwait(false);
            var capacity = await scheduler.ReconcileCapacityAsync(Guid.NewGuid(),
                new(work.Handle, capacityEvidence.OperationId), cancellationToken).ConfigureAwait(false);
            if (outcome.Committed || capacity.Committed) recovered++;
        }
        if (recovered > 0) wakeSignal.Notify(GpuSchedulerWakeReason.Reconciliation | GpuSchedulerWakeReason.CapacityReleased);
        return recovered;
    }
}
