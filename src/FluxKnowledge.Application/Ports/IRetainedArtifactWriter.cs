using FluxKnowledge.Domain.Sources;

namespace FluxKnowledge.Application.Ports;

/// <summary>Writes a retained processor's child artifact to the storage root bound to its parent revision.</summary>
public interface IRetainedArtifactWriter
{
    ValueTask<RetainedArtifactWriteReceipt> WriteAsync(
        SourceRevisionId parentSourceRevisionId,
        Stream content,
        long maximumByteLength,
        CancellationToken cancellationToken);
}

public sealed record RetainedArtifactWriteReceipt(
    string ContentSha256,
    string StoreRelativePath,
    long ByteLength,
    bool IsUtf8Text,
    bool IsNestedArchive);
