using System.Collections.Concurrent;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Infrastructure.Inference.Search;
using FluxKnowledge.Web;
using Microsoft.Extensions.Logging;
using Xunit;

namespace FluxKnowledge.Web.Tests.Composition;

public sealed class BgeCpuWarmupServiceTests
{
    [Fact]
    public async Task Shutdown_deadline_returns_without_releasing_models_or_owner_while_native_work_continues()
    {
        var modelsDisposed = 0;
        var ownerDisposed = 0;
        await using var pool = Pool(() => Interlocked.Increment(ref modelsDisposed),
            () => Interlocked.Increment(ref ownerDisposed));
        var service = new BgeCpuWarmupService(pool, new RecordingLogger());
        await service.StartAsync(CancellationToken.None);
        await pool.WarmAsync(CancellationToken.None);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var work = pool.ExecuteAsync(async (_, _, _) =>
        {
            entered.TrySetResult();
            await release.Task;
            return 1;
        }, CancellationToken.None).AsTask();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            using var deadline = new CancellationTokenSource();
            var stop = service.StopAsync(deadline.Token);
            deadline.Cancel();
            await stop.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(0, modelsDisposed);
            Assert.Equal(0, ownerDisposed);
            Assert.False(pool.DisposeAsync().IsCompleted);
        }
        finally { release.TrySetResult(); }
        Assert.Equal(1, await work);
        await pool.DisposeAsync();
        Assert.Equal(1, modelsDisposed);
        Assert.Equal(1, ownerDisposed);
    }

    [Fact]
    public async Task Shutdown_deadline_during_warmup_keeps_late_cleanup_observed()
    {
        var loading = new TaskCompletionSource<CpuResidentPassageLoad>(TaskCreationOptions.RunContinuationsAsynchronously);
        var logger = new RecordingLogger();
        var pool = new ResidentCpuPassageInference(_ => loading.Task,
            new EmbeddingProfile("test-embedding", 1024), "test-reranker");
        var service = new BgeCpuWarmupService(pool, logger);
        await service.StartAsync(CancellationToken.None);
        using var deadline = new CancellationTokenSource();
        var stop = service.StopAsync(deadline.Token);
        deadline.Cancel();
        try { await stop.WaitAsync(TimeSpan.FromSeconds(5)); }
        finally { loading.TrySetResult(Load(() => throw new IOException("synthetic-native-close"), () => { })); }
        await pool.DisposeAsync();
        await service.StopAsync(CancellationToken.None);
        Assert.Contains(logger.Entries, entry => entry.Exception is AggregateException);
    }

    [Fact]
    public async Task Native_cleanup_failure_is_reported_and_does_not_release_uncertain_owner()
    {
        var ownerDisposed = 0;
        var logger = new RecordingLogger();
        var pool = Pool(() => throw new IOException("synthetic-native-close"),
            () => Interlocked.Increment(ref ownerDisposed));
        var service = new BgeCpuWarmupService(pool, logger);
        await service.StartAsync(CancellationToken.None);
        await pool.WarmAsync(CancellationToken.None);
        await Assert.ThrowsAsync<AggregateException>(() => service.StopAsync(CancellationToken.None));
        Assert.Equal(0, ownerDisposed);
    }

    private static ResidentCpuPassageInference Pool(Action close, Action release) => new(
        _ => Task.FromResult(Load(close, release)), new EmbeddingProfile("test-embedding", 1024), "test-reranker");

    private static CpuResidentPassageLoad Load(Action close, Action release) => new(
        [new CpuPassageInferenceLane(new Embedding(), new Reranker(), close)], new Owner(release));

    private sealed class Owner(Action release) : IDisposable { public void Dispose() => release(); }
    private sealed class Embedding : IEmbeddingProvider
    {
        public ValueTask<EmbeddingResult> CreateEmbeddingAsync(string text, CancellationToken ct) => throw new NotSupportedException();
    }
    private sealed class Reranker : IPassageReranker
    {
        public ValueTask<RerankResult> RerankAsync(string query, IReadOnlyList<RerankPassage> passages, CancellationToken ct) => throw new NotSupportedException();
    }
    private sealed class RecordingLogger : ILogger<BgeCpuWarmupService>
    {
        public ConcurrentQueue<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Entries.Enqueue((logLevel, formatter(state, exception), exception));
    }
}
