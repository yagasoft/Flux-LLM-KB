using System.Diagnostics;
using System.Diagnostics.Tracing;
using FluxKnowledge.Application.Gpu;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Domain.Gpu;
using FluxKnowledge.Infrastructure.Inference.Search;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.SqlServer.Workers;
using Microsoft.EntityFrameworkCore;

namespace FluxKnowledge.Web;

/// <summary>Explicit staging-only driver. It never registers the production scheduler or changes its executor API.</summary>
internal sealed class StagingSqlGpuRankBackend : IExpandedGpuRankBackend
{
    private const long CapacityBytes = 8L * 1024 * 1024 * 1024;
    private readonly IDbContextFactory<FluxKnowledgeDbContext> _factory;
    private readonly IBgeGpuModelFactory _models;
    private readonly Action _verifyTrialPermit;
    private readonly TimeProvider _clock;
    private readonly CancellationToken _stopping;
    private readonly Func<CancellationToken,ValueTask>? _beforeTerminalObservation;
    private readonly Action? _beforeCohortAdmission;
    private readonly object _sync = new();
    private readonly Queue<StagingGpuRankReceipt> _receipts = new();
    private bool _uncertain;
    private int _active;

    internal StagingSqlGpuRankBackend(IDbContextFactory<FluxKnowledgeDbContext> factory, IBgeGpuModelFactory models,
        Action verifyTrialPermit, TimeProvider clock, CancellationToken stopping,
        Func<CancellationToken,ValueTask>? beforeTerminalObservation = null, Action? beforeCohortAdmission = null)
    {
        if (models.Precision != BgeRerankerPrecision.Fp16Internal) throw new ArgumentException("Explicit FP16 staging factory required.", nameof(models));
        _factory = factory; _models = models; _verifyTrialPermit = verifyTrialPermit; _clock = clock; _stopping = stopping;
        _beforeTerminalObservation = beforeTerminalObservation;
        _beforeCohortAdmission = beforeCohortAdmission;
    }

    public string RerankerFingerprint => BgeOfflineModels.GetRerankerFingerprint(BgeRerankerPrecision.Fp16Internal);
    internal StagingGpuRankReceipt[] Snapshot() { lock (_sync) return _receipts.ToArray(); }

