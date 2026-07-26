using FluxKnowledge.Domain.Common;
using FluxKnowledge.Domain.Pipeline;

namespace FluxKnowledge.Application.Ports;

public interface IPipelineStore
{
    ValueTask<PipelineRecord?> FindBySourceRevisionAsync(
        SourceIdentityId sourceIdentityId,
        long revision,
        CancellationToken cancellationToken);

    ValueTask SaveAsync(PipelineRecord record, CancellationToken cancellationToken);
}
