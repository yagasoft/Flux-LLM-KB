using System.Data.Common;
using FluxKnowledge.Domain.Gpu;

namespace FluxKnowledge.Application.Gpu;

/// <summary>Only trusted model wrappers may assert release, after all native sessions are disposed.</summary>
public sealed record GpuInteractiveNativeResult<T>(T? Value, bool NativeCapacityReleased, string? RefusalReason = null);

/// <summary>Bounded private work, delivered through the existing scheduler. No model residency.</summary>
public sealed class GpuInteractiveExecutor : IGpuExecutorAdapter, IGpuExecutorRecoveryAdapter
{
    private readonly IGpuInteractiveRequestStore _requests;
    private readonly IGpuExecutorLifecycleSink _lifecycle;
    private readonly IGpuSchedulerStore _scheduler;
    private readonly IGpuSchedulerWakeSignal _wakeSignal;
    private readonly TimeProvider _clock;
    private readonly TimeSpan _executionTimeout;
    private readonly CancellationToken _stopping;
    private readonly GpuInteractiveOwnerRecovery? _ownerRecovery;
    private readonly Guid _instance = Guid.NewGuid();
    private readonly object _sync = new();
    private readonly Dictionary<Guid, Entry> _entries = [];
    private readonly GpuInteractiveExecutionProfile _defaultProfile;
    private readonly HashSet<GpuInteractiveExecutionProfile> _profiles = [];

    public GpuInteractiveExecutor(IGpuInteractiveRequestStore requests, IGpuExecutorLifecycleSink lifecycle,
        IGpuSchedulerStore scheduler, IGpuSchedulerWakeSignal wakeSignal, TimeProvider clock,
        string runtimeKey, string settingsFingerprint, long estimatedBytes, CancellationToken stoppingToken = default,
        GpuInteractiveOwnerRecovery? ownerRecovery = null, TimeSpan? executionTimeout = null)
    {
        GpuSchedulerOpaqueKeyValidator.RequireCanonical(runtimeKey, nameof(runtimeKey), 256);
        GpuSchedulerOpaqueKeyValidator.RequireCanonical(settingsFingerprint, nameof(settingsFingerprint), 256);
        if (estimatedBytes <= 0) throw new ArgumentOutOfRangeException(nameof(estimatedBytes));
        _executionTimeout = executionTimeout ?? TimeSpan.FromSeconds(10);
        if (_executionTimeout < TimeSpan.FromSeconds(10) || _executionTimeout > TimeSpan.FromSeconds(25))
            throw new ArgumentOutOfRangeException(nameof(executionTimeout));
        _requests = requests; _lifecycle = lifecycle; _scheduler = scheduler; _wakeSignal = wakeSignal; _clock = clock;
        _stopping = stoppingToken;
        _ownerRecovery = ownerRecovery;
        _defaultProfile = new(runtimeKey, settingsFingerprint, estimatedBytes);
        _profiles.Add(_defaultProfile);
    }
    public GpuInteractiveExecutor(IGpuInteractiveRequestStore requests, IGpuExecutorLifecycleSink lifecycle,
        IGpuSchedulerStore scheduler, IGpuSchedulerWakeSignal wakeSignal, TimeProvider clock,
        string runtimeKey, string settingsFingerprint, long estimatedBytes,
        IReadOnlyList<GpuInteractiveExecutionProfile> additionalProfiles, CancellationToken stoppingToken = default,
        GpuInteractiveOwnerRecovery? ownerRecovery = null, TimeSpan? executionTimeout = null)
        : this(requests, lifecycle, scheduler, wakeSignal, clock, runtimeKey, settingsFingerprint, estimatedBytes,
            stoppingToken, ownerRecovery, executionTimeout)
    {
        foreach (var profile in additionalProfiles)
        {
            GpuSchedulerOpaqueKeyValidator.RequireCanonical(profile.RuntimeKey, nameof(additionalProfiles), 256);
            GpuSchedulerOpaqueKeyValidator.RequireCanonical(profile.SettingsFingerprint, nameof(additionalProfiles), 256);
            if (profile.EstimatedBytes <= 0) throw new ArgumentException("interactive-profile-capacity-invalid", nameof(additionalProfiles));
            _profiles.Add(profile);
        }
    }
    public string ExecutorKey => $"retrieval-gpu:{_instance:N}";

