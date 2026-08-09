using FluxKnowledge.Application.Contracts;
using FluxKnowledge.Domain.Sources;

namespace FluxKnowledge.Application.Ports;

public interface ISourceScanner
{
    ValueTask<SourceScanResult> ScanAsync(
        SourceRootConfiguration sourceRoot,
        SourceScanRequest scanRequest,
        CancellationToken cancellationToken);
}
