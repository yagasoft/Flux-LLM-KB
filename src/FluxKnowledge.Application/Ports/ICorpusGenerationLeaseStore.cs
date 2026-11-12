using FluxKnowledge.Application.Gpu;

namespace FluxKnowledge.Application.Ports;

/// <summary>Pins a stamped SQL generation against destructive cleanup before query embedding.</summary>
public interface ICorpusGenerationLease : IAsyncDisposable
{
    Guid LeaseId { get; }
    IndexGenerationDescriptor Generation { get; }
    ValueTask<bool> IsCurrentAsync(CancellationToken cancellationToken);
    /// <summary>Whether the pinned generation remains readable while unrelated publication advances.</summary>
    ValueTask<bool> IsReadableAsync(CancellationToken cancellationToken) => IsCurrentAsync(cancellationToken);
}

public interface ICorpusGenerationLeaseStore
{
    ValueTask<ICorpusGenerationLease?> TryAcquireAsync(Guid ownerInstanceId,
        GpuInteractiveOwnerIdentity owner, string modelFingerprint, int dimensions,
        CancellationToken cancellationToken);
    ValueTask<ICorpusGenerationLease?> TryAcquireReadableAsync(Guid ownerInstanceId,
        GpuInteractiveOwnerIdentity owner, string modelFingerprint, int dimensions,
        CancellationToken cancellationToken) => TryAcquireAsync(ownerInstanceId, owner, modelFingerprint, dimensions, cancellationToken);
}
