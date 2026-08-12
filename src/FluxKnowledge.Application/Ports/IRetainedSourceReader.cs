using FluxKnowledge.Application.Pipeline;
using FluxKnowledge.Domain.Sources;

namespace FluxKnowledge.Application.Ports;

/// <summary>Reads the app-owned, checksum-verified bytes for one immutable source revision.</summary>
public interface IRetainedSourceReader
{
    /// <summary>Validates the immutable private binding and physical length without buffering its bytes.</summary>
    async ValueTask<RetainedArtifactInspection> InspectAsync(
        SourceRevisionId sourceRevisionId,
        CancellationToken cancellationToken)
    {
        var retained = await ReadBytesAsync(sourceRevisionId, cancellationToken).ConfigureAwait(false);
        return new RetainedArtifactInspection(retained.SourceRevisionId, retained.ContentSha256, retained.ByteLength);
    }

    ValueTask<RetainedSourceBytes> ReadBytesAsync(
        SourceRevisionId sourceRevisionId,
        CancellationToken cancellationToken);

    ValueTask<Utf8FileSource> ReadUtf8Async(
        SourceRevisionId sourceRevisionId,
        CancellationToken cancellationToken);
}

/// <summary>Private retained-artifact metadata; it is never a public projection.</summary>
public sealed record RetainedArtifactInspection(
    SourceRevisionId SourceRevisionId,
    string ContentSha256,
    long ByteLength);

/// <summary>Checksum-verified immutable retained source bytes; no source-adapter path is exposed.</summary>
public sealed record RetainedSourceBytes(
    SourceRevisionId SourceRevisionId,
    byte[] Bytes,
    string ContentSha256,
    long ByteLength);
