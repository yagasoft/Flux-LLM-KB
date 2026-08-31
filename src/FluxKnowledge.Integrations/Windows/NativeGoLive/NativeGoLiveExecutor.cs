using FluxKnowledge.Application.Operations;
using System.Text.RegularExpressions;

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
            }
            catch (Exception exception) when (TryGetSafeBootstrapFailureReason(exception, out var reasonCode))
            {
                return NativeGoLiveResult.Refused(reasonCode);
            }
            catch (OperationCanceledException)
            {
                return NativeGoLiveResult.Refused("clean-slate-incomplete");
            }
            catch (Exception)
            {
                return NativeGoLiveResult.Refused("clean-slate-incomplete");
            }

            try
            {
                await host.AdmitAndWipeAsync(request, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return NativeGoLiveResult.Refused("clean-slate-admission-failed");
            }
            catch (Exception)
            {
                return NativeGoLiveResult.Refused("clean-slate-admission-failed");
            }

            try
            {
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
            catch (NativeGoLiveContractException exception) when (
                exception.ReasonCode is "vss-exact-action-not-proved" or
                    "vss-add-diff-area-failed" or "vss-change-diff-area-failed")
            {
                return NativeGoLiveResult.Refused(exception.ReasonCode);
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

    private static bool TryGetSafeBootstrapFailureReason(Exception exception, out string reasonCode)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (BootstrapFailureCode.IsMatch(current.Message))
            {
                reasonCode = current.Message;
                return true;
            }
        }

        reasonCode = string.Empty;
        return false;
    }

    private static readonly Regex BootstrapFailureCode = new(
        @"\Anative-go-live-bootstrap-(?:(?:reset|install|probe)-(?:connection|sni-load|script-parse|sql-batch-[1-9][0-9]*)-failed|(?:reset|install|probe)-failed)\z",
        RegexOptions.CultureInvariant);
}
