using System.Threading.Channels;
using FluxKnowledge.Application.Gpu;
using FluxKnowledge.Infrastructure.SqlServer.Workers;
using FluxKnowledge.Integration.Tests.Gpu;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Workers;

public sealed class GpuExecutorDispatchRecoveryServiceTests
{
    [Fact]
    public async Task Test_only_fake_acknowledges_the_original_handle_through_the_lifecycle_sink()
    {
        var handle = CreateHandle("executor-a");
        var sink = new CapturingLifecycleSink();
        var fake = new DeterministicFakeGpuExecutor(
            "executor-a",
            sink,
            DeterministicFakeGpuExecutorMode.Acknowledge,
            () => Guid.Parse("30000000-0000-0000-0000-000000000001"));

        await fake.DeliverAsync(handle, CancellationToken.None);

        Assert.Equal(handle, Assert.Single(fake.DeliveredHandles));
        Assert.Equal(handle, Assert.Single(sink.Acknowledgements).Handle);
    }

    [Fact]
    public async Task Unresponsive_test_fake_cancels_without_a_lifecycle_or_store_mutation()
    {
        var handle = CreateHandle("executor-a");
        var store = new PendingDispatchStore(handle);
        var sink = new CapturingLifecycleSink();
        var fake = new DeterministicFakeGpuExecutor(
            "executor-a",
            sink,
            DeterministicFakeGpuExecutorMode.Unresponsive,
            Guid.NewGuid);
        await using var provider = CreateProvider(store, TimeProvider.System, fake);
        var service = provider.GetRequiredService<GpuExecutorDispatchRecoveryService>();

        await service.StartAsync(CancellationToken.None);
        await fake.DeliveryStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await service.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Empty(sink.Acknowledgements);
        Assert.Equal(0, store.MutationCount);
    }

