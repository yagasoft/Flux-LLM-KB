using FluxKnowledge.Domain.Sources;

namespace FluxKnowledge.Application.Ports;

public sealed record ClaimedSourceWatchBatch(
    SourceRootId SourceRootId,
    DateTimeOffset FirstSignalAtUtc,
    DateTimeOffset LastSignalAtUtc,
    int SignalCount,
    long DebounceGeneration,
    string LeaseOwner,
    long LeaseGeneration);

public interface ISourceRootWatchStore
{
    ValueTask<IReadOnlyList<SourceRootConfiguration>> ReadEnabledRootsAsync(CancellationToken cancellationToken);
    ValueTask RecordSignalAsync(SourceWatchSignal signal, CancellationToken cancellationToken);

    ValueTask<ClaimedSourceWatchBatch?> ClaimDueBatchAsync(
        DateTimeOffset nowUtc,
        string leaseOwner,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken);

    ValueTask ReleaseScanAsync(ClaimedSourceWatchBatch batch, CancellationToken cancellationToken);
}
