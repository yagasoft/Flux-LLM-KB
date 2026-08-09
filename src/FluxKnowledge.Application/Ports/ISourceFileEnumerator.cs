using FluxKnowledge.Application.Contracts;
using FluxKnowledge.Application.Sources;
using FluxKnowledge.Domain.Sources;

namespace FluxKnowledge.Application.Ports;

public sealed record SourceDiscoveredFile(
    string CanonicalPath,
    string RelativePath,
    string StableSourceIdentity,
    byte[] ClassificationBuffer,
    bool HasFullBoundedBuffer,
    string ContentSha256,
    long ByteLength,
    DateTimeOffset LastWriteAtUtc,
    SourceClassificationResult Classification);

public sealed record SourceEnumerationEvidence(string Kind, string RelativePath, string Detail);

public interface ISourceFileEnumerator
{
    IReadOnlyList<SourceEnumerationEvidence> LastEvidence { get; }

    IAsyncEnumerable<SourceDiscoveredFile> EnumerateAsync(
        SourceRootConfiguration sourceRoot,
        CancellationToken cancellationToken);
}
