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
    TimeProvider? timeProvider = null,
    IOutlookComDiagnosticSink? diagnostics = null)
{
    private static readonly TimeSpan FailureCleanupTimeout = TimeSpan.FromSeconds(10);
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;
    private readonly IOutlookComDiagnosticSink _diagnostics = diagnostics ?? NoOpOutlookComDiagnosticSink.Instance;

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
        if (claim.TargetPath is null)
        {
            using var cleanup = new CancellationTokenSource(FailureCleanupTimeout);
            await controlPlane.FailBrowseAsync(claim, OutlookBrowseFailureCode.Failed, cleanup.Token).ConfigureAwait(false);
            return new OutlookHostRunResult(OutlookHostExitReason.DurableClaimDisabled);
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
        catch (OutlookComHostException exception)
        {
            await RecordDiagnosticAsync(exception).ConfigureAwait(false);
            return await FailAsync(claim, exception.Reason, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            var classified = OutlookComFailureClassifier.Classify(exception);
            await RecordDiagnosticAsync(classified).ConfigureAwait(false);
            return await FailAsync(claim, classified.Reason, cancellationToken).ConfigureAwait(false);
        }

        await using (adapter.ConfigureAwait(false))
        {
            IReadOnlyList<OutlookFolderDescriptor> folders;
            try
            {
                folders = await adapter.BrowseFoldersAsync(claim.TargetPath, cancellationToken).ConfigureAwait(false);
            }
            catch (OutlookBrowseTargetException)
            {
                return await FailBrowseTargetAsync(claim).ConfigureAwait(false);
            }
            catch (OutlookComHostException exception)
            {
                await RecordDiagnosticAsync(exception).ConfigureAwait(false);
                return await FailAsync(claim, exception.Reason, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                var classified = OutlookComFailureClassifier.Classify(exception);
                await RecordDiagnosticAsync(classified).ConfigureAwait(false);
                return await FailAsync(claim, classified.Reason, cancellationToken).ConfigureAwait(false);
            }

            if (folders.Count != 1)
            {
                using var cleanup = new CancellationTokenSource(FailureCleanupTimeout);
                await controlPlane.FailBrowseAsync(claim, OutlookBrowseFailureCode.Failed, cleanup.Token).ConfigureAwait(false);
                return new OutlookHostRunResult(OutlookHostExitReason.DurableClaimDisabled);
            }

            await controlPlane.CompleteBrowseAsync(claim, folders, cancellationToken).ConfigureAwait(false);
            return new OutlookHostRunResult(OutlookHostExitReason.Completed);
        }
    }

    private async ValueTask RecordDiagnosticAsync(OutlookComHostException failure)
    {
        try
        {
            await _diagnostics.WriteAsync(failure, CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // The opt-in private diagnostic channel must not alter a fenced host outcome.
        }
    }

    private async ValueTask<OutlookHostRunResult> FailAsync(
        OutlookBrowseClaim claim,
        OutlookComFailureReason reason,
        CancellationToken cancellationToken)
    {
        var failure = reason == OutlookComFailureReason.FolderAccessDenied
            ? OutlookBrowseFailureCode.AccessDenied
            : OutlookBrowseFailureCode.HostUnavailable;
        using var cleanup = new CancellationTokenSource(FailureCleanupTimeout);
        await controlPlane.FailBrowseAsync(claim, failure, cleanup.Token).ConfigureAwait(false);
        return new OutlookHostRunResult(reason switch
        {
            OutlookComFailureReason.DependencyMissing => OutlookHostExitReason.ComDependencyMissing,
            OutlookComFailureReason.FolderAccessDenied => OutlookHostExitReason.FolderAccessDenied,
            OutlookComFailureReason.LeaseStale => OutlookHostExitReason.LeaseStale,
            _ => OutlookHostExitReason.OutlookUnavailable
        });
    }

    private async ValueTask<OutlookHostRunResult> FailBrowseTargetAsync(OutlookBrowseClaim claim)
    {
        using var cleanup = new CancellationTokenSource(FailureCleanupTimeout);
        await controlPlane.FailBrowseAsync(claim, OutlookBrowseFailureCode.Failed, cleanup.Token).ConfigureAwait(false);
        return new OutlookHostRunResult(OutlookHostExitReason.DurableClaimDisabled);
    }

}
