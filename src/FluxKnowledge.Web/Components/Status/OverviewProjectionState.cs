using FluxKnowledge.Application.Contracts;
using System.Data.Common;

namespace FluxKnowledge.Web.Components.Status;

public sealed class OverviewProjectionState(IProjectionReader reader, ILogger<OverviewProjectionState>? logger = null)
{
    private readonly SemaphoreSlim _reloadGate = new(1, 1);
    public bool HasSnapshot { get; private set; }
    public bool IsStale { get; private set; }
    public DateTimeOffset? LastSuccessfulReadAtUtc { get; private set; }
    public string? RefreshIssue { get; private set; }

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
            await ReloadAsync(cancellationToken).ConfigureAwait(false);
            return subscription;
        }
        catch
        {
            await subscription.DisposeAsync();
            throw;
        }
    }

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
