using FluxKnowledge.Application.Contracts;

namespace FluxKnowledge.Application.Ports;

public interface ICorpusProjectionReader
{
    ValueTask<CorpusPage> ReadPageAsync(CorpusQuery query, CancellationToken cancellationToken);
    ValueTask<IReadOnlyList<CorpusFolder>> ReadFoldersAsync(Guid sourceRootId, string? folder, CancellationToken cancellationToken);
    ValueTask<CorpusEntryDetail?> ReadDetailAsync(Guid pipelineRecordId, CancellationToken cancellationToken);
}

public interface IOperatorEventProjectionReader
{
    ValueTask<OperatorEventPage> ReadPageAsync(OperatorEventQuery query, CancellationToken cancellationToken);
}
