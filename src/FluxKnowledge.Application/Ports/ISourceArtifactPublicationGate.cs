namespace FluxKnowledge.Application.Ports;

/// <summary>
/// Serialises one content-addressed blob's physical publication and reference registration
/// with deletion of that same blob. The lease is process-spanning and never names a path.
/// </summary>
public interface ISourceArtifactPublicationGate
{
    ValueTask<ISourceArtifactPublicationLease> AcquireSharedAsync(
        string contentSha256,
        CancellationToken cancellationToken);

    ValueTask<ISourceArtifactPublicationLease?> TryAcquireExclusiveAsync(
        string contentSha256,
        CancellationToken cancellationToken);

    /// <summary>Acquires the longest available prefix without waiting on a conflicting publisher.</summary>
    ValueTask<ISourceArtifactPublicationBatchLease?> TryAcquireExclusiveBatchAsync(
        IReadOnlyList<string> orderedContentHashes,
        CancellationToken cancellationToken);
}

public interface ISourceArtifactPublicationLease : IAsyncDisposable;

public interface ISourceArtifactPublicationBatchLease : IAsyncDisposable
{
    int AcquiredCount { get; }
    ValueTask<bool> IsHeldAsync(string contentSha256, CancellationToken cancellationToken);
}
