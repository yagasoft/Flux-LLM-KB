using FluxKnowledge.Application.Ports;
using System.Collections.Concurrent;

namespace FluxKnowledge.Infrastructure.Inference.Search;

/// <summary>One CPU model callback owns this lane until its native shards have settled.</summary>
public sealed class CpuPassageInferenceLane(IEmbeddingProvider embedding, IPassageReranker reranker, Action dispose) : IDisposable
{
    private int _disposed;
    public IEmbeddingProvider Embedding { get; } = embedding;
    public IPassageReranker Reranker { get; } = reranker;
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0) dispose();
    }
}

/// <summary>The owner lease outlives every lane, including native work continuing after caller cancellation.</summary>
public sealed class CpuResidentPassageLoad(IReadOnlyList<CpuPassageInferenceLane> lanes, IDisposable owner) : IDisposable
{
    private static readonly ConcurrentBag<IDisposable> UncertainOwners = [];
    private int _disposed;
    public IReadOnlyList<CpuPassageInferenceLane> Lanes { get; } = lanes;
    internal static void RetainUncertainOwner(IDisposable owner) => UncertainOwners.Add(owner);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        var errors = new List<Exception>();
        foreach (var lane in Lanes)
        {
            try { lane.Dispose(); }
            catch (Exception exception) { errors.Add(exception); }
        }
        if (errors.Count > 0)
        {
            // A failed native disposal cannot prove that capacity is gone. Retain the OS lock until process exit.
            RetainUncertainOwner(owner);
            throw new CpuNativeReleaseUnconfirmedException(errors.ToArray());
        }
        try { owner.Dispose(); }
        catch (Exception failure)
        {
            RetainUncertainOwner(owner);
            throw new CpuNativeReleaseUnconfirmedException(failure);
        }
    }
}

/// <summary>Warm, offline CPU sessions with a hard bound on concurrent model callbacks.</summary>
public sealed class ResidentCpuPassageInference : IScheduledPassageInference, IAsyncDisposable, IDisposable
{
    private readonly Func<CancellationToken, Task<CpuResidentPassageLoad>> _load;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly object _sync = new();
    private readonly Queue<CpuPassageInferenceLane> _available = new();
    private readonly List<PendingAdmission> _pending = [];
    private readonly int _maximumPendingRequests;
    private CpuResidentPassageLoad? _loaded;
    private Task? _warm;
    private Task? _disposeTask;
    private TaskCompletionSource _idle = CompletedSignal();
    private int _active;
    private bool _closed, _disposed;

    public ResidentCpuPassageInference(Func<CancellationToken, Task<CpuResidentPassageLoad>> load,
        EmbeddingProfile embeddingProfile, string rerankerFingerprint) : this(load, embeddingProfile, rerankerFingerprint, 0) { }

    public ResidentCpuPassageInference(Func<CancellationToken, Task<CpuResidentPassageLoad>> load,
        EmbeddingProfile embeddingProfile, string rerankerFingerprint, int maximumPendingRequests)
    {
        if (maximumPendingRequests is < 0 or > 1) throw new ArgumentOutOfRangeException(nameof(maximumPendingRequests));
        _maximumPendingRequests = maximumPendingRequests;
        _load = load;
        EmbeddingProfile = embeddingProfile;
        RerankerFingerprint = rerankerFingerprint;
    }

    public EmbeddingProfile EmbeddingProfile { get; }
    public string RerankerFingerprint { get; }
    public int ActiveSearchCount { get { lock (_sync) return _active; } }
    public int PendingSearchCount { get { lock (_sync) return _pending.Count; } }
    public int MaximumPendingRequests => _maximumPendingRequests;