    public GpuInteractiveOperation<T> StartOwned<T>(Func<GpuOwnedWorkContext, ValueTask<GpuInteractiveNativeResult<T>>> work,
        CancellationToken cancellationToken, GpuInteractiveExecutionProfile? profile = null, bool declineWhenGpuBusy = false) =>
        StartOperation(work, cancellationToken, profile, declineWhenGpuBusy, trackSettlement: true);

    public GpuInteractiveOperation<T> StartOwned<T>(Func<GpuOwnedWorkContext, ValueTask<GpuInteractiveNativeResult<T>>> work,
        CancellationToken cancellationToken, TimeSpan queueWait, GpuInteractiveExecutionProfile? profile = null, bool declineWhenGpuBusy = false) =>
        StartOperation(work, cancellationToken, profile, declineWhenGpuBusy, trackSettlement: true, queueWait);

    public async ValueTask RecoverAsync(CancellationToken cancellationToken)
    {
        if (_ownerRecovery is not null) await _ownerRecovery.RecoverAsync(cancellationToken).ConfigureAwait(false);
    }

    public ValueTask<T> ExecuteAsync<T>(Func<CancellationToken, ValueTask<GpuInteractiveNativeResult<T>>> work, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(work);
        return ExecuteWithOwnershipAsync<T>(context => work(context.CancellationToken), cancellationToken);
    }

    public async ValueTask<T> ExecuteWithOwnershipAsync<T>(Func<GpuOwnedWorkContext, ValueTask<GpuInteractiveNativeResult<T>>> work,
        CancellationToken cancellationToken, bool declineWhenGpuBusy = false) =>
        await StartOperation(work, cancellationToken, null, declineWhenGpuBusy, trackSettlement: false).Result.ConfigureAwait(false);

    private GpuInteractiveOperation<T> StartOperation<T>(Func<GpuOwnedWorkContext, ValueTask<GpuInteractiveNativeResult<T>>> work,
        CancellationToken cancellationToken, GpuInteractiveExecutionProfile? profile, bool declineWhenGpuBusy, bool trackSettlement, TimeSpan? queueWait = null)
    {
        ArgumentNullException.ThrowIfNull(work);
        cancellationToken.ThrowIfCancellationRequested();
        var queuedFor=queueWait??TimeSpan.FromSeconds(2);
        if(queuedFor<=TimeSpan.Zero || queuedFor>TimeSpan.FromSeconds(4) || queuedFor>=_executionTimeout)
            throw new ArgumentOutOfRangeException(nameof(queueWait));
        profile ??= _defaultProfile;
        if (!_profiles.Contains(profile)) throw new ArgumentException("interactive-profile-not-admitted", nameof(profile));
        if (trackSettlement && _requests is not IGpuInteractiveSettlementStore) throw new InvalidOperationException("interactive-settlement-store-required");
        var now = _clock.GetUtcNow();
        var request = new GpuInteractiveHandoffRequest(Guid.NewGuid(), _instance, ExecutorKey, profile.RuntimeKey,
            profile.SettingsFingerprint, profile.EstimatedBytes, now.Add(queuedFor), now.Add(_executionTimeout), declineWhenGpuBusy);
        var entry = new Entry(request, new CancellationTokenSource(_executionTimeout, _clock), cancellationToken, _stopping,
            async context => { var result = await work(context).ConfigureAwait(false); return new(result.Value, result.NativeCapacityReleased, result.RefusalReason); });
        entry.TrackSettlement = trackSettlement;
        lock (_sync)
        {
            if (_entries.Count >= 2)
            {
                entry.Dispose();
                if (declineWhenGpuBusy) throw new GpuInteractiveBusyWithoutHandoffException();
                throw new InvalidOperationException("interactive-queue-full");
            }
            _entries.Add(request.RequestId, entry);
        }
        var result = RunRequestAsync<T>(entry);
        if (trackSettlement) _ = SettleEntryAsync(entry, result);
        return new(request.RequestId, _instance, ExecutorKey, result, entry.Settlement.Task);
    }

