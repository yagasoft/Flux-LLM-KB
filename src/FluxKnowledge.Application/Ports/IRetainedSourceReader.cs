using FluxKnowledge.Application.Pipeline;
using FluxKnowledge.Domain.Sources;

namespace FluxKnowledge.Application.Ports;

/// <summary>Reads the app-owned, checksum-verified bytes for one immutable source revision.</summary>
public interface IRetainedSourceReader
{
    ValueTask<Utf8FileSource> ReadUtf8Async(
        SourceRevisionId sourceRevisionId,
        CancellationToken cancellationToken);
}
