using FluxKnowledge.Application.Gpu;

namespace FluxKnowledge.Application.Ports;

/// <summary>Pins a stamped SQL generation against destructive cleanup before query embedding.</summary>
public interface ICorpusGenerationLease : IAsyncDisposable
{
    Guid LeaseId { get; }
    IndexGenerationDescriptor Generation { get; }
    ValueTask<bool> IsCurrentAsync(CancellationToken cancellationToken);
}

public interface ICorpusGenerationLeaseStore
{
    ValueTask<ICorpusGenerationLease?> TryAcquireAsync(Guid ownerInstanceId,
        GpuInteractiveOwnerIdentity owner, string modelFingerprint, int dimensions,
        CancellationToken cancellationToken);
}