    public async Task<ExpandedGpuRankOutcome> RunAsync(Guid cohortId,
        Func<IPassageReranker, CancellationToken, ValueTask<IReadOnlyDictionary<Guid, RerankResult>>> score,
        CancellationToken cancellationToken)
    {
        lock (_sync)
            if (_uncertain) return new(new Dictionary<Guid, RerankResult>(), ExpandedGpuRankSettlement.RetainedUncertain, new PassageInferenceOwnershipException());
        // These checks precede executor creation and any durable handoff.
        try { cancellationToken.ThrowIfCancellationRequested(); _verifyTrialPermit(); _beforeCohortAdmission?.Invoke(); }
        catch (Exception exception) { return new(new Dictionary<Guid, RerankResult>(), ExpandedGpuRankSettlement.Settled, exception); }
        if (Interlocked.CompareExchange(ref _active, 1, 0) != 0)
            return new(new Dictionary<Guid, RerankResult>(), ExpandedGpuRankSettlement.Settled, new PassageRetrievalRefusalException("busy"));
        var timer = Stopwatch.StartNew();
        using var nativeEvents = new NativeEvents();
        var store = new SqlGpuSchedulerStore(_factory, timeProvider: _clock, interactiveExecutionTimeout: TimeSpan.FromSeconds(20));
        var executor = new GpuInteractiveExecutor(store, new Lifecycle(store), store, new ChannelGpuSchedulerWakeSignal(), _clock,
            BgeOfflineModels.GetGpuRuntimeKey(_models.Precision), BgeOfflineModels.GpuSettingsFingerprint, CapacityBytes,
            stoppingToken: _stopping, executionTimeout: TimeSpan.FromSeconds(20));
        Task? delivery = null;
        Task<IReadOnlyDictionary<Guid, RerankResult>>? execution = null;
        GpuExecutorBatchHandle? handle = null;
        Guid? requestId = null;
        bool callbackSettled = false, nativeDisposed = false, terminalVerified = false, noHandoff = false;
        double ownerEnteredMs = 0, loadMs = 0, scoringMs = 0, unloadMs = 0;
        Exception? callbackFailure = null, failure = null;
        IReadOnlyDictionary<Guid, RerankResult> results = new Dictionary<Guid, RerankResult>();
        var settlement = ExpandedGpuRankSettlement.RetainedUncertain;
        try
        {
            execution = executor.ExecuteWithOwnershipAsync<IReadOnlyDictionary<Guid, RerankResult>>(async owner =>
            {
                ownerEnteredMs = timer.Elapsed.TotalMilliseconds;
                BgeGpuModelLease<IPassageReranker>? model = null;
                IReadOnlyDictionary<Guid, RerankResult>? scored = null;
                try
                {
                    _verifyTrialPermit();
                    var phase = Stopwatch.StartNew();
                    try { model = await _models.OpenRerankerAsync(owner).ConfigureAwait(false); }
                    finally { loadMs=phase.Elapsed.TotalMilliseconds; }
                    phase.Restart();
                    try { scored = await score(model.Model, owner.CancellationToken).ConfigureAwait(false); }
                    finally { scoringMs=phase.Elapsed.TotalMilliseconds; }
                }
                catch (Exception exception) { callbackFailure = exception; }
                finally
                {
                    var unload=Stopwatch.StartNew();
                    try { model?.Dispose(); nativeDisposed = owner.NativeCapacityReleased; }
                    catch (Exception cleanup) { callbackFailure = callbackFailure is null ? cleanup : new AggregateException(callbackFailure, cleanup); }
                    finally { unloadMs=unload.Elapsed.TotalMilliseconds; }
                    callbackSettled = true;
                }
                return new(scored, nativeDisposed, callbackFailure is null ? null : "staging-rank-failed");
            }, cancellationToken, declineWhenGpuBusy: true).AsTask();

            // A new executor incarnation owns exactly one request. No foreign request can satisfy this read.
            for (var attempt = 0; ; attempt++)
            {
                if (execution.IsCompleted) { await execution.ConfigureAwait(false); throw new InvalidOperationException("staging-dispatch-missing"); }
                await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
                requestId = await db.GpuMiniTasks.AsNoTracking().Where(t => t.RequiredExecutorKey == executor.ExecutorKey)
                    .Select(t => (Guid?)t.Id).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
                if (requestId.HasValue) break;
                if (attempt >= 100) throw new TimeoutException("staging-handoff-not-observed");
                await Task.Delay(10, cancellationToken).ConfigureAwait(false);
            }
            var admission = await store.RunAdmissionRoundAsync(Guid.NewGuid(), GpuSchedulerWakeReason.WorkReady,
                new(1, CapacityBytes, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(10)),
                (candidate, _) => ValueTask.FromResult(Allowed(candidate, executor.ExecutorKey)
                    ? new GpuAdmissionDecision(GpuAdmissionDisposition.Admit, "gpu-0", "staging-expanded-cohort", null, executor.ExecutorKey)
                    : new GpuAdmissionDecision(GpuAdmissionDisposition.Busy, null, null, null)), cancellationToken).ConfigureAwait(false);
            if (!admission.Committed || admission.Disposition != GpuAdmissionDisposition.Admit)
                throw new InvalidOperationException("staging-exact-admission-refused");
            handle = (await store.ReadPendingDispatchesAsync(cancellationToken).ConfigureAwait(false))
                .Single(h => h.ExecutorKey == executor.ExecutorKey);
            // Cancellation may stop Execute waiting, but cannot abandon this exact delivery.
            delivery = executor.DeliverAsync(handle, CancellationToken.None).AsTask();
            results = await execution.ConfigureAwait(false);
            await delivery.ConfigureAwait(false);
            if (!callbackSettled || !nativeDisposed || callbackFailure is not null)
                throw new PassageInferenceOwnershipException(callbackFailure);
            if (_beforeTerminalObservation is not null) await _beforeTerminalObservation(cancellationToken).ConfigureAwait(false);
            await VerifyTerminalAsync(requestId.Value, handle, executor.ExecutorKey, cancellationToken).ConfigureAwait(false);
            terminalVerified = true;
            settlement = ExpandedGpuRankSettlement.Settled;
        }
        catch (GpuInteractiveBusyWithoutHandoffException)
        {
            noHandoff = true;
            failure = new PassageRetrievalRefusalException("busy");
            settlement = ExpandedGpuRankSettlement.Settled;
        }
        catch (Exception exception) { failure = callbackFailure is null ? exception : new AggregateException(exception, callbackFailure); }
        finally
        {
            // Observe actual owned operations even after an early Execute error. Generic executor
            // errors still have unjoined queued cleanup, so a clean-looking DB never reopens this backend.
            if (execution is not null)
                try { await execution.ConfigureAwait(false); }
                catch (Exception observed) { failure ??= observed; }
            if (delivery is not null)
                try { await delivery.ConfigureAwait(false); }
                catch (Exception observed) { failure = failure is null ? observed : new AggregateException(failure, observed); }
            lock (_sync)
            {
                _uncertain |= settlement == ExpandedGpuRankSettlement.RetainedUncertain;
                if (_receipts.Count == 128) _receipts.Dequeue();
                _receipts.Enqueue(new(cohortId, requestId, executor.ExecutorKey, handle, callbackSettled, nativeDisposed,
                    terminalVerified, noHandoff, settlement.ToString(), failure?.GetType().Name, timer.Elapsed.TotalMilliseconds,
                    ownerEnteredMs,loadMs,scoringMs,unloadMs,nativeEvents.Counts()));
            }
            Interlocked.Exchange(ref _active, 0);
        }
        return new(settlement == ExpandedGpuRankSettlement.Settled && failure is null
            ? results : new Dictionary<Guid,RerankResult>(), settlement, failure);
    }

