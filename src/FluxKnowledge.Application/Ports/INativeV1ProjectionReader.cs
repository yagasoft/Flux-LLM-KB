using FluxKnowledge.Application.IntegrationV1;
using FluxKnowledge.Application.IntegrationV1.Code;
using FluxKnowledge.Application.IntegrationV1.Corpus;
using FluxKnowledge.Application.IntegrationV1.Operations;

namespace FluxKnowledge.Application.Ports;

/// <summary>
/// Boundary for the closed, retained-only native v1 projections and action preparation.
/// It deliberately has no path, URL or parser input.
/// </summary>
public interface INativeV1ProjectionReader
{
    ValueTask<object> ReadCorpusAsync(NativeCorpusQuery query, CancellationToken cancellationToken);
    ValueTask<object> ReadCodeAsync(NativeCodeQuery query, CancellationToken cancellationToken);
    ValueTask<object> ReadStatusAsync(NativeOperationsStatus query, CancellationToken cancellationToken);
    ValueTask<object> ReadAuditAsync(NativeAuditQuery query, CancellationToken cancellationToken);

}
