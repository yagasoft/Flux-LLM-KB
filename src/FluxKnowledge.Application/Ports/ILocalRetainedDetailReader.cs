using FluxKnowledge.Application.Sources;
using FluxKnowledge.Application.Visibility;

namespace FluxKnowledge.Application.Ports;

/// <summary>Reads trusted-local retained branch detail without widening public corpus or mutation stores.</summary>
public interface ILocalRetainedDetailReader
{
    ValueTask<LocalRetainedDetailProjection?> ReadAsync(Guid branchId, CancellationToken cancellationToken);

    ValueTask<LocalDisclosureResult> ReadExcerptAsync(Guid branchId, CancellationToken cancellationToken);
}
