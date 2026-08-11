using System.Data.Common;
using FluxKnowledge.Application.Contracts;
using FluxKnowledge.Domain.Outlook;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Integrations.Outlook;

namespace FluxKnowledge.OutlookHost;

internal interface IOutlookHostEnvironment
{
    bool IsWindows { get; }
    bool IsInteractiveSession { get; }
    OutlookHostIdentity Identity { get; }
}

internal interface IOutlookSessionSingletonFactory
{
    ValueTask<IAsyncDisposable?> TryAcquireAsync(OutlookHostIdentity identity, CancellationToken cancellationToken);
}

internal interface IOutlookHostControlPlane
{
    ValueTask<OutlookHostCatchUpWork?> TryClaimCatchUpAsync(
        OutlookHostIdentity host,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken);

    ValueTask RecordHintAsync(
        OutlookCaptureProfileId profileId,
        OutlookHint hint,
        CancellationToken cancellationToken);

    ValueTask<OutlookCatchUpClaim?> RenewCatchUpAsync(
        OutlookCatchUpClaim claim,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken);

    ValueTask<bool> CompleteCatchUpAsync(
        OutlookCatchUpClaim claim,
        int exportedCount,
        CancellationToken cancellationToken);

    ValueTask FailCatchUpAsync(
        OutlookCatchUpClaim claim,
        OutlookCatchUpFailureReason reason,
        CancellationToken cancellationToken);
}

internal interface IOutlookExportIngestionBridge
{
    ValueTask<bool> ExportAndIngestAsync(
        OutlookHostCatchUpWork work,
        OutlookHostFolderConfiguration folder,
        OutlookItemEnvelope item,
        OutlookMessagePayload payload,
        CancellationToken cancellationToken);
}

