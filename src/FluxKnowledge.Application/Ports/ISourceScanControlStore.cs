using FluxKnowledge.Application.Contracts;
using FluxKnowledge.Domain.Sources;

namespace FluxKnowledge.Application.Ports;

public sealed record ClaimedSourceScan(
    Guid ControlJobId,
    string LeaseOwner,
    long LeaseGeneration,
    SourceRootConfiguration SourceRoot,
    SourceScanRequest ScanRequest);

public interface ISourceScanControlStore
{
    ValueTask<ClaimedSourceScan?> ClaimNextReleasedAsync(
        string leaseOwner,
        DateTimeOffset nowUtc,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken);

    ValueTask CompleteAsync(
        ClaimedSourceScan claim,
        SourceScanResult result,
        string? failureReason,
        CancellationToken cancellationToken);

    ValueTask<bool> RenewLeaseAsync(ClaimedSourceScan claim, TimeSpan leaseDuration, CancellationToken cancellationToken) =>
        ValueTask.FromResult(false);
}
