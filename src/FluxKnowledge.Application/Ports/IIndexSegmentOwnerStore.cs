using FluxKnowledge.Application.Indexing;

namespace FluxKnowledge.Application.Ports;

/// <summary>Durable native ownership survives loss of a SQL session; only disposal or proved process exit releases it.</summary>
public interface IIndexSegmentOwnerStore
{
    ValueTask<Guid> RegisterAsync(IndexSegmentDescriptor segment, Guid instanceId, CancellationToken cancellationToken);
    ValueTask OpenedAsync(Guid ownerId, CancellationToken cancellationToken);
    ValueTask ReleasedAfterDisposalAsync(Guid ownerId, CancellationToken cancellationToken);
    ValueTask<IReadOnlySet<string>> ReadRetiringAsync(IReadOnlyList<string> segmentIds, CancellationToken cancellationToken);
}