    private static bool Allowed(GpuBatchCandidate candidate, string executor) =>
        candidate.PriorityLane == GpuPriorityLane.InteractiveRetrieval && candidate.RequiredExecutorKey == executor &&
        candidate.ModelRuntimeKey == BgeOfflineModels.GetGpuRuntimeKey(BgeRerankerPrecision.Fp16Internal) &&
        candidate.SettingsFingerprint == BgeOfflineModels.GpuSettingsFingerprint && candidate.ItemCount == 1 && candidate.EstimatedBytes == CapacityBytes;

    private async Task VerifyTerminalAsync(Guid requestId, GpuExecutorBatchHandle handle, string executor, CancellationToken ct)
    {
        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var task = await db.GpuMiniTasks.AsNoTracking().SingleAsync(t => t.Id == requestId, ct).ConfigureAwait(false);
        var batch = await db.GpuBatches.AsNoTracking().SingleAsync(b => b.Id == handle.BatchId, ct).ConfigureAwait(false);
        var dispatch = await db.GpuExecutorDispatches.AsNoTracking().SingleAsync(d => d.DispatchId == handle.DispatchId, ct).ConfigureAwait(false);
        var receipt = await db.GpuExecutorResultReceipts.AsNoTracking().SingleAsync(r => r.DispatchId == handle.DispatchId, ct).ConfigureAwait(false);
        var slot = await db.GpuCapacitySlots.AsNoTracking().SingleAsync(s => s.SlotKey == handle.CapacitySlotKey, ct).ConfigureAwait(false);
        if (task.RequiredExecutorKey != executor || task.ParentJobId is not null || task.BatchId != handle.BatchId ||
            task.ExecutionState != (int)GpuMiniTaskExecutionState.Completed || task.AdmissionGeneration != handle.AdmissionGeneration ||
            batch.State != (int)GpuBatchState.Completed || batch.ItemCount != 1 || batch.AdmissionGeneration != handle.AdmissionGeneration ||
            batch.CapacitySlotKey != handle.CapacitySlotKey || dispatch.BatchId != handle.BatchId || dispatch.ExecutorKey != executor ||
            dispatch.AdmissionGeneration != handle.AdmissionGeneration || dispatch.CapacitySlotKey != handle.CapacitySlotKey ||
            dispatch.State != (int)GpuExecutorDispatchState.Terminal || receipt.BatchId != handle.BatchId || receipt.MiniTaskId != requestId ||
            receipt.ExecutorKey != executor || receipt.AdmissionGeneration != handle.AdmissionGeneration ||
            receipt.EvidenceClass != (int)GpuExecutorEvidenceClass.TaskOutcomeConfirmed || receipt.Disposition != (int)GpuMiniTaskBoundaryDisposition.Completed ||
            slot.State != (int)GpuCapacitySlotState.Available || slot.ActiveBatchId is not null || slot.OwnerKey is not null ||
            await db.GpuMiniTasks.AnyAsync(t => t.BatchId == handle.BatchId && t.Id != requestId, ct).ConfigureAwait(false) ||
            await db.GpuExecutorEvidence.AnyAsync(e => e.DispatchId == handle.DispatchId, ct).ConfigureAwait(false))
            throw new PassageInferenceOwnershipException();
    }