internal sealed class OutlookHostLoop(
    OutlookHostOptions options,
    IOutlookHostEnvironment environment,
    IOutlookSessionSingletonFactory singletonFactory,
    IOutlookHostControlPlane controlPlane,
    IClassicOutlookAdapterFactory adapterFactory,
    IOutlookExportIngestionBridge ingestionBridge,
    TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    public async ValueTask<OutlookHostRunResult> RunOnceAsync(CancellationToken cancellationToken)
    {
        if (!options.Enabled)
        {
            return new OutlookHostRunResult(OutlookHostExitReason.Disabled);
        }

        if (!environment.IsWindows)
        {
            return new OutlookHostRunResult(OutlookHostExitReason.NotWindows);
        }

        if (!environment.IsInteractiveSession)
        {
            return new OutlookHostRunResult(OutlookHostExitReason.NonInteractiveSession);
        }

        var identity = environment.Identity;
        identity.Validate();
        await using var singleton = await singletonFactory
            .TryAcquireAsync(identity, cancellationToken)
            .ConfigureAwait(false);
        if (singleton is null)
        {
            return new OutlookHostRunResult(OutlookHostExitReason.SingletonUnavailable);
        }

        var work = await controlPlane
            .TryClaimCatchUpAsync(identity, options.CatchUpLeaseDuration, cancellationToken)
            .ConfigureAwait(false);
        if (work is null)
        {
            return new OutlookHostRunResult(OutlookHostExitReason.NoDurableWork);
        }

        if (!work.IsDurablyEnabled ||
            work.Folders.Count == 0 ||
            work.Claim.LeaseOwner != identity)
        {
            await controlPlane.FailCatchUpAsync(
                work.Claim,
                OutlookCatchUpFailureReason.ConfigurationChanged,
                cancellationToken).ConfigureAwait(false);
            return new OutlookHostRunResult(OutlookHostExitReason.DurableClaimDisabled);
        }

        if (work.Claim.LeaseExpiresAtUtc <= _clock.GetUtcNow())
        {
            await controlPlane.FailCatchUpAsync(
                work.Claim,
                OutlookCatchUpFailureReason.LeaseLost,
                cancellationToken).ConfigureAwait(false);
            return new OutlookHostRunResult(OutlookHostExitReason.LeaseStale);
        }

        var renewedClaim = await controlPlane
            .RenewCatchUpAsync(work.Claim, options.CatchUpLeaseDuration, cancellationToken)
            .ConfigureAwait(false);
        if (renewedClaim is null)
        {
            await controlPlane.FailCatchUpAsync(
                work.Claim,
                OutlookCatchUpFailureReason.LeaseLost,
                cancellationToken).ConfigureAwait(false);
            return new OutlookHostRunResult(OutlookHostExitReason.LeaseStale);
        }

        work = work with { Claim = renewedClaim };

        IClassicOutlookAdapter adapter;
        try
        {
            adapter = await adapterFactory.CreateAsync(
                new OutlookComActivationContext(
                    environment.IsWindows,
                    environment.IsInteractiveSession,
                    HasSessionSingleton: true,
                    identity,
                    work,
                    BrowseClaim: null,
                    _clock.GetUtcNow()),
                cancellationToken).ConfigureAwait(false);
        }
        catch (OutlookComHostException exception)
        {
            await controlPlane.FailCatchUpAsync(
                work.Claim,
                MapFailure(exception.Reason),
                cancellationToken).ConfigureAwait(false);
            return new OutlookHostRunResult(MapExit(exception.Reason));
        }

        await using (adapter.ConfigureAwait(false))
        {
            await using var heartbeat = new OutlookCatchUpHeartbeat(
                controlPlane,
                work.Claim,
                options.CatchUpLeaseDuration,
                options.HeartbeatCadence,
                cancellationToken);
            try
            {
                var exportedCount = await CatchUpAsync(work, adapter, heartbeat, cancellationToken).ConfigureAwait(false);
                await heartbeat.StopAsync().ConfigureAwait(false);
                heartbeat.ThrowIfLeaseLost();
                var finalClaim = await controlPlane
                    .RenewCatchUpAsync(heartbeat.CurrentClaim, options.CatchUpLeaseDuration, cancellationToken)
                    .ConfigureAwait(false);
                if (finalClaim is null)
                {
                    throw new OutlookComHostException(OutlookComFailureReason.LeaseStale);
                }

                if (!await controlPlane
                    .CompleteCatchUpAsync(finalClaim, exportedCount, cancellationToken)
                    .ConfigureAwait(false))
                {
                    throw new OutlookComHostException(OutlookComFailureReason.LeaseStale);
                }

                return new OutlookHostRunResult(OutlookHostExitReason.Completed, exportedCount);
            }
            catch (OutlookComHostException exception)
            {
                await controlPlane.FailCatchUpAsync(
                    work.Claim,
                    MapFailure(exception.Reason),
                    cancellationToken).ConfigureAwait(false);
                return new OutlookHostRunResult(MapExit(exception.Reason));
            }
            catch (OutlookReadyExportLeaseException)
            {
                await controlPlane.FailCatchUpAsync(
                    work.Claim,
                    OutlookCatchUpFailureReason.LeaseLost,
                    cancellationToken).ConfigureAwait(false);
                return new OutlookHostRunResult(OutlookHostExitReason.LeaseStale);
            }
            catch (Exception exception) when (IsExpectedIngestionFailure(exception))
            {
                await controlPlane.FailCatchUpAsync(
                    work.Claim,
                    OutlookCatchUpFailureReason.RetryableHostFailure,
                    cancellationToken).ConfigureAwait(false);
                return new OutlookHostRunResult(OutlookHostExitReason.IngestionFailed);
            }
        }
    }

    private async ValueTask<int> CatchUpAsync(
        OutlookHostCatchUpWork work,
        IClassicOutlookAdapter adapter,
        OutlookCatchUpHeartbeat heartbeat,
        CancellationToken cancellationToken)
    {
        var exportedCount = 0;
        foreach (var folder in work.Folders)
        {
            var callback = new Func<OutlookHint, ValueTask>(hint =>
                controlPlane.RecordHintAsync(work.Claim.ProfileId, hint, cancellationToken));
            await using var subscription = await adapter
                .SubscribeHintsAsync(folder.Identity, callback, cancellationToken)
                .ConfigureAwait(false);
            var cursorUtc = folder.CursorUtc ?? DateTimeOffset.UnixEpoch;
            var cursor = new OutlookCursor(folder.Basis, cursorUtc.Subtract(folder.Overlap), folder.CursorFingerprint);
            var observedEntries = new HashSet<string>(StringComparer.Ordinal);
            await foreach (var item in adapter
                .EnumerateAsync(folder.Identity, cursor, cancellationToken)
                .WithCancellation(cancellationToken)
                .ConfigureAwait(false))
            {
                // Periodic renewal runs independently while COM enumeration is slow; observe loss
                // before any private content read or export.
                cancellationToken.ThrowIfCancellationRequested();
                if (!observedEntries.Add(item.EntryId))
                {
                    continue;
                }
                heartbeat.ThrowIfLeaseLost();

                var payload = await adapter.ReadForExportAsync(item, cancellationToken).ConfigureAwait(false);
                if (await ingestionBridge
                    .ExportAndIngestAsync(work, folder, item, payload, cancellationToken)
                    .ConfigureAwait(false))
                {
                    exportedCount++;
                }
            }
        }

        return exportedCount;
    }

    private static OutlookCatchUpFailureReason MapFailure(OutlookComFailureReason reason) => reason switch
    {
        OutlookComFailureReason.FolderAccessDenied => OutlookCatchUpFailureReason.AccessDenied,
        OutlookComFailureReason.LeaseStale => OutlookCatchUpFailureReason.LeaseLost,
        OutlookComFailureReason.DependencyMissing or OutlookComFailureReason.OutlookUnavailable =>
            OutlookCatchUpFailureReason.RetryableHostFailure,
        _ => OutlookCatchUpFailureReason.Blocked
    };

    private static OutlookHostExitReason MapExit(OutlookComFailureReason reason) => reason switch
    {
        OutlookComFailureReason.DependencyMissing => OutlookHostExitReason.ComDependencyMissing,
        OutlookComFailureReason.OutlookUnavailable => OutlookHostExitReason.OutlookUnavailable,
        OutlookComFailureReason.FolderAccessDenied => OutlookHostExitReason.FolderAccessDenied,
        OutlookComFailureReason.LeaseStale => OutlookHostExitReason.LeaseStale,
        _ => OutlookHostExitReason.OutlookUnavailable
    };

    private static bool IsExpectedIngestionFailure(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is IOException or UnauthorizedAccessException or InvalidDataException or DbException or
                OutlookReadyExportValidationException)
            {
                return true;
            }
        }

        return false;
    }
}

