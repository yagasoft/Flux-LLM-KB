using FluxKnowledge.Application.Gpu;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Search;
using System.Diagnostics;
using System.Diagnostics.Tracing;

namespace FluxKnowledge.Infrastructure.Inference.Search;

/// <summary>Uses the normal interactive executor and joins exact native/durable settlement for each cohort.</summary>
public sealed class BgeExpandedGpuRankBackend : IExpandedGpuRankBackend
{
    private readonly GpuInteractiveExecutor _executor;
    private readonly IBgeGpuModelFactory _models;
    private readonly TimeProvider _clock;
    private readonly object _sync = new();
    private readonly Queue<ExpandedGpuRankReceipt> _receipts = new();
    private bool _uncertain;
    private int _active;
    public static GpuInteractiveExecutionProfile Profile { get; } = new(
        BgeOfflineModels.GetGpuRuntimeKey(BgeRerankerPrecision.Fp16Internal), BgeOfflineModels.GpuSettingsFingerprint, 8L * 1024 * 1024 * 1024);
    public BgeExpandedGpuRankBackend(GpuInteractiveExecutor executor, IBgeGpuModelFactory models, TimeProvider? clock = null)
    {
        if (models.Precision != BgeRerankerPrecision.Fp16Internal) throw new ArgumentException("expanded-rank-profile-mismatch", nameof(models));
        _executor = executor; _models = models; _clock = clock ?? TimeProvider.System;
    }
    public string RerankerFingerprint => BgeOfflineModels.GetRerankerFingerprint(BgeRerankerPrecision.Fp16Internal);
    public ExpandedGpuRankReceipt[] Snapshot() { lock (_sync) return _receipts.ToArray(); }

    public async Task<ExpandedGpuRankOutcome> RunAsync(Guid cohortId,
        Func<IPassageReranker, CancellationToken, ValueTask<IReadOnlyDictionary<Guid, RerankResult>>> score, CancellationToken cancellationToken)
    {
        lock (_sync)
            if (_uncertain) return new(new Dictionary<Guid, RerankResult>(), ExpandedGpuRankSettlement.RetainedUncertain, new PassageInferenceOwnershipException());
        if (Interlocked.CompareExchange(ref _active, 1, 0) != 0)
            return new(new Dictionary<Guid, RerankResult>(), ExpandedGpuRankSettlement.Settled, new PassageRetrievalRefusalException("busy"));
        var started = _clock.GetTimestamp();
        GpuInteractiveOperation<IReadOnlyDictionary<Guid, RerankResult>>? operation = null;
        GpuInteractiveSettlement? settled = null;
        Exception? failure = null, callbackFailure = null;
        IReadOnlyDictionary<Guid, RerankResult> results = new Dictionary<Guid, RerankResult>();
        bool callbackSettled = false, nativeDisposed = false;
        double ownerMs = 0, loadMs = 0, scoreMs = 0, unloadMs = 0;
        var nativeCalls = new GpuRankNativeCounts(0, 0, false);
        try
        {
            operation = _executor.StartOwned<IReadOnlyDictionary<Guid, RerankResult>>(async owner =>
            {
                ownerMs = _clock.GetElapsedTime(started).TotalMilliseconds;
                BgeGpuModelLease<IPassageReranker>? model = null;
                IReadOnlyDictionary<Guid, RerankResult>? scored = null;
                using var calls = new NativeCalls();
                try
                {
                    var phase = _clock.GetTimestamp();
                    try { model = await _models.OpenRerankerAsync(owner).ConfigureAwait(false); }
                    finally { loadMs = _clock.GetElapsedTime(phase).TotalMilliseconds; }
                    phase = _clock.GetTimestamp();
                    try { scored = await score(model.Model, owner.CancellationToken).ConfigureAwait(false); }
                    finally { scoreMs = _clock.GetElapsedTime(phase).TotalMilliseconds; }
                }
                catch (Exception exception) { callbackFailure = exception; }
                finally
                {
                    var phase = _clock.GetTimestamp();
                    try { model?.Dispose(); nativeDisposed = model is not null && owner.NativeCapacityReleased; }
                    catch (Exception cleanup) { callbackFailure = callbackFailure is null ? cleanup : new AggregateException(callbackFailure, cleanup); }
                    finally { unloadMs = _clock.GetElapsedTime(phase).TotalMilliseconds; callbackSettled = true; nativeCalls = calls.Snapshot(); }
                }
                return new(scored, owner.NativeCapacityReleased, callbackFailure is null ? null : "expanded-rank-failed");
            }, cancellationToken, TimeSpan.FromSeconds(4), Profile, declineWhenGpuBusy: false);
            results = await operation.Result.ConfigureAwait(false);
        }
        catch (GpuInteractiveBusyWithoutHandoffException) { failure = new PassageRetrievalRefusalException("busy"); }
        catch (InvalidOperationException exception) when (exception.Message == "interactive-queue-full")
        { failure = new PassageRetrievalRefusalException("busy"); }
        catch (Exception exception) { failure = callbackFailure ?? exception; }
        finally
        {
            // Result cancellation/failure never closes ownership; this join has no caller cancellation.
            if (operation is not null) settled = await operation.Settlement.ConfigureAwait(false);
            var uncertain = settled?.Kind == GpuInteractiveSettlementKind.RetainedUncertain;
            lock (_sync)
            {
                _uncertain |= uncertain;
                if (_receipts.Count == 128) _receipts.Dequeue();
                _receipts.Enqueue(new(cohortId, operation?.RequestId, _executor.ExecutorKey, settled?.Handle,
                    callbackSettled, nativeDisposed, settled?.Kind == GpuInteractiveSettlementKind.Released,
                    operation is null || settled?.Kind is GpuInteractiveSettlementKind.NoHandoff or GpuInteractiveSettlementKind.CancelledBeforeExecution,
                    uncertain ? nameof(ExpandedGpuRankSettlement.RetainedUncertain) : nameof(ExpandedGpuRankSettlement.Settled),
                    failure?.GetType().Name, _clock.GetElapsedTime(started).TotalMilliseconds, ownerMs, loadMs, scoreMs, unloadMs, nativeCalls,
                    settled?.Kind.ToString(),settled?.ObservationFailureType));
            }
            Interlocked.Exchange(ref _active, 0);
        }
        var state = settled?.Kind == GpuInteractiveSettlementKind.RetainedUncertain
            ? ExpandedGpuRankSettlement.RetainedUncertain : ExpandedGpuRankSettlement.Settled;
        return new(state == ExpandedGpuRankSettlement.Settled && failure is null ? results : new Dictionary<Guid, RerankResult>(), state, failure);
    }

