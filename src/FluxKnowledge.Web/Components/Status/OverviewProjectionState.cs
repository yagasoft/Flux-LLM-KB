using FluxKnowledge.Application.Contracts;
using System.Data.Common;

namespace FluxKnowledge.Web.Components.Status;

public sealed class OverviewProjectionState(IProjectionReader reader, ILogger<OverviewProjectionState>? logger = null,
    OverviewSnapshotCoordinator? shared = null, TimeProvider? timeProvider = null)
{
    private readonly SemaphoreSlim _reloadGate = new(1, 1);
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;
    private IDisposable? _sharedSubscription;
    private bool _isStale;
    private string? _refreshIssue;
    public bool UsesSharedSnapshots => shared is not null;
    public bool HasSnapshot { get; private set; }
    public bool IsStale
    {
        get => _isStale || shared is not null && LastSuccessfulReadAtUtc is { } sampled &&
            _clock.GetUtcNow() - sampled >= TimeSpan.FromSeconds(10);
        private set => _isStale = value;
    }
    public DateTimeOffset? LastSuccessfulReadAtUtc { get; private set; }
    public string? RefreshIssue
    {
        get => _refreshIssue ?? (IsStale ? "The last committed sample is more than 10 seconds old. Refreshing status." : null);
        private set => _refreshIssue = value;
    }

    public OverviewProjection Current { get; private set; } = new(
        0,
        0,
        0,
        0,
        0,
        0,
        0,
        "none",
        new IndexRecoverySummary("Starting", null, null, null, null, 0))
    {
        GpuSchedulerStatus = GpuSchedulerStatusProjection.Empty
    };

    public async ValueTask ReloadAsync(CancellationToken cancellationToken)
    {
        if (shared is not null)
        {
            await shared.RefreshAsync(cancellationToken, force: true).ConfigureAwait(false);
            SynchronizeFromShared();
            return;
        }
        if (!await _reloadGate.WaitAsync(0, cancellationToken).ConfigureAwait(false)) return;
        try
        {
            var next = await reader.ReadOverviewAsync(cancellationToken).ConfigureAwait(false);
            if (!next.Activity.IsConsistent)
            {
                IsStale = true;
                RefreshIssue = "Status changed during the read. Waiting for a consistent sample.";
                return;
            }
            Current = next;
            HasSnapshot = true;
            IsStale = false;
            RefreshIssue = null;
            LastSuccessfulReadAtUtc = next.Activity.IsAvailable ? next.Activity.ObservedAtUtc : DateTimeOffset.UtcNow;
        }
        catch (OverviewReadBudgetExceededException exception)
        {
            logger?.LogDebug(exception, "Overview status read budget was exhausted.");
            IsStale = true;
            RefreshIssue = "Status is busy. Waiting for a consistent sample.";
        }
        catch (Exception exception) when (exception is DbException or InvalidOperationException or TimeoutException)
        {
            logger?.LogWarning(exception, "Overview status refresh failed.");
            IsStale = true;
            RefreshIssue = "Status refresh failed.";
        }
        finally { _reloadGate.Release(); }
    }

    public async ValueTask<StatusEventSubscription> SubscribeAndReloadAsync(
        StatusEventFeed statusEvents,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(statusEvents);

        var subscription = statusEvents.Subscribe();
        try
        {
            if (shared is null) await ReloadAsync(cancellationToken).ConfigureAwait(false);
            else
            {
                ReleaseSharedSubscription();
                _sharedSubscription = shared.Subscribe();
                SynchronizeFromShared();
                if (!HasSnapshot) await shared.RefreshAsync(cancellationToken).ConfigureAwait(false);
                else _ = shared.RefreshAsync(CancellationToken.None).AsTask();
                SynchronizeFromShared();
            }
            return subscription;
        }
        catch
        {
            ReleaseSharedSubscription();
            await subscription.DisposeAsync();
            throw;
        }
    }

    public void SynchronizeFromShared()
    {
        if (shared is null) return;
        var value = shared.Current;
        if (value.Projection is { } projection)
        {
            Current = projection;
            HasSnapshot = true;
            LastSuccessfulReadAtUtc = OverviewSnapshotCoordinator.SampleTime(value);
        }
        IsStale = value.RefreshIssue is not null;
        RefreshIssue = value.RefreshIssue;
    }

    public void ReleaseSharedSubscription() => Interlocked.Exchange(ref _sharedSubscription, null)?.Dispose();

    public ValueTask HandleStatusChangedAsync(StatusChanged statusChanged, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(statusChanged);
        return AffectsOverview(statusChanged.Projection)
            ? ReloadAsync(cancellationToken)
            : ValueTask.CompletedTask;
    }

    public static bool AffectsOverview(string projection) => projection.ToLowerInvariant() is
        "pipeline" or "sources" or "index-recovery" or "gpu-scheduler" or "reconnect";
}
