using FluxKnowledge.Application.Contracts;
using FluxKnowledge.Domain.Sources;

namespace FluxKnowledge.Application.Ports;

public interface ISourceRootStore
{
    ValueTask<SourceRootReceipt> CreateAsync(
        SourceRootCreateRequest request,
        ScanStartIntent startIntent,
        CancellationToken cancellationToken);

    ValueTask<bool> ReleaseAsync(
        SourceRootId sourceRootId,
        SourceScanRequestId sourceScanRequestId,
        string actor,
        CancellationToken cancellationToken);
}
