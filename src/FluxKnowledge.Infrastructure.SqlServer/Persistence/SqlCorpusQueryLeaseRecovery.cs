using FluxKnowledge.Application.Gpu;
using Microsoft.EntityFrameworkCore;

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence;

internal static class SqlCorpusQueryLeaseRecovery
{
    // Caller holds Exclusive cleanup ownership. A lost SQL session is insufficient:
    // its native owner may still be running and using captured data/files.
    internal static async Task<bool> TryDrainAbandonedAsync(FluxKnowledgeDbContext context,
        IGpuInteractiveOwnerProbe? probe, CancellationToken cancellationToken)
    {
        var registrations = await context.CorpusQueryLeases.AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false);
        if (probe is not null)
        {
            foreach (var registration in registrations)
            {
                var owner = new GpuInteractiveOwnerIdentity(registration.OwnerProcessId, registration.OwnerStartedAtUtc,
                    registration.OwnerMachineFingerprint);
                if (probe.Observe(owner) != GpuInteractiveOwnerObservation.Exited) continue;
                await context.CorpusQueryLeases.Where(value => value.Id == registration.Id &&
                    value.OwnerInstanceId == registration.OwnerInstanceId && value.OwnerProcessId == registration.OwnerProcessId &&
                    value.OwnerStartedAtUtc == registration.OwnerStartedAtUtc && value.OwnerMachineFingerprint == registration.OwnerMachineFingerprint)
                    .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        return !await context.CorpusQueryLeases.AnyAsync(cancellationToken).ConfigureAwait(false);
    }
}
