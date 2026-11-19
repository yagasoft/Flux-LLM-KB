using System.Collections.Concurrent;
using System.ComponentModel;
using System.Threading.Channels;
using FluxKnowledge.Application.Gpu;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Domain.Gpu;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Integrations.Documents;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FluxKnowledge.Infrastructure.SqlServer.Workers;

/// <summary>Owns local native allocations. SQL grants each borrow and decides when an idle allocation may be evicted.</summary>
internal sealed class GpuModelResidencyRuntime(SqlGpuModelResidencyStore store, GpuSchedulerOptions options,
    IGpuInteractiveOwnerProbe ownerProbe, IGpuSchedulerWakeSignal schedulerWake, TimeProvider clock,
    IDatabaseStartupReadiness readiness, ILogger<GpuModelResidencyRuntime> logger, SqlGpuSchedulerStore scheduler)
    : BackgroundService, IGpuProcessModelResidency
{
    private readonly ConcurrentDictionary<Guid, LocalAllocation> _allocations = new();
    private readonly ConcurrentDictionary<Guid, GpuResidentModelFence> _processBorrows = new();
    private readonly Channel<bool> _signal = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
    { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true });

    internal sealed record NativeBorrow(GpuResidentModelFence Fence, OwnedWarmModelProcess Process);
    private sealed class LocalAllocation(object resource, Func<CancellationToken, ValueTask<bool>> release,
        GpuResidentModelKey? model = null)
    {
        private readonly SemaphoreSlim _gate = new(1, 1);
        private bool _released;
        internal object Resource { get; } = resource;
        internal GpuResidentModelKey? Model { get; } = model;

        internal async ValueTask<bool> Release(CancellationToken ct, Func<ValueTask<bool>>? authorised = null)
        {
            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (authorised is not null && !await authorised().ConfigureAwait(false)) return false;
                if (!_released) _released = await release(ct).ConfigureAwait(false);
                return _released;
            }
            finally { _gate.Release(); }
        }

        internal async ValueTask<T?> WhileResidentAsync<T>(Func<ValueTask<T>> operation, CancellationToken ct) where T : class
        {
            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try { return _released ? null : await operation().ConfigureAwait(false); }
            finally { _gate.Release(); }
        }
    }

    internal GpuResidentModelKey? EntryModel(string runtimeKey, string settingsFingerprint) =>
        options.GetResidentEntryModel(runtimeKey, settingsFingerprint);

    internal void Notify() => _signal.Writer.TryWrite(true);

    public bool Supports(GpuOwnedWorkContext context) => EntryModel(context.RuntimeKey, context.SettingsFingerprint) is not null;

    public async ValueTask<T> GetModelAsync<T>(GpuOwnedWorkContext context, GpuResidentModelKey model,
        Func<ValueTask<T>> open) where T : class, IGpuProcessResidentModel
    {
        context.RequireActive(model.RuntimeKey, model.SettingsFingerprint);
        if (!Supports(context)) throw new InvalidOperationException("gpu-process-residency-disabled");
        var handle = context.Handle;
        var ct = context.CancellationToken;
        var existing = await store.ReadBorrowAsync(handle, ct).ConfigureAwait(false);
        var selected = existing ?? await store.BeginBorrowAsync(handle, Guid.NewGuid(), handle.DispatchId,
            model, ownerProbe.Current, null, ct).ConfigureAwait(false);
        if (selected is null || selected.Helper is not null || selected.Owner != ownerProbe.Current)
            throw new InvalidOperationException("gpu-process-resident-owner-unverified");

        if (existing is not null && !_allocations.ContainsKey(selected.ResidencyId) &&
            (!_processBorrows.TryGetValue(handle.DispatchId, out var previous) || previous != selected))
            throw new InvalidOperationException("gpu-process-resident-model-missing");
        _processBorrows.TryAdd(handle.DispatchId, selected);
        if (_processBorrows[handle.DispatchId] != selected)
            throw new InvalidOperationException("gpu-process-resident-fence-changed");
        if (_allocations.TryGetValue(selected.ResidencyId, out var allocation))
        {
            if (allocation.Resource is not T typed || !typed.IsIdle)
                throw new InvalidOperationException("gpu-process-resident-busy");
            if (allocation.Model == model)
            {
                context.MarkResidentModelUsed();
                return typed;
            }
            if (!await allocation.Release(ct).ConfigureAwait(false))
                throw new InvalidOperationException("gpu-process-model-release-unproven");
            Forget(selected.ResidencyId);
        }
        context.RequireActive(model.RuntimeKey, model.SettingsFingerprint);
        var resource = await open().ConfigureAwait(false);
        _allocations[selected.ResidencyId] = new(resource, _ =>
        {
            resource.Dispose();
            return ValueTask.FromResult(resource.PhysicalReleaseConfirmed);
        }, model);
        context.MarkResidentModelUsed();
        return resource;
    }

    public async ValueTask<bool> CompleteAsync(GpuOwnedWorkContext context, Guid operationId, Guid taskId,
        CancellationToken ct)
    {
        if (!_processBorrows.TryGetValue(context.Handle.DispatchId, out var fence) ||
            !_allocations.TryGetValue(fence.ResidencyId, out var allocation) ||
            allocation.Resource is not IGpuProcessResidentModel { IsIdle: true } ||
            allocation.Model is not { } model) return false;
        var settled = await scheduler.CompleteResidentBatchAsync(operationId, context.Handle, taskId, fence, model, ct).ConfigureAwait(false);
        if (!settled.Accepted || !settled.Committed) return false;
        _processBorrows.TryRemove(context.Handle.DispatchId, out _);
        Notify();
        schedulerWake.Notify(GpuSchedulerWakeReason.Reconciliation);
        return true;
    }

    public async ValueTask<bool> ReleaseAsync(GpuOwnedWorkContext context, CancellationToken ct)
    {
        var handle = context.Handle;
        var borrow = await store.ReadBorrowAsync(handle, ct).ConfigureAwait(false);
        if (borrow is null) return context.NativeCapacityReleased;
        if (borrow.Helper is not null || borrow.Owner != ownerProbe.Current) return false;
        if (_allocations.TryGetValue(borrow.ResidencyId, out var allocation))
        {
            if (!await allocation.Release(ct).ConfigureAwait(false)) return false;
        }
        else if (!_processBorrows.TryGetValue(handle.DispatchId, out var tracked) || tracked != borrow)
            return false;
        if (!context.NativeCapacityReleased || !await store.ReleaseBorrowedAsync(borrow, handle, handle.DispatchId,
                GpuResidentReleaseKind.NativeSessionDisposed, ct).ConfigureAwait(false))
            return false;
        Forget(borrow.ResidencyId);
        _processBorrows.TryRemove(handle.DispatchId, out _);
        return true;
    }

    public async ValueTask<bool> RecoverExitedAsync(GpuExecutorBatchHandle handle, GpuInteractiveOwnerIdentity owner,
        CancellationToken ct)
    {
        var borrow = await store.ReadBorrowAsync(handle, ct).ConfigureAwait(false);
        if (borrow is null) return true;
        return borrow.Helper is null && borrow.Owner == owner &&
            ownerProbe.Observe(owner) == GpuInteractiveOwnerObservation.Exited &&
            await store.ReleaseBorrowedAsync(borrow, handle, handle.DispatchId, GpuResidentReleaseKind.HostingProcessExited, ct)
                .ConfigureAwait(false);
    }

    internal async ValueTask<Guid> GetExecutionIdentityAsync(GpuExecutorBatchHandle handle, CancellationToken ct) =>
        await store.ReadBorrowAsync(handle, ct).ConfigureAwait(false) is null ? Guid.NewGuid() : handle.DispatchId;

    internal async ValueTask<NativeBorrow> OpenHelperAsync(GpuExecutorBatchHandle handle, Guid executionInstanceId,
        GpuResidentModelKey key, Func<Guid, string, CancellationToken, ValueTask<OwnedWarmModelProcess>> open, CancellationToken ct)
    {
        var existing = await store.ReadBorrowAsync(handle, ct).ConfigureAwait(false);
        if (existing is not null)
        {
            if (executionInstanceId == handle.DispatchId && existing.Owner == ownerProbe.Current &&
                _allocations.TryGetValue(existing.ResidencyId, out var allocation) &&
                allocation.Resource is OwnedWarmModelProcess local && existing.Helper ==
                new GpuResidentHelperIdentity(existing.ResidencyId, local.Identity.ProcessId, local.Identity.StartedAtUtc))
                return new(existing, local);
            throw new InvalidOperationException("gpu-resident-helper-owner-unverified");
        }

        var id = Guid.NewGuid();
        var owner = ownerProbe.Current;
        var helper = await open(id, owner.MachineFingerprint, ct).ConfigureAwait(false);
        var proof = new GpuResidentHelperIdentity(id, helper.Identity.ProcessId, helper.Identity.StartedAtUtc);
        _allocations[id] = new(helper, async token =>
        {
            if (!await helper.StopAndConfirmAsync(token).ConfigureAwait(false)) return false;
            await helper.DisposeAsync().ConfigureAwait(false);
            return true;
        });
        try
        {
            var created = await store.BeginBorrowAsync(handle, id, executionInstanceId, key, owner, proof, ct).ConfigureAwait(false);
            if (created is null) throw new InvalidOperationException("gpu-resident-helper-claim-refused");
            Notify();
            return new(created, helper);
        }
        catch
        {
            // No request was sent to the newly launched helper. Keep the local
            // proof if SQL cannot tell us whether the borrow committed.
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(15), clock);
            if (await _allocations[id].Release(cleanup.Token).ConfigureAwait(false))
            {
                try
                {
                    var committed = await store.ReadBorrowAsync(handle, cleanup.Token).ConfigureAwait(false);
                    if (committed?.ResidencyId != id) Forget(id);
                }
                catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
                { logger.LogWarning(error, "GPU startup cleanup could not reconcile the residency record."); }
            }
            Notify();
            throw;
        }
    }

    internal async ValueTask<bool> ConfirmHelperEmptyAsync(GpuResidentModelFence fence, CancellationToken ct)
    {
        if (fence.Helper is null || fence.Owner.MachineFingerprint != ownerProbe.Current.MachineFingerprint)
            return false;
        if (_allocations.TryGetValue(fence.ResidencyId, out var local))
        {
            if (fence.Owner != ownerProbe.Current || local.Resource is not OwnedWarmModelProcess process ||
                process.Identity.ProcessId != fence.Helper.ProcessId || process.Identity.StartedAtUtc != fence.Helper.StartedAtUtc)
                return false;
            // A shutdown from the previous job must lose to a completed park and a successor.
            return await local.Release(ct, () => store.IsCurrentHelperCleanupTargetAsync(fence, ct)).ConfigureAwait(false);
        }
        if (ownerProbe.Observe(fence.Owner) != GpuInteractiveOwnerObservation.Exited) return false;
        if (!await store.IsCurrentHelperCleanupTargetAsync(fence, ct).ConfigureAwait(false)) return false;
        try
        {
            using var job = OwnedOcrJob.TryOpen(fence.Helper.JobId);
            return job is not null && await job.TerminateAndConfirmAsync(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
        }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    internal async ValueTask<T?> SettleHelperAsync<T>(GpuResidentModelFence fence, Func<ValueTask<T>> settle, CancellationToken ct) where T : class
    {
        if (fence.Owner != ownerProbe.Current || !_allocations.TryGetValue(fence.ResidencyId, out var local) ||
            local.Resource is not OwnedWarmModelProcess) return null;
        return await local.WhileResidentAsync(settle, ct).ConfigureAwait(false);
    }

    internal void Forget(Guid id) => _allocations.TryRemove(id, out _);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await readiness.WaitAsync(stoppingToken).ConfigureAwait(false);
        while (!stoppingToken.IsCancellationRequested)
        {
            DateTimeOffset? nextExpiry = null;
            try { nextExpiry = await ReconcileIdleAsync(stoppingToken).ConfigureAwait(false); }
            catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException && !stoppingToken.IsCancellationRequested)
            { logger.LogWarning(error, "GPU model residency could not be reconciled. Capacity remains fenced."); }
            var delay = NextIdleProbeDelay(nextExpiry, clock.GetUtcNow(), options.FallbackInterval);
            if (delay == TimeSpan.Zero) continue;
            using var interval = new CancellationTokenSource(delay, clock);
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(interval.Token, stoppingToken);
            try { await _signal.Reader.ReadAsync(wait.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (interval.IsCancellationRequested && !stoppingToken.IsCancellationRequested) { }
        }
    }

    internal static TimeSpan NextIdleProbeDelay(DateTimeOffset? expiry, DateTimeOffset now, TimeSpan fallback)
    {
        if (expiry is not { } due || due - now >= fallback) return fallback;
        return due <= now ? TimeSpan.Zero : due - now;
    }

    internal async ValueTask<DateTimeOffset?> ReconcileIdleAsync(CancellationToken ct, bool stopping = false)
    {
        var now = clock.GetUtcNow();
        DateTimeOffset? next = null;
        foreach (var row in await store.ReadLiveAsync(ct).ConfigureAwait(false))
        {
            if (row.State is not ((int)GpuModelResidencyState.Idle or (int)GpuModelResidencyState.Evicting)) continue;
            var fence = SqlGpuModelResidencyStore.ReadFence(row);
            if (fence.Owner != ownerProbe.Current &&
                ownerProbe.Observe(fence.Owner) != GpuInteractiveOwnerObservation.Exited)
                continue;
            if (row.State == (int)GpuModelResidencyState.Idle)
            {
                if (!stopping && row.IdleUntilUtc is { } idleUntil && idleUntil > now)
                {
                    if (next is null || idleUntil < next) next = idleUntil;
                    continue;
                }
                var evicting = await store.RequestEvictionAsync(fence,
                    stopping ? GpuResidentEvictionReason.Shutdown : GpuResidentEvictionReason.Expired, ct).ConfigureAwait(false);
                if (evicting is null) continue;
                fence = evicting;
            }
            GpuResidentReleaseKind? release = null;
            if (fence.Helper is not null)
            {
                if (await ConfirmHelperEmptyAsync(fence, ct).ConfigureAwait(false))
                    release = GpuResidentReleaseKind.ContainedJobEmpty;
            }
            else if (_allocations.TryGetValue(fence.ResidencyId, out var allocation))
            {
                if (await allocation.Release(ct).ConfigureAwait(false)) release = GpuResidentReleaseKind.NativeSessionDisposed;
            }
            else if (ownerProbe.Observe(fence.Owner) == GpuInteractiveOwnerObservation.Exited)
                release = GpuResidentReleaseKind.HostingProcessExited;
            if (release is { } evidence && await store.ConfirmEvictionAsync(fence, evidence, ct).ConfigureAwait(false))
            {
                Forget(fence.ResidencyId);
                schedulerWake.Notify(GpuSchedulerWakeReason.CapacityReleased);
            }
        }
        return next;
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
        await ReconcileIdleAsync(cancellationToken, stopping: true).ConfigureAwait(false);
    }
}
