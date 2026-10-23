using FluxKnowledge.Application.Contracts;
using FluxKnowledge.Domain.Sources;

namespace FluxKnowledge.Application.Ports;

/// <summary>A transient discovery read, not a reusable or serializable publication proof.</summary>
public interface ISourceFileCapture : IAsyncDisposable
{
    SourceDiscoveredFile Snapshot { get; }
}

public interface ICapturingSourceFileEnumerator : ISourceFileEnumerator
{
    /// <summary>
    /// Yields one owned capture at a time. Finish retention and reference convergence
    /// before advancing: advancing or disposing the iterator disposes its current capture.
    /// </summary>
    IAsyncEnumerable<ISourceFileCapture> EnumerateCapturedAsync(
        SourceRootConfiguration sourceRoot,
        SourceScanOwnership ownership,
        CancellationToken cancellationToken);
}

public interface ICapturedSourceArtifactStore : ISourceArtifactStore
{
    /// <summary>Consumes a capture once; a successful receipt owns its remaining lifetime.</summary>
    ValueTask<SourceArtifactReceipt> PutCapturedFileAsync(
        ISourceFileCapture capture,
        SourceArtifactMetadata metadata,
        CancellationToken cancellationToken);
}
