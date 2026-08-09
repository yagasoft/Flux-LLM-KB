using FluxKnowledge.Application.Contracts;

namespace FluxKnowledge.Application.Ports;

public interface ISourceArtifactStore
{
    ValueTask<SourceArtifactReceipt> PutFileAsync(
        SourceDiscoveredFile snapshot,
        SourceArtifactMetadata metadata,
        CancellationToken cancellationToken);

    ValueTask<SourceArtifactReceipt> PutAsync(
        ReadOnlyMemory<byte> content,
        SourceArtifactMetadata metadata,
        CancellationToken cancellationToken);
}

public sealed class SourceSnapshotChangedException(string message) : IOException(message);