    private async Task<T> RunRequestAsync<T>(Entry entry)
    {
        var request = entry.Request;
        var handedOff = false;
        try
        {
            var handoff = await _requests.HandoffInteractiveAsync(request, entry.Cancellation.Token).ConfigureAwait(false);
            if (entry.TrackSettlement && (!handoff.Committed || handoff.MiniTaskId != request.RequestId))
                throw new InvalidOperationException("interactive-handoff-unconfirmed");
            handedOff = true;
            entry.HandoffCommitted = true;
            _wakeSignal.Notify(GpuSchedulerWakeReason.WorkReady);
            var queueRemaining = request.QueueDeadlineUtc - _clock.GetUtcNow();
            if (queueRemaining <= TimeSpan.Zero) throw new TimeoutException("interactive-queue-expired");
            await entry.Started.Task.WaitAsync(queueRemaining, _clock, entry.Cancellation.Token).ConfigureAwait(false);
            return (T)(await entry.Result.Task.WaitAsync(entry.Cancellation.Token).ConfigureAwait(false))!;
        }
        catch (GpuInteractiveBusyWithoutHandoffException) when (!handedOff)
        {
            // The admission transaction refused before insertion. There is no durable request to cancel.
            entry.NoHandoffProven = true;
            Remove(entry);
            throw;
        }
        catch
        {
            entry.Cancellation.Cancel();
            entry.Result.TrySetCanceled(entry.Cancellation.Token);
            entry.CancellationCleanup = CancelAndRemoveQueuedAsync(entry);
            throw;
        }
        finally { entry.ReleaseWaiter(); }
    }

    private async Task SettleEntryAsync(Entry entry, Task result)
    {
        var kind = GpuInteractiveSettlementKind.RetainedUncertain;
        string? observationFailureType=null;
        try
        {
            try { await result.ConfigureAwait(false); } catch (Exception e) when (e is not OutOfMemoryException and not StackOverflowException) { }
            if (entry.CancellationCleanup is { } cancellation) await cancellation.ConfigureAwait(false);
            Task? delivery;
            lock (_sync)
            {
                // Cancellation has established/joined any admitted undelivered work.
                // No later dispatcher may enter native work after this snapshot.
                entry.DeliveryClosed = true;
                delivery = entry.Delivery?.Value;
            }
            if (delivery is not null) await delivery.ConfigureAwait(false);
            if (entry.NoHandoffProven) kind = GpuInteractiveSettlementKind.NoHandoff;
            else
            {
                using var observation = new CancellationTokenSource(TimeSpan.FromSeconds(5), _clock);
                var snapshot = await ((IGpuInteractiveSettlementStore)_requests).ReadInteractiveRequestAsync(
                    entry.Request.RequestId, _instance, observation.Token).ConfigureAwait(false);
                var identity = snapshot.RequestId == entry.Request.RequestId && snapshot.ExecutorInstanceId == _instance;
                var profile = snapshot.RuntimeKey == entry.Request.ModelRuntimeKey && snapshot.SettingsFingerprint == entry.Request.SettingsFingerprint;
                if (identity && !entry.CallbackEntered && snapshot.Handle is null &&
                    snapshot.State == GpuInteractiveRequestState.CancelledBeforeExecution && profile)
                    kind = GpuInteractiveSettlementKind.CancelledBeforeExecution;
                else if (identity && profile && snapshot.Handle == entry.Handle && snapshot.Handle is not null &&
                    snapshot.State == GpuInteractiveRequestState.Released && (!entry.CallbackEntered || entry.NativeReleased))
                    kind = GpuInteractiveSettlementKind.Released;
                else if (identity && snapshot.State == GpuInteractiveRequestState.NotFound && !entry.HandoffCommitted &&
                    entry.QueuedCancellationCompleted && !entry.CallbackEntered && snapshot.Handle is null)
                    // Handoff has returned and exact cancellation passed the admission lock;
                    // no unresolved insertion or future local handoff remains.
                    kind = GpuInteractiveSettlementKind.NoHandoff;
            }
        }
        catch (Exception e) when (e is not OutOfMemoryException and not StackOverflowException)
        { observationFailureType=e.GetType().Name; /* An observation failure never grants capacity. */ }
        entry.Settlement.TrySetResult(new(entry.Request.RequestId, _instance, entry.Handle, kind, entry.CallbackEntered, entry.NativeReleased,observationFailureType));
    }

    public ValueTask DeliverAsync(GpuExecutorBatchHandle handle, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(handle);
        handle.Validate();
        return handle.ExecutorKey == ExecutorKey ? new(DeliverOwnedAsync(handle, cancellationToken)) : ValueTask.CompletedTask;
    }

