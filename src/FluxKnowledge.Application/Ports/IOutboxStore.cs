using FluxKnowledge.Application.Workers;
using FluxKnowledge.Domain.Jobs;

namespace FluxKnowledge.Application.Ports;

public interface IOutboxStore
{
    ValueTask EnqueueAsync(DispatchMessage message, CancellationToken cancellationToken);

    ValueTask<ClaimedDispatchMessage?> ClaimNextDueAsync(
        string leaseOwner,
        DateTimeOffset nowUtc,
        TimeSpan leaseDuration,
        IReadOnlyCollection<string> registeredOperations,
        CancellationToken cancellationToken);

    ValueTask ReleaseAsync(
        ClaimedDispatchMessage claim,
        DateTimeOffset dueAtUtc,
        CancellationToken cancellationToken);
}
