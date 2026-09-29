using FluxKnowledge.Application.Ports;
using System.Collections.Concurrent;

namespace FluxKnowledge.Infrastructure.Inference.Search;

/// <summary>One complete CPU search owns this lane until its callback and native shards have settled.</summary>
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
            throw new AggregateException("cpu-search-native-release-unconfirmed", errors);
        }
        try { owner.Dispose(); }
        catch { RetainUncertainOwner(owner); throw; }
    }
}

/// <summary>Warm, offline CPU sessions with a hard bound on concurrent complete searches.</summary>
public sealed class ResidentCpuPassageInference : IScheduledPassageInference, IAsyncDisposable, IDisposable
{
    private readonly Func<CancellationToken, Task<CpuResidentPassageLoad>> _load;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly object _sync = new();
    private readonly Queue<CpuPassageInferenceLane> _available = new();
    private CpuResidentPassageLoad? _loaded;
    private Task? _warm;
    private Task? _disposeTask;
    private TaskCompletionSource _idle = CompletedSignal();
    private int _active;
    private bool _closed, _disposed;

    public ResidentCpuPassageInference(Func<CancellationToken, Task<CpuResidentPassageLoad>> load,
        EmbeddingProfile embeddingProfile, string rerankerFingerprint)
    {
        _load = load;
        EmbeddingProfile = embeddingProfile;
        RerankerFingerprint = rerankerFingerprint;
    }

    public EmbeddingProfile EmbeddingProfile { get; }
    public string RerankerFingerprint { get; }

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
        CpuPassageInferenceLane lane;
        lock (_sync)
        {
            if (_closed || _warm?.IsFaulted == true) throw new PassageRetrievalRefusalException("unavailable");
            if (_warm?.IsCompletedSuccessfully != true || _available.Count == 0)
                throw new PassageRetrievalRefusalException("busy");
            lane = _available.Dequeue();
            if (_active++ == 0) _idle = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
            // ONNX Run is synchronous. A caller deadline cannot free this lane before Run returns.
            return await Task.Run(async () =>
                await work(lane.Embedding, lane.Reranker, linked.Token).ConfigureAwait(false),
                CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            lock (_sync)
            {
                if (!_closed) _available.Enqueue(lane);
                if (--_active == 0) _idle.TrySetResult();
            }
        }
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
            warm = _warm;
            idle = _idle.Task;
        }
        _shutdown.Cancel();
        if (warm is not null)
        {
            try { await warm.ConfigureAwait(false); }
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
        finally { _shutdown.Dispose(); }
    }

    private static TaskCompletionSource CompletedSignal()
    {
        var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        signal.SetResult();
        return signal;
    }
}