    public Task WarmAsync(CancellationToken cancellationToken)
    {
        Task warm;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            _warm ??= Task.Run(() => LoadAsync(_shutdown.Token), CancellationToken.None);
            warm = _warm;
        }
        return warm.WaitAsync(cancellationToken);
    }

    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        var loaded = await _load(cancellationToken).ConfigureAwait(false);
        if (loaded.Lanes.Count == 0)
        {
            loaded.Dispose();
            throw new InvalidOperationException("cpu-search-no-lanes");
        }
        lock (_sync)
        {
            if (!_closed)
            {
                _loaded = loaded;
                foreach (var lane in loaded.Lanes) _available.Enqueue(lane);
                return;
            }
        }
        loaded.Dispose();
    }

    public async ValueTask<T> ExecuteAsync<T>(
        Func<IEmbeddingProvider, IPassageReranker, CancellationToken, ValueTask<T>> work,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(work);
        cancellationToken.ThrowIfCancellationRequested();
        Task<CpuPassageInferenceLane> assigned;
        PendingAdmission? pending = null;
        lock (_sync)
        {
            if (_closed || _warm?.IsFaulted == true) throw new PassageRetrievalRefusalException("unavailable");
            if (_warm?.IsCompletedSuccessfully != true)
                throw new PassageRetrievalRefusalException("busy");
            if (_available.Count > 0)
            {
                assigned = Task.FromResult(_available.Dequeue());
                if (_active++ == 0) _idle = new(TaskCreationOptions.RunContinuationsAsynchronously);
            }
            else
            {
                if (_pending.Count >= _maximumPendingRequests) throw new PassageRetrievalRefusalException("busy");
                pending = new();
                _pending.Add(pending);
                assigned = pending.Completion.Task;
            }
        }
        // Registration disposal can wait for its callback; never dispose it under _sync.
        using var registration = pending is null ? default : cancellationToken.Register(() => CancelPending(pending, cancellationToken));
        var lane = await assigned.ConfigureAwait(false);
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
            // ONNX Run is synchronous. A caller deadline cannot free this lane before Run returns.
            return await Task.Run(async () =>
            {
                linked.Token.ThrowIfCancellationRequested();
                return await work(lane.Embedding, lane.Reranker, linked.Token).ConfigureAwait(false);
            },
                CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            lock (_sync)
            {
                if (!_closed && _pending.Count > 0)
                {
                    var next = _pending[0];
                    _pending.RemoveAt(0);
                    // This reservation remains active until its own finally. Never signal
                    // idle between settled callback and assigned-but-not-started callback.
                    next.Completion.TrySetResult(lane);
                }
                else
                {
                    if (!_closed) _available.Enqueue(lane);
                    if (--_active == 0) _idle.TrySetResult();
                }
            }
        }
    }

    private void CancelPending(PendingAdmission pending, CancellationToken cancellationToken)
    {
        lock (_sync)
            if (_pending.Remove(pending)) pending.Completion.TrySetCanceled(cancellationToken);
        // A granted reservation alone owns its lane and returns it in ExecuteAsync.finally.
    }

    private sealed class PendingAdmission
    {
        internal TaskCompletionSource<CpuPassageInferenceLane> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public ValueTask DisposeAsync()
    {
        lock (_sync)
        {
            _disposeTask ??= Task.Run(DisposeCoreAsync);
            return new ValueTask(_disposeTask);
        }
    }

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    private async Task DisposeCoreAsync()
    {
        Task? warm;
        Task idle;
        lock (_sync)
        {
            _closed = true;
            foreach (var pending in _pending)
                pending.Completion.TrySetException(new PassageRetrievalRefusalException("unavailable"));
            _pending.Clear();
            warm = _warm;
            idle = _idle.Task;
        }
        try
        {
            var failures = new List<Exception>();
            try { _shutdown.Cancel(); }
            catch (AggregateException failure) { failures.Add(failure); }
            if (warm is not null)
            {
                try { await warm.ConfigureAwait(false); }
                catch (CpuNativeReleaseUnconfirmedException failure) { failures.Add(failure); }
                catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException) { }
            }
            await idle.ConfigureAwait(false);
            CpuResidentPassageLoad? loaded;
            lock (_sync)
            {
                if (_disposed) return;
                _disposed = true;
                loaded = _loaded;
            }
            try { loaded?.Dispose(); }
            catch (CpuNativeReleaseUnconfirmedException failure) { failures.Add(failure); }
            if (failures.Any(failure => failure is CpuNativeReleaseUnconfirmedException))
                throw new CpuNativeReleaseUnconfirmedException(failures.ToArray());
            if (failures.Count > 0)
                throw new AggregateException("cpu-search-shutdown-callback-failed", failures);
        }
        finally { _shutdown.Dispose(); }
    }

    private static TaskCompletionSource CompletedSignal()
    {
        var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        signal.SetResult();
        return signal;
    }
}