    private async Task DeliverOwnedAsync(GpuExecutorBatchHandle handle, CancellationToken deliveryCancellation)
    {
        var work = await RetryDurableAsync(() => _requests.ReadInteractiveExecutionAsync(handle, _instance, GpuExecutorDispatchState.PendingDelivery, deliveryCancellation)).ConfigureAwait(false)
            ?? await RetryDurableAsync(() => _requests.ReadInteractiveExecutionAsync(handle, _instance, GpuExecutorDispatchState.Acknowledged, deliveryCancellation)).ConfigureAwait(false);
        var requestId = work?.RequestId;
        if (!requestId.HasValue)
        {
            // Uncertain dispatches cannot execute. An entry bound to this incarnation
            // can still prove that its one native callback never started.
            requestId = (await RetryDurableAsync(() => _requests.ReadInteractiveRecoveryAsync(deliveryCancellation)).ConfigureAwait(false))
                .SingleOrDefault(w => w.ExecutorInstanceId == _instance && w.Handle == handle)?.RequestId;
        }
        if (!requestId.HasValue) return;
        Task delivery;
        lock (_sync)
        {
            if (!_entries.TryGetValue(requestId.Value, out var entry)) return; // Process loss is recovered without replay.
            if (entry.DeliveryClosed) return;
            if (entry.Handle is not null && entry.Handle != handle) return;
            entry.Handle = handle;
            delivery = (entry.Delivery ??= new Lazy<Task>(() => Task.Run(() => ExecuteOwnedAsync(entry, handle)),
                LazyThreadSafetyMode.ExecutionAndPublication)).Value;
        }
        // A dispatch fallback cancellation stops waiting, never frees native ownership.
        await delivery.WaitAsync(deliveryCancellation).ConfigureAwait(false);
    }