    private sealed class Lifecycle(SqlGpuSchedulerStore store) : IGpuExecutorLifecycleSink
    {
        public ValueTask<GpuExecutorDispatchMutationResult> AcknowledgeAsync(GpuExecutorAcknowledgement v, CancellationToken ct) => store.AcknowledgeAsync(v, ct);
        public ValueTask<GpuExecutorDispatchMutationResult> MarkDeliveryUncertainAsync(GpuExecutorDeliveryUncertainty v, CancellationToken ct) => store.MarkDeliveryUncertainAsync(v, ct);
        public ValueTask<GpuExecutorDispatchMutationResult> RecordReceiptAsync(GpuExecutorResultReceipt v, CancellationToken ct) => store.RecordReceiptAsync(v, ct);
        public ValueTask<GpuExecutorDispatchMutationResult> RecordTrustedEvidenceAsync(GpuExecutorTrustedEvidence v, CancellationToken ct) => store.RecordTrustedEvidenceAsync(v, ct);
        public ValueTask<GpuBatchCallbackResult> HandleCallbackAsync(Guid op, GpuBatchCallback v, CancellationToken ct) => store.ApplyBatchCallbackAsync(op, v, ct);
    }

    internal sealed class NativeEvents : EventListener
    {
        private readonly HashSet<string> _running = [];
        private int _started, _settled;
        private bool _invalid;
        protected override void OnEventSourceCreated(EventSource source)
        {
            if(source.Name=="FluxKnowledge-HybridSearch") EnableEvents(source,EventLevel.Informational);
        }
        protected override void OnEventWritten(EventWrittenEventArgs item)
        {
            if(item.EventId!=3 || item.Payload is not {Count:>=5} fields || fields[1] as string!="gpu-reranker" ||
                fields[2] as string!="native-run" || _running is null) return;
            lock(_running)
            {
                if(fields[0] is not string id || id.Length>128 || _started>=128 || _settled>=128) {_invalid=true;return;}
                if(fields[4] as string=="begin") {_started++;_invalid|=_running.Count!=0;_invalid|=!_running.Add(id);}
                else if(fields[4] as string=="settled") {_settled++;_invalid|=!_running.Remove(id);}
                else _invalid=true;
            }
        }
        internal StagingGpuNativeCounts Counts()
        {
            lock(_running) return new(_started,_settled,_invalid || _started!=_settled || _running.Count!=0);
        }
    }
}

internal sealed record StagingGpuRankReceipt(Guid CohortId, Guid? RequestId, string ExecutorKey, GpuExecutorBatchHandle? Handle,
    bool CallbackSettled, bool NativeDisposed, bool TerminalVerified, bool NoHandoff, string Settlement, string? FailureType, double Milliseconds,
    double OwnerEnteredMilliseconds,double LoadMilliseconds,double ScoringMilliseconds,double UnloadMilliseconds,StagingGpuNativeCounts NativeCalls);

internal sealed record StagingGpuNativeCounts(int Started,int Settled,bool InvalidOrUnsettled);
