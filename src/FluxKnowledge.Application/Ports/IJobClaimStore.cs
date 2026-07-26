using FluxKnowledge.Domain.Jobs;

namespace FluxKnowledge.Application.Ports;

public interface IJobClaimStore
{
    ValueTask<Job?> ClaimWorkerAsync(
        string leaseOwner,
        DateTimeOffset nowUtc,
        DateTimeOffset leaseExpiresAtUtc,
        CancellationToken cancellationToken);

    ValueTask<Job?> ClaimGpuAsync(
        string leaseOwner,
        DateTimeOffset nowUtc,
        DateTimeOffset leaseExpiresAtUtc,
        CancellationToken cancellationToken);
}
