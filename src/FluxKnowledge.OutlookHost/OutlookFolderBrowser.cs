using FluxKnowledge.Application.Contracts;

namespace FluxKnowledge.OutlookHost;

internal interface IOutlookFolderBrowseControlPlane
{
    ValueTask<OutlookBrowseClaim?> TryClaimBrowseAsync(
        OutlookHostIdentity host,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken);

    ValueTask CompleteBrowseAsync(
        OutlookBrowseClaim claim,
        IReadOnlyList<OutlookFolderDescriptor> folders,
        CancellationToken cancellationToken);

    ValueTask FailBrowseAsync(
        OutlookBrowseClaim claim,
        OutlookBrowseFailureCode failureCode,
        CancellationToken cancellationToken);
}

/// <summary>Claims and completes only fenced durable browse work through the same fail-closed COM gate.</summary>
internal sealed class OutlookFolderBrowser(
    OutlookHostOptions options,
    IOutlookHostEnvironment environment,
    IOutlookSessionSingletonFactory singletonFactory,
    IOutlookFolderBrowseControlPlane controlPlane,
    IClassicOutlookAdapterFactory adapterFactory,
    TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    public async ValueTask<OutlookHostRunResult> RunOnceAsync(CancellationToken cancellationToken)
    {
        if (!options.Enabled) return new OutlookHostRunResult(OutlookHostExitReason.Disabled);
        if (!environment.IsWindows) return new OutlookHostRunResult(OutlookHostExitReason.NotWindows);
        if (!environment.IsInteractiveSession) return new OutlookHostRunResult(OutlookHostExitReason.NonInteractiveSession);

        var identity = environment.Identity;
        identity.Validate();
        await using var singleton = await singletonFactory
            .TryAcquireAsync(identity, cancellationToken)
            .ConfigureAwait(false);
        if (singleton is null) return new OutlookHostRunResult(OutlookHostExitReason.SingletonUnavailable);

        var claim = await controlPlane
            .TryClaimBrowseAsync(identity, options.CatchUpLeaseDuration, cancellationToken)
            .ConfigureAwait(false);
        if (claim is null) return new OutlookHostRunResult(OutlookHostExitReason.NoDurableWork);
        if (claim.Host != identity)
        {
            await controlPlane.FailBrowseAsync(claim, OutlookBrowseFailureCode.Failed, cancellationToken).ConfigureAwait(false);
            return new OutlookHostRunResult(OutlookHostExitReason.DurableClaimDisabled);
        }
        if (claim.LeaseExpiresAtUtc <= _clock.GetUtcNow())
        {
            await controlPlane.FailBrowseAsync(claim, OutlookBrowseFailureCode.Expired, cancellationToken).ConfigureAwait(false);
            return new OutlookHostRunResult(OutlookHostExitReason.LeaseStale);
        }

        IClassicOutlookAdapter adapter;
        try
        {
            adapter = await adapterFactory.CreateAsync(
                new OutlookComActivationContext(
                    environment.IsWindows,
                    environment.IsInteractiveSession,
                    HasSessionSingleton: true,
                    identity,
                    DurableWork: null,
                    claim,
                    _clock.GetUtcNow()),
                cancellationToken).ConfigureAwait(false);
        }
        catch (OutlookComHostException)
        {
            await controlPlane.FailBrowseAsync(claim, OutlookBrowseFailureCode.HostUnavailable, cancellationToken).ConfigureAwait(false);
            return new OutlookHostRunResult(OutlookHostExitReason.OutlookUnavailable);
        }

        await using (adapter.ConfigureAwait(false))
        {
            try
            {
            var folders = await adapter.BrowseFoldersAsync(cancellationToken).ConfigureAwait(false);
            await controlPlane.CompleteBrowseAsync(claim, folders, cancellationToken).ConfigureAwait(false);
                return new OutlookHostRunResult(OutlookHostExitReason.Completed);
            }
            catch (OutlookComHostException exception)
            {
                var failure = exception.Reason == OutlookComFailureReason.FolderAccessDenied
                    ? OutlookBrowseFailureCode.AccessDenied
                    : OutlookBrowseFailureCode.HostUnavailable;
                await controlPlane.FailBrowseAsync(claim, failure, cancellationToken).ConfigureAwait(false);
                return new OutlookHostRunResult(
                    failure == OutlookBrowseFailureCode.AccessDenied
                        ? OutlookHostExitReason.FolderAccessDenied
                        : OutlookHostExitReason.OutlookUnavailable);
            }
        }
    }
}
