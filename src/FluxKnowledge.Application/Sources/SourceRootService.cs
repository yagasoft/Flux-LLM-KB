using FluxKnowledge.Application.Contracts;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Workers;
using FluxKnowledge.Domain.Sources;

namespace FluxKnowledge.Application.Sources;

public sealed class SourceRootService(
    ISourceRootPathPolicy pathPolicy,
    ISourceRootStore rootStore,
    IOutboxWakeSignal wakeSignal,
    ISourceScanWakeSignal? sourceWakeSignal = null)
{
    public async ValueTask<SourceRootReceipt> CreateAsync(
        SourceRootCreateRequest request,
        ScanStartIntent startIntent,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var validation = pathPolicy.ValidateAndCanonicalise(request);
        var canonicalRequest = request with
        {
            FullPath = validation.CanonicalPath,
            PathValidation = validation
        };
        _ = SourceRootConfiguration.Create(
            canonicalRequest.FullPath,
            canonicalRequest.DisplayName,
            canonicalRequest.Recursive,
            canonicalRequest.FollowLinks,
            canonicalRequest.MaximumFileBytes,
            canonicalRequest.IncludePatterns,
            canonicalRequest.ExcludePatterns,
            canonicalRequest.AllowedClassifications,
            canonicalRequest.ReconciliationCadence);
        var receipt = await rootStore.CreateAsync(canonicalRequest, startIntent, cancellationToken)
            .ConfigureAwait(false);
        if (startIntent == ScanStartIntent.SaveAndScan && !receipt.IsHeld)
        {
            wakeSignal.Notify();
            sourceWakeSignal?.Notify();
        }

        return receipt;
    }
}