    private async Task ExecuteOwnedAsync(Entry entry, GpuExecutorBatchHandle handle)
    {
        var nativeReleased = true; // This one delivery task has not entered the native callback.
        try
        {
            entry.Started.TrySetResult();
            var acknowledged = await RetryDurableAsync(() => _lifecycle.AcknowledgeAsync(new(entry.AcknowledgementId, handle), _stopping)).ConfigureAwait(false);
            if (!acknowledged.Accepted || !acknowledged.Committed) throw new InvalidOperationException("interactive-dispatch-not-owned");
            var owned = await RetryDurableAsync(() => _requests.ReadInteractiveExecutionAsync(handle, _instance, GpuExecutorDispatchState.Acknowledged, _stopping)).ConfigureAwait(false);
            if (owned is null) throw new InvalidOperationException("interactive-dispatch-not-owned");
            GpuInteractiveNativeResult<object?> result;
            if (owned.CancellationRequested || entry.Cancellation.IsCancellationRequested)
                result = new(null, true, "interactive-request-expired");
            else
            {
                nativeReleased = false;
                var context = new GpuOwnedWorkContext(handle, owned.ModelRuntimeKey, owned.SettingsFingerprint, entry.Cancellation.Token);
                bool registeredNativeReleased;
                var callbackReturned = false;
                entry.CallbackEntered = true;
                try
                {
                    result = await entry.Work(context).ConfigureAwait(false);
                    callbackReturned = true;
                }
                finally
                {
                    registeredNativeReleased = context.InvalidateAndReadNativeRelease();
                    entry.RegisteredNativeReleased = registeredNativeReleased;
                    // A thrown callback can still prove disposal through registered leases.
                    // A returned refusal of release remains unconfirmed even if no lease was registered.
                    if (entry.TrackSettlement && !callbackReturned) nativeReleased = registeredNativeReleased;
                }
                result = result with { NativeCapacityReleased = result.NativeCapacityReleased && registeredNativeReleased };
            }
            // Exceptions and deadlines do not prove native memory release.
            if (!result.NativeCapacityReleased) throw new InvalidOperationException("interactive-native-release-unconfirmed");
            nativeReleased = true;
            var success = result.RefusalReason is null && !entry.Cancellation.IsCancellationRequested && _clock.GetUtcNow() < entry.Request.ExecutionDeadlineUtc;
            var disposition = success ? GpuMiniTaskBoundaryDisposition.Completed : GpuMiniTaskBoundaryDisposition.OutcomeUncertain;
            var receipt = new GpuExecutorResultReceipt(entry.ReceiptId, handle, entry.Request.RequestId, disposition, null,
                success ? GpuExecutorEvidenceClass.TaskOutcomeConfirmed : GpuExecutorEvidenceClass.TaskOutcomeUncertainConfirmed);
            var recorded = await RetryDurableAsync(() => _lifecycle.RecordReceiptAsync(receipt, _stopping)).ConfigureAwait(false);
            if (!recorded.Accepted || !recorded.Committed) throw new InvalidOperationException("interactive-receipt-not-owned");
            var callback = new GpuBatchCallback(handle, success ? GpuBatchCallbackKind.Completed : GpuBatchCallbackKind.CapacityReleased,
                [new(entry.Request.RequestId, disposition)], CapacityReleased: true);
            var released = await RetryDurableAsync(() => _lifecycle.HandleCallbackAsync(entry.CallbackId, callback, _stopping)).ConfigureAwait(false);
            if (!released.Accepted || !released.Committed) throw new InvalidOperationException("interactive-capacity-release-not-committed");
            if (success && !entry.Cancellation.IsCancellationRequested) entry.Result.TrySetResult(result.Value);
            else entry.Result.TrySetException(new InvalidOperationException(result.RefusalReason ?? "interactive-request-expired"));
            Remove(entry);
        }
        catch
        {
            entry.Result.TrySetException(new InvalidOperationException("interactive-execution-or-release-unconfirmed"));
            try
            {
                var reservation = (await RetryDurableAsync(() => _scheduler.ReadStaleCapacityReservationsAsync(_clock.GetUtcNow(), _stopping)).ConfigureAwait(false))
                    .SingleOrDefault(r => r.BatchId == handle.BatchId && r.CapacitySlotKey == handle.CapacitySlotKey && r.AdmissionGeneration == handle.AdmissionGeneration);
                var uncertaintyOperation = Guid.NewGuid();
                if (reservation is not null) await RetryDurableAsync(() => _scheduler.MarkCapacityUncertainAsync(uncertaintyOperation, reservation, _stopping)).ConfigureAwait(false);
                if (nativeReleased)
                {
                    // A watchdog may have marked the slot uncertain while native cleanup ran.
                    // Successful disposal is independent proof; reuse the existing reconciliation.
                    var outcomeEvidence = new GpuExecutorTrustedEvidence(Guid.NewGuid(), handle, "retrieval-native-cleanup",
                        _clock.GetUtcNow(), GpuExecutorEvidenceClass.TaskOutcomeUncertainConfirmed);
                    var capacityEvidence = outcomeEvidence with { OperationId = Guid.NewGuid(), EvidenceClass = GpuExecutorEvidenceClass.CapacityReleaseConfirmed };
                    var outcomeRecorded = await RetryDurableAsync(() => _lifecycle.RecordTrustedEvidenceAsync(outcomeEvidence, _stopping)).ConfigureAwait(false);
                    var capacityRecorded = await RetryDurableAsync(() => _lifecycle.RecordTrustedEvidenceAsync(capacityEvidence, _stopping)).ConfigureAwait(false);
                    if (outcomeRecorded.Committed && capacityRecorded.Committed)
                    {
                        var outcomeOperation = Guid.NewGuid();
                        var capacityOperation = Guid.NewGuid();
                        var outcome = await RetryDurableAsync(() => _scheduler.ReconcileTaskOutcomeAsync(outcomeOperation,
                            new(handle, outcomeEvidence.OperationId, entry.Request.RequestId), _stopping)).ConfigureAwait(false);
                        var capacity = await RetryDurableAsync(() => _scheduler.ReconcileCapacityAsync(capacityOperation,
                            new(handle, capacityEvidence.OperationId), _stopping)).ConfigureAwait(false);
                        if (entry.TrackSettlement && (!outcome.Committed || !capacity.Committed))
                            throw new InvalidOperationException("interactive-reconciliation-unconfirmed");
                    }
                    var released = entry.TrackSettlement
                        ? (await RetryDurableAsync(() => ((IGpuInteractiveSettlementStore)_requests).ReadInteractiveRequestAsync(entry.Request.RequestId, _instance, _stopping)).ConfigureAwait(false)).State == GpuInteractiveRequestState.Released
                        : !(await RetryDurableAsync(() => _requests.ReadInteractiveRecoveryAsync(_stopping)).ConfigureAwait(false)).Any(w => w.RequestId == entry.Request.RequestId && w.Handle == handle);
                    if (released)
                    {
                        _wakeSignal.Notify(GpuSchedulerWakeReason.Reconciliation | GpuSchedulerWakeReason.CapacityReleased);
                        Remove(entry);
                    }
                }
            }
            catch { /* Preserve the reservation; process-exit recovery remains the release authority. */ }
        }
        finally { entry.NativeReleased = nativeReleased && (!entry.CallbackEntered || entry.RegisteredNativeReleased); }
    }