    private sealed class NativeCalls : EventListener
    {
        private readonly HashSet<string> _running = [];
        private int _started, _settled; private bool _invalid;
        protected override void OnEventSourceCreated(EventSource source)
        { if (source.Name == "FluxKnowledge-HybridSearch") EnableEvents(source, EventLevel.Informational); }
        protected override void OnEventWritten(EventWrittenEventArgs item)
        {
            if (item.EventId != 3 || item.Payload is not { Count: >= 5 } p || p[1] as string != "gpu-reranker" || p[2] as string != "native-run" || _running is null) return;
            lock (_running)
            {
                if (p[0] is not string id || id.Length > 128 || _started >= 128 || _settled >= 128) { _invalid = true; return; }
                if (p[4] as string == "begin") { _started++; _invalid |= _running.Count != 0 || !_running.Add(id); }
                else if (p[4] as string == "settled") { _settled++; _invalid |= !_running.Remove(id); }
                else _invalid = true;
            }
        }
        internal GpuRankNativeCounts Snapshot()
        { lock (_running) return new(_started, _settled, _invalid || _started != _settled || _running.Count != 0); }
    }
}

public sealed record ExpandedGpuRankReceipt(Guid CohortId, Guid? RequestId, string ExecutorKey, GpuExecutorBatchHandle? Handle,
    bool CallbackSettled, bool NativeDisposed, bool TerminalVerified, bool NoHandoff, string Settlement, string? FailureType, double Milliseconds,
    double OwnerEnteredMilliseconds, double LoadMilliseconds, double ScoringMilliseconds, double UnloadMilliseconds, GpuRankNativeCounts NativeCalls,
    string? OwnershipKind=null,string? OwnershipObservationFailureType=null);
public sealed record GpuRankNativeCounts(int Started, int Settled, bool InvalidOrUnsettled);