internal sealed class OutlookCatchUpHeartbeat : IAsyncDisposable
{
    private readonly IOutlookHostControlPlane _controlPlane;
    private OutlookCatchUpClaim _claim;
    private readonly TimeSpan _leaseDuration;
    private readonly CancellationTokenSource _stop;
    private readonly Task _loop;
    private int _leaseLost;
    private int _stopped;

    public OutlookCatchUpClaim CurrentClaim => Volatile.Read(ref _claim);

    public OutlookCatchUpHeartbeat(
        IOutlookHostControlPlane controlPlane,
        OutlookCatchUpClaim claim,
        TimeSpan leaseDuration,
        TimeSpan cadence,
        CancellationToken cancellationToken)
    {
        if (cadence <= TimeSpan.Zero || cadence >= leaseDuration)
        {
            throw new ArgumentOutOfRangeException(nameof(cadence));
        }

        _controlPlane = controlPlane;
        _claim = claim;
        _leaseDuration = leaseDuration;
        _stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _loop = RunAsync(cadence, _stop.Token);
    }

    public void ThrowIfLeaseLost()
    {
        if (Volatile.Read(ref _leaseLost) != 0)
        {
            throw new OutlookComHostException(OutlookComFailureReason.LeaseStale);
        }
    }

    public async ValueTask StopAsync()
    {
        if (Interlocked.Exchange(ref _stopped, 1) == 0)
        {
            await _stop.CancelAsync().ConfigureAwait(false);
        }

        try
        {
            await _loop.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _stop.Dispose();
    }

    private async Task RunAsync(TimeSpan cadence, CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(cadence);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                var renewedClaim = await _controlPlane
                    .RenewCatchUpAsync(CurrentClaim, _leaseDuration, cancellationToken)
                    .ConfigureAwait(false);
                if (renewedClaim is null)
                {
                    Interlocked.Exchange(ref _leaseLost, 1);
                    return;
                }

                Volatile.Write(ref _claim, renewedClaim);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch
        {
            Interlocked.Exchange(ref _leaseLost, 1);
        }
    }
}