    private async Task<T> RetryDurableAsync<T>(Func<ValueTask<T>> operation)
    {
        while (true)
        {
            try { return await operation().ConfigureAwait(false); }
            catch (DbException) when (!_stopping.IsCancellationRequested)
            {
                // Same operation identity after response loss; native execution is never repeated.
                await Task.Delay(TimeSpan.FromSeconds(1), _clock, _stopping).ConfigureAwait(false);
            }
        }
    }

    private async Task CancelAndRemoveQueuedAsync(Entry entry)
    {
        try
        {
            await RetryDurableAsync(() => _requests.CancelInteractiveAsync(entry.Request.RequestId, _instance, _stopping)).ConfigureAwait(false);
            entry.QueuedCancellationCompleted = true;
            if (entry.TrackSettlement)
            {
                var snapshot = await RetryDurableAsync(() => ((IGpuInteractiveSettlementStore)_requests).ReadInteractiveRequestAsync(
                    entry.Request.RequestId, _instance, _stopping)).ConfigureAwait(false);
                Task? exactDelivery = null;
                lock (_sync)
                {
                    if (entry.Delivery is null && snapshot.Handle is { } handle && snapshot.State == GpuInteractiveRequestState.Owned && !entry.DeliveryClosed)
                    {
                        entry.Handle = handle;
                        exactDelivery = (entry.Delivery = new Lazy<Task>(() => Task.Run(() => ExecuteOwnedAsync(entry, handle)),
                            LazyThreadSafetyMode.ExecutionAndPublication)).Value;
                    }
                    else if (entry.Delivery is null && snapshot.State is GpuInteractiveRequestState.NotFound or GpuInteractiveRequestState.CancelledBeforeExecution)
                        Remove(entry);
                }
                if (exactDelivery is not null) await exactDelivery.ConfigureAwait(false);
                return;
            }
            var owned = await RetryDurableAsync(() => _requests.ReadInteractiveRecoveryAsync(_stopping)).ConfigureAwait(false);
            GpuExecutorBatchHandle? undelivered = null;
            lock (_sync)
            {
                if (entry.Delivery is null)
                {
                    undelivered = owned.SingleOrDefault(w => w.RequestId == entry.Request.RequestId && w.ExecutorInstanceId == _instance)?.Handle;
                    if (undelivered is null) Remove(entry);
                }
            }
            // The dispatch reader skips DeliveryUncertain. Cancellation must initiate
            // this same one-owner cleanup path when an admitted request was never delivered.
            if (undelivered is not null) await DeliverOwnedAsync(undelivered, _stopping).ConfigureAwait(false);
        }
        catch { /* SQL deadlines refuse queued work; retain local ownership if uncertain. */ }
    }

    private void Remove(Entry entry)
    {
        lock (_sync) { if (_entries.Remove(entry.Request.RequestId)) entry.ReleaseOwner(); }
    }

    private sealed class Entry : IDisposable
    {
        private readonly CancellationTokenSource _deadline;
        private int _released;
        public Entry(GpuInteractiveHandoffRequest request, CancellationTokenSource deadline, CancellationToken caller, CancellationToken stopping,
            Func<GpuOwnedWorkContext, ValueTask<GpuInteractiveNativeResult<object?>>> work)
        {
            Request = request; _deadline = deadline; Work = work;
            Cancellation = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token, caller, stopping);
        }
        public GpuInteractiveHandoffRequest Request { get; }
        public Func<GpuOwnedWorkContext, ValueTask<GpuInteractiveNativeResult<object?>>> Work { get; }
        public CancellationTokenSource Cancellation { get; }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<object?> Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public GpuExecutorBatchHandle? Handle { get; set; }
        public Lazy<Task>? Delivery { get; set; }
        public bool TrackSettlement, HandoffCommitted, NoHandoffProven, QueuedCancellationCompleted, DeliveryClosed;
        public bool CallbackEntered, RegisteredNativeReleased, NativeReleased;
        public Task? CancellationCleanup { get; set; }
        public TaskCompletionSource<GpuInteractiveSettlement> Settlement { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Guid AcknowledgementId { get; } = Guid.NewGuid();
        public Guid ReceiptId { get; } = Guid.NewGuid();
        public Guid CallbackId { get; } = Guid.NewGuid();
        public void ReleaseWaiter() { if ((Interlocked.Or(ref _released, 1) | 1) == 3) Dispose(); }
        public void ReleaseOwner() { if ((Interlocked.Or(ref _released, 2) | 2) == 3) Dispose(); }
        public void Dispose() { Cancellation.Dispose(); _deadline.Dispose(); }
    }
}