    [Fact]
    public async Task Bounded_delivery_timeout_cancels_an_unresponsive_fake_without_a_lifecycle_or_store_mutation()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.Parse("2026-08-05T12:00:00+00:00"));
        var store = new PendingDispatchStore(CreateHandle("executor-a"));
        var sink = new CapturingLifecycleSink();
        var fake = new DeterministicFakeGpuExecutor(
            "executor-a",
            sink,
            DeterministicFakeGpuExecutorMode.Unresponsive,
            Guid.NewGuid);
        await using var provider = CreateProvider(store, clock, fake, TimeSpan.FromMinutes(1));
        var service = provider.GetRequiredService<GpuExecutorDispatchRecoveryService>();

        await service.StartAsync(CancellationToken.None);
        await fake.DeliveryStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await clock.ScheduledTimers.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));

        clock.AdvanceBy(TimeSpan.FromMinutes(1));

        await fake.DeliveryCancelled.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Empty(sink.Acknowledgements);
        Assert.Equal(0, store.MutationCount);
        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Startup_delivers_the_original_pending_handle_only_to_the_ordinal_matching_adapter()
    {
        var handle = CreateHandle("executor-a");
        var store = new PendingDispatchStore(handle);
        var adapter = new RecordingAdapter("executor-a");
        await using var provider = CreateProvider(store, TimeProvider.System, adapter);
        var service = provider.GetRequiredService<GpuExecutorDispatchRecoveryService>();

        await service.StartAsync(CancellationToken.None);
        try
        {
            Assert.Equal(handle, await adapter.Deliveries.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.Equal(0, store.MutationCount);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Local_prompt_replays_the_same_pending_handle_after_a_lost_delivery_prompt()
    {
        var handle = CreateHandle("executor-a");
        var store = new PendingDispatchStore(handle);
        var adapter = new RecordingAdapter("executor-a");
        await using var provider = CreateProvider(store, TimeProvider.System, adapter);
        var signal = provider.GetRequiredService<ChannelGpuExecutorDispatchSignal>();
        var service = provider.GetRequiredService<GpuExecutorDispatchRecoveryService>();

        await service.StartAsync(CancellationToken.None);
        try
        {
            Assert.Equal(handle, await adapter.Deliveries.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2)));

            signal.Notify();

            Assert.Equal(handle, await adapter.Deliveries.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.Equal(0, store.MutationCount);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task New_service_instance_rereads_pending_dispatch_after_restart()
    {
        var handle = CreateHandle("executor-a");
        var store = new PendingDispatchStore(handle);
        var unmatched = new RecordingAdapter("executor-b");
        await using (var firstProvider = CreateProvider(store, TimeProvider.System, unmatched))
        {
            var firstService = firstProvider.GetRequiredService<GpuExecutorDispatchRecoveryService>();
            await firstService.StartAsync(CancellationToken.None);
            await store.ReadsReached(1).WaitAsync(TimeSpan.FromSeconds(2));
            await firstService.StopAsync(CancellationToken.None);
        }

        var matching = new RecordingAdapter("executor-a");
        await using var secondProvider = CreateProvider(store, TimeProvider.System, matching);
        var secondService = secondProvider.GetRequiredService<GpuExecutorDispatchRecoveryService>();
        await secondService.StartAsync(CancellationToken.None);
        try
        {
            Assert.Equal(handle, await matching.Deliveries.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.Equal(0, store.MutationCount);
        }
        finally
        {
            await secondService.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Unmatched_adapter_leaves_the_pending_dispatch_untouched()
    {
        var store = new PendingDispatchStore(CreateHandle("executor-a"));
        var adapter = new RecordingAdapter("executor-b");
        await using var provider = CreateProvider(store, TimeProvider.System, adapter);
        var service = provider.GetRequiredService<GpuExecutorDispatchRecoveryService>();

        await service.StartAsync(CancellationToken.None);
        try
        {
            await store.ReadsReached(1).WaitAsync(TimeSpan.FromSeconds(2));
            Assert.False(adapter.Deliveries.Reader.TryRead(out _));
            Assert.Equal(0, store.MutationCount);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Fallback_elapsed_time_only_rereads_the_same_pending_handle_without_lifecycle_mutation()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.Parse("2026-08-05T12:00:00+00:00"));
        var handle = CreateHandle("executor-a");
        var store = new PendingDispatchStore(handle);
        var adapter = new RecordingAdapter("executor-a");
        await using var provider = CreateProvider(store, clock, adapter, TimeSpan.FromMinutes(1));
        var service = provider.GetRequiredService<GpuExecutorDispatchRecoveryService>();

        await service.StartAsync(CancellationToken.None);
        try
        {
            Assert.Equal(handle, await adapter.Deliveries.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2)));
            await clock.ScheduledTimers.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));

            clock.AdvanceBy(TimeSpan.FromMinutes(1));

            Assert.Equal(handle, await adapter.Deliveries.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.Equal(0, store.MutationCount);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Adapter_exception_leaves_the_pending_dispatch_untouched_for_a_later_recovery_pass()
    {
        var handle = CreateHandle("executor-a");
        var store = new PendingDispatchStore(handle);
        var adapter = new RecordingAdapter("executor-a") { ThrowOnDelivery = true };
        await using var provider = CreateProvider(store, TimeProvider.System, adapter);
        var signal = provider.GetRequiredService<ChannelGpuExecutorDispatchSignal>();
        var service = provider.GetRequiredService<GpuExecutorDispatchRecoveryService>();

        await service.StartAsync(CancellationToken.None);
        try
        {
            await adapter.AttemptsReached(1).WaitAsync(TimeSpan.FromSeconds(2));
            adapter.ThrowOnDelivery = false;
            signal.Notify();

            Assert.Equal(handle, await adapter.Deliveries.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.Equal(0, store.MutationCount);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Coalescing_signal_is_payload_free_and_preserves_one_pending_prompt()
    {
        var signal = new ChannelGpuExecutorDispatchSignal();

        signal.Notify();
        signal.Notify();

        await signal.WaitAsync(CancellationToken.None);
        Assert.False(signal.TryConsume());
    }

    private static ServiceProvider CreateProvider(
        PendingDispatchStore store,
        TimeProvider timeProvider,
        IGpuExecutorAdapter adapter,
        TimeSpan? fallbackInterval = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(timeProvider);
        services.AddSingleton(new GpuSchedulerOptions(
            1,
            1,
            TimeSpan.FromMinutes(1),
            fallbackInterval ?? TimeSpan.FromHours(1),
            TimeSpan.FromHours(1)));
        services.AddSingleton(store);
        services.AddScoped<IGpuExecutorDispatchStore>(provider => provider.GetRequiredService<PendingDispatchStore>());
        services.AddSingleton<ChannelGpuExecutorDispatchSignal>();
        services.AddSingleton<IGpuExecutorDispatchSignal>(provider => provider.GetRequiredService<ChannelGpuExecutorDispatchSignal>());
        services.AddSingleton<IGpuExecutorAdapter>(adapter);
        services.AddSingleton<GpuExecutorDispatchRecoveryService>();
        return services.BuildServiceProvider();
    }

    private static GpuExecutorBatchHandle CreateHandle(string executorKey) => new(
        Guid.Parse("10000000-0000-0000-0000-000000000001"),
        "slot-a",
        executorKey,
        1,
        Guid.Parse("20000000-0000-0000-0000-000000000001"));

    private sealed class PendingDispatchStore(GpuExecutorBatchHandle handle) : IGpuExecutorDispatchStore
    {
        private readonly TaskCompletionSource _firstRead = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int MutationCount { get; private set; }

        public ValueTask<IReadOnlyList<GpuExecutorBatchHandle>> ReadPendingDispatchesAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _firstRead.TrySetResult();

            return ValueTask.FromResult<IReadOnlyList<GpuExecutorBatchHandle>>([handle]);
        }

        public Task ReadsReached(int target)
        {
            ArgumentOutOfRangeException.ThrowIfNotEqual(target, 1);
            return _firstRead.Task;
        }

        public ValueTask<GpuExecutorDispatchMutationResult> AcknowledgeAsync(GpuExecutorAcknowledgement acknowledgement, CancellationToken cancellationToken) => Mutate();
        public ValueTask<GpuExecutorDispatchMutationResult> MarkDeliveryUncertainAsync(GpuExecutorDeliveryUncertainty uncertainty, CancellationToken cancellationToken) => Mutate();
        public ValueTask<GpuExecutorDispatchMutationResult> RecordReceiptAsync(GpuExecutorResultReceipt receipt, CancellationToken cancellationToken) => Mutate();
        public ValueTask<GpuExecutorDispatchMutationResult> RecordTrustedEvidenceAsync(GpuExecutorTrustedEvidence evidence, CancellationToken cancellationToken) => Mutate();

        private ValueTask<GpuExecutorDispatchMutationResult> Mutate()
        {
            MutationCount++;
            return ValueTask.FromResult(new GpuExecutorDispatchMutationResult(true, true));
        }
    }

    private sealed class RecordingAdapter(string executorKey) : IGpuExecutorAdapter
    {
        private readonly List<TaskCompletionSource> _attemptTargets = [];
        private int _attempts;

        public string ExecutorKey { get; } = executorKey;
        public bool ThrowOnDelivery { get; set; }
        public Channel<GpuExecutorBatchHandle> Deliveries { get; } = Channel.CreateUnbounded<GpuExecutorBatchHandle>();

        public ValueTask DeliverAsync(GpuExecutorBatchHandle handle, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var attempts = Interlocked.Increment(ref _attempts);
            foreach (var target in _attemptTargets.Where((_, index) => index < attempts))
            {
                target.TrySetResult();
            }

            if (ThrowOnDelivery)
            {
                throw new InvalidOperationException("Scripted delivery failure.");
            }

            Deliveries.Writer.TryWrite(handle);
            return ValueTask.CompletedTask;
        }

        public Task AttemptsReached(int target)
        {
            if (Volatile.Read(ref _attempts) >= target)
            {
                return Task.CompletedTask;
            }

            while (_attemptTargets.Count < target)
            {
                _attemptTargets.Add(new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
            }

            return _attemptTargets[target - 1].Task;
        }
    }

    private sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private readonly object _sync = new();
        private readonly List<ManualTimer> _timers = [];
        private DateTimeOffset _now = now;

        public Channel<DateTimeOffset> ScheduledTimers { get; } = Channel.CreateUnbounded<DateTimeOffset>();

        public override DateTimeOffset GetUtcNow()
        {
            lock (_sync)
            {
                return _now;
            }
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            if (period != Timeout.InfiniteTimeSpan)
            {
                throw new NotSupportedException("Only one-shot timers are supported.");
            }

            lock (_sync)
            {
                var dueAtUtc = _now.Add(dueTime);
                var timer = new ManualTimer(callback, state, dueAtUtc);
                _timers.Add(timer);
                ScheduledTimers.Writer.TryWrite(dueAtUtc);
                return timer;
            }
        }

        public void AdvanceBy(TimeSpan duration)
        {
            List<(TimerCallback Callback, object? State)> callbacks = [];
            lock (_sync)
            {
                _now = _now.Add(duration);
                foreach (var timer in _timers)
                {
                    if (timer.TryTake(_now, out var callback, out var state))
                    {
                        callbacks.Add((callback, state));
                    }
                }
            }

            foreach (var (callback, state) in callbacks)
            {
                callback(state);
            }
        }

        private sealed class ManualTimer(TimerCallback callback, object? state, DateTimeOffset dueAtUtc) : ITimer
        {
            private int _completed;

            public bool Change(TimeSpan dueTime, TimeSpan period) => throw new NotSupportedException();
            public void Dispose() => Interlocked.Exchange(ref _completed, 1);
            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }

            public bool TryTake(DateTimeOffset now, out TimerCallback takenCallback, out object? takenState)
            {
                if (dueAtUtc > now || Interlocked.CompareExchange(ref _completed, 1, 0) != 0)
                {
                    takenCallback = null!;
                    takenState = null;
                    return false;
                }

                takenCallback = callback;
                takenState = state;
                return true;
            }
        }
    }

    private sealed class CapturingLifecycleSink : IGpuExecutorLifecycleSink
    {
        public List<GpuExecutorAcknowledgement> Acknowledgements { get; } = [];

        public ValueTask<GpuExecutorDispatchMutationResult> AcknowledgeAsync(
            GpuExecutorAcknowledgement acknowledgement,
            CancellationToken cancellationToken)
        {
            Acknowledgements.Add(acknowledgement);
            return ValueTask.FromResult(new GpuExecutorDispatchMutationResult(true, true));
        }

        public ValueTask<GpuExecutorDispatchMutationResult> MarkDeliveryUncertainAsync(GpuExecutorDeliveryUncertainty uncertainty, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<GpuExecutorDispatchMutationResult> RecordReceiptAsync(GpuExecutorResultReceipt receipt, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<GpuExecutorDispatchMutationResult> RecordTrustedEvidenceAsync(GpuExecutorTrustedEvidence evidence, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<GpuBatchCallbackResult> HandleCallbackAsync(Guid operationId, GpuBatchCallback callback, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
