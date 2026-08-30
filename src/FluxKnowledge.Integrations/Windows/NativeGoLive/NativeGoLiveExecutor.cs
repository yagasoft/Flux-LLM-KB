using FluxKnowledge.Application.Operations;

namespace FluxKnowledge.Integrations.Windows.NativeGoLive;

/// <summary>
/// Sequences a single confirmed clean-slate operation through typed ports. Deployment execution
/// deliberately has no durable journal, compare-and-swap, adoption, marker, replay or resume path:
/// every later invocation starts from a newly observed admission boundary.
/// </summary>
public sealed class NativeGoLiveExecutor
{
    public async Task<NativeGoLiveResult> ExecuteAsync(
        NativeGoLiveRequest request,
        INativeGoLiveHost host,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(host);

        if (request.IsPlanOnly) return NativeGoLiveResult.Completed();
        if (!HasAllAcknowledgements(request)) return NativeGoLiveResult.Refused("go-live-acknowledgement-required");
        if (cancellationToken.IsCancellationRequested)
            return NativeGoLiveResult.Refused("go-live-cancelled-before-admission");

        INativeGoLiveLease lease;
        try
        {
            lease = await host.AcquireLeaseAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (NativeGoLiveLeaseUnavailableException)
        {
            return NativeGoLiveResult.Refused("go-live-lease-unavailable");
        }
        catch (OperationCanceledException)
        {
            return NativeGoLiveResult.Refused("go-live-cancelled-before-admission");
        }

        await using (lease)
        {
            try
            {
                await host.PrepareHostPrerequisitesAsync(request.Plan, cancellationToken).ConfigureAwait(false);
                await host.AdmitAndWipeAsync(request, cancellationToken).ConfigureAwait(false);
                await host.VerifyOneShotPreflightAsync(request.Plan, cancellationToken).ConfigureAwait(false);
                await host.StopPoolAsync(cancellationToken).ConfigureAwait(false);
                await host.ConfigureVssAsync(request.Plan.Vss, cancellationToken).ConfigureAwait(false);
                await host.CreateEmptyRootAsync(request.Plan, cancellationToken).ConfigureAwait(false);
                await host.ProvisionEmptyCatalogueAsync(request.Plan.Sql, cancellationToken).ConfigureAwait(false);
                await host.PublishAndStartAsync(request.Plan, cancellationToken).ConfigureAwait(false);
                await host.ValidateAsync(request.Plan, cancellationToken).ConfigureAwait(false);
                await host.RegisterMarketplaceAsync(request.Plan.Codex, cancellationToken).ConfigureAwait(false);
                return NativeGoLiveResult.Completed();
            }
            catch (OperationCanceledException)
            {
                return NativeGoLiveResult.Refused("clean-slate-incomplete");
            }
            catch (Exception)
            {
                return NativeGoLiveResult.Refused("clean-slate-incomplete");
            }
        }
    }

    private static bool HasAllAcknowledgements(NativeGoLiveRequest request) =>
        request.ConfirmCleanSlate &&
        request.ConfirmConfigureVss &&
        request.ConfirmDestroySql &&
        request.ConfirmRegisterCodex;
}
