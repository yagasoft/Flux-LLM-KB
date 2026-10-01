using FluxKnowledge.Application.Gpu;
using FluxKnowledge.Domain.Gpu;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using FluxKnowledge.Infrastructure.SqlServer.Workers;
using FluxKnowledge.Infrastructure.Inference.Search;
using FluxKnowledge.Integration.Tests.Models;
using FluxKnowledge.Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Gpu;

public sealed class GpuInteractiveExecutorTests : IAsyncLifetime
{
    // Cancellation may finish after the caller returns; never share that database with the next test.
    private readonly NativeSqlServerFixture fixture = new();
    public Task InitializeAsync() => fixture.InitializeAsync();
    public Task DisposeAsync() => fixture.DisposeAsync();

    [NativeSqlServerFact]
    public async Task Conditional_executor_busy_refusal_does_not_create_or_cancel_gpu_work()
    {
        await SqlTestData.ClearPipelineAsync(fixture);
        var factory = SqlTestData.CreateFactory(fixture);
        var store = new SqlGpuSchedulerStore(factory);
        await using (var setup = await factory.CreateDbContextAsync())
        {
            setup.GpuCapacitySlots.Add(new GpuCapacitySlotEntity
                { SlotKey = "gpu-0", State = (int)GpuCapacitySlotState.Reserved, UpdatedAtUtc = DateTimeOffset.UtcNow });
            await setup.SaveChangesAsync();
        }
        var executor = new GpuInteractiveExecutor(store, new Lifecycle(store), store, new ChannelGpuSchedulerWakeSignal(),
            TimeProvider.System, "synthetic-retrieval-v1", "synthetic-settings-v1", 10);
        await Assert.ThrowsAsync<GpuInteractiveBusyWithoutHandoffException>(() =>
            executor.ExecuteWithOwnershipAsync<int>(_ => throw new InvalidOperationException("Native callback ran"),
                CancellationToken.None, declineWhenGpuBusy: true).AsTask());
        await using var verify = await factory.CreateDbContextAsync();
        Assert.Empty(await verify.GpuMiniTasks.ToArrayAsync());
        Assert.Empty(await verify.GpuBatches.ToArrayAsync());
    }

    [NativeSqlServerFact]
    public async Task Twenty_second_owner_can_complete_after_ten_seconds_without_early_capacity_release()
    {
        await SqlTestData.ClearPipelineAsync(fixture);
        var factory = SqlTestData.CreateFactory(fixture);
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        var store = new SqlGpuSchedulerStore(factory, timeProvider: clock,
            interactiveExecutionTimeout: TimeSpan.FromSeconds(20));
        await using (var setup = await factory.CreateDbContextAsync())
        {
            setup.GpuCapacitySlots.Add(new GpuCapacitySlotEntity { SlotKey = "gpu-0", State = 0, UpdatedAtUtc = clock.GetUtcNow() });
            await setup.SaveChangesAsync();
        }
        var executor = new GpuInteractiveExecutor(store, new Lifecycle(store), store, new ChannelGpuSchedulerWakeSignal(),
            clock, "synthetic-retrieval-v1", "synthetic-settings-v1", 10,
            executionTimeout: TimeSpan.FromSeconds(20));
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishNative = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var result = executor.ExecuteWithOwnershipAsync<int>(async ownership =>
        {
            started.TrySetResult();
            await finishNative.Task;
            return new(42, true, ownership.CancellationToken.IsCancellationRequested ? "expired" : null);
        }, CancellationToken.None).AsTask();
        await using var waiting = await factory.CreateDbContextAsync();
        for (var attempt = 0; await waiting.GpuMiniTasks.CountAsync() == 0 && attempt < 50; attempt++) await Task.Delay(10);
        Assert.Equal(clock.GetUtcNow().AddSeconds(20), (await waiting.GpuMiniTasks.SingleAsync()).ExecutionDeadlineUtc);
        var options = new GpuSchedulerOptions(4, 1024, TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(1));
        await store.RunAdmissionRoundAsync(Guid.NewGuid(), GpuSchedulerWakeReason.WorkReady, options,
            (candidate, _) => ValueTask.FromResult(new GpuAdmissionDecision(GpuAdmissionDisposition.Admit, "gpu-0", "owner", null, candidate.RequiredExecutorKey)), CancellationToken.None);
        var delivery = executor.DeliverAsync(Assert.Single(await store.ReadPendingDispatchesAsync(CancellationToken.None)), CancellationToken.None).AsTask();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        clock.Advance(TimeSpan.FromSeconds(11));
        Assert.False(result.IsCompleted);
        await using (var beforeCleanup = await factory.CreateDbContextAsync())
            Assert.Equal((int)GpuCapacitySlotState.Reserved, (await beforeCleanup.GpuCapacitySlots.SingleAsync()).State);
        finishNative.SetResult();
        await delivery.WaitAsync(TimeSpan.FromSeconds(8));
        Assert.Equal(42, await result);
        await using var afterCleanup = await factory.CreateDbContextAsync();
        Assert.Equal((int)GpuCapacitySlotState.Available, (await afterCleanup.GpuCapacitySlots.SingleAsync()).State);
    }

    [NativeSqlServerFact]
    public async Task Twenty_second_owner_expiry_keeps_capacity_until_native_cleanup()
    {
        await SqlTestData.ClearPipelineAsync(fixture);
        var factory = SqlTestData.CreateFactory(fixture);
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        var store = new SqlGpuSchedulerStore(factory, timeProvider: clock,
            interactiveExecutionTimeout: TimeSpan.FromSeconds(20));
        await using (var setup = await factory.CreateDbContextAsync())
        {
            setup.GpuCapacitySlots.Add(new GpuCapacitySlotEntity { SlotKey = "gpu-0", State = 0, UpdatedAtUtc = clock.GetUtcNow() });
            await setup.SaveChangesAsync();
        }
        var executor = new GpuInteractiveExecutor(store, new Lifecycle(store), store, new ChannelGpuSchedulerWakeSignal(),
            clock, "synthetic-retrieval-v1", "synthetic-settings-v1", 10,
            executionTimeout: TimeSpan.FromSeconds(20));
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishNative = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var result = executor.ExecuteWithOwnershipAsync<int>(async _ =>
        {
            started.TrySetResult();
            await finishNative.Task;
            return new(42, true);
        }, CancellationToken.None).AsTask();
        await using var waiting = await factory.CreateDbContextAsync();
        for (var attempt = 0; await waiting.GpuMiniTasks.CountAsync() == 0 && attempt < 50; attempt++) await Task.Delay(10);
        var options = new GpuSchedulerOptions(4, 1024, TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(1));
        await store.RunAdmissionRoundAsync(Guid.NewGuid(), GpuSchedulerWakeReason.WorkReady, options,
            (candidate, _) => ValueTask.FromResult(new GpuAdmissionDecision(GpuAdmissionDisposition.Admit, "gpu-0", "owner", null, candidate.RequiredExecutorKey)), CancellationToken.None);
        var delivery = executor.DeliverAsync(Assert.Single(await store.ReadPendingDispatchesAsync(CancellationToken.None)), CancellationToken.None).AsTask();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        clock.Advance(TimeSpan.FromSeconds(20));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => result);
        await using (var beforeCleanup = await factory.CreateDbContextAsync())
            Assert.Equal((int)GpuCapacitySlotState.Reserved, (await beforeCleanup.GpuCapacitySlots.SingleAsync()).State);
        finishNative.SetResult();
        await delivery.WaitAsync(TimeSpan.FromSeconds(8));
        await using var afterCleanup = await factory.CreateDbContextAsync();
        Assert.Equal((int)GpuCapacitySlotState.Available, (await afterCleanup.GpuCapacitySlots.SingleAsync()).State);
        Assert.Equal((int)GpuMiniTaskExecutionState.OutcomeUncertain, (await afterCleanup.GpuMiniTasks.SingleAsync()).ExecutionState);
    }
    [NativeSqlServerFact]
    public async Task Two_requests_sharing_one_trace_remain_distinct_through_owned_native_batch_measurements()
    {
        using var trace = new HybridSearchTraceListener();
        await SqlTestData.ClearPipelineAsync(fixture);
        var factory = SqlTestData.CreateFactory(fixture);
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        var store = new SqlGpuSchedulerStore(factory, timeProvider: clock);
        await using var context = await factory.CreateDbContextAsync();
        context.GpuCapacitySlots.Add(new GpuCapacitySlotEntity { SlotKey = "gpu-0", State = 0, UpdatedAtUtc = clock.GetUtcNow() });
        await context.SaveChangesAsync();
        var executor = new GpuInteractiveExecutor(store, new Lifecycle(store), store, new ChannelGpuSchedulerWakeSignal(),
            clock, BgeOfflineModels.GpuRuntimeKey, BgeOfflineModels.GpuSettingsFingerprint, 10);
        var inference = new BgeScheduledPassageInference(executor, new BgeGpuInferenceSession(new BgeGpuInferenceTests.RecordingModels()));
        var traceId = System.Diagnostics.ActivityTraceId.CreateRandom().ToString();
        async Task<string> RequestAsync()
        {
            using var activity = new System.Diagnostics.Activity("HTTP search").SetIdFormat(System.Diagnostics.ActivityIdFormat.W3C)
                .SetParentId("00-" + traceId + "-" + System.Diagnostics.ActivitySpanId.CreateRandom() + "-01").Start();
            var spanId = activity.SpanId.ToString();
            await inference.ExecuteAsync(async (embedding, reranker, ct) =>
            {
                await embedding.CreateEmbeddingAsync("query", ct);
                await reranker.RerankAsync("query", [new(8, "passage")], ct);
                return 42;
            }, CancellationToken.None);
            return spanId;
        }
        var first = RequestAsync();
        var second = RequestAsync();
        for (var attempt = 0; await context.GpuMiniTasks.CountAsync() < 2 && attempt < 50; attempt++) await Task.Delay(10);
        Assert.Equal(2, await context.GpuMiniTasks.CountAsync());
        var options = new GpuSchedulerOptions(4, 1024, TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(1));
        for (var index = 0; index < 2; index++)
        {
            await store.RunAdmissionRoundAsync(Guid.NewGuid(), GpuSchedulerWakeReason.WorkReady, options,
                (candidate, _) => ValueTask.FromResult(new GpuAdmissionDecision(GpuAdmissionDisposition.Admit, "gpu-0", "owner", null, candidate.RequiredExecutorKey)), CancellationToken.None);
            await executor.DeliverAsync(Assert.Single(await store.ReadPendingDispatchesAsync(CancellationToken.None)), CancellationToken.None);
        }
        var spans = await Task.WhenAll(first, second);
        var mappings = trace.Events.Where(entry => entry.Id == 4 && Equals(entry["traceId"], traceId)).ToArray();
        Assert.Equal(2, mappings.Length);
        Assert.All(mappings, entry => Assert.Contains("spanId", entry.Names));
        Assert.Equal(spans.Order(), mappings.Select(entry => (string)entry["spanId"]!).Order());
        Assert.Equal(2, mappings.Select(entry => entry["batchId"]).Distinct().Count());
        foreach (var mapping in mappings)
            Assert.Equal(6, trace.Events.Count(entry => entry.Id == 3 && Equals(entry["batchId"], mapping["batchId"])));
        Assert.Equal((int)GpuCapacitySlotState.Available, await context.GpuCapacitySlots.AsNoTracking().Select(slot => slot.State).SingleAsync());
    }

    [NativeSqlServerTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Scheduler_runs_embedding_then_reranking_and_retains_capacity_if_native_disposal_fails(bool disposalFails)
    {
        await SqlTestData.ClearPipelineAsync(fixture);
        var factory = SqlTestData.CreateFactory(fixture);
        var store = new SqlGpuSchedulerStore(factory);
        await using (var setup = await factory.CreateDbContextAsync())
        {
            setup.GpuCapacitySlots.Add(new GpuCapacitySlotEntity { SlotKey = "gpu-0", State = 0, UpdatedAtUtc = DateTimeOffset.UtcNow });
            await setup.SaveChangesAsync();
        }
        var models = new BgeGpuInferenceTests.RecordingModels { Failure = disposalFails ? "dispose" : null };
        var session = new BgeGpuInferenceSession(models);
        var executor = new GpuInteractiveExecutor(store, new Lifecycle(store), store, new ChannelGpuSchedulerWakeSignal(),
            TimeProvider.System, BgeOfflineModels.GpuRuntimeKey, BgeOfflineModels.GpuSettingsFingerprint, 10);
        var response = executor.ExecuteWithOwnershipAsync(context => session.ExecuteSearchAsync(context, async (embedding, reranker, ct) =>
        {
            await embedding.CreateEmbeddingAsync("English query", ct);
            return await reranker.RerankAsync("English query", [new(12, "coherent passage"), new(9, "another passage")], ct);
        }), CancellationToken.None).AsTask();
        await using var verify = await factory.CreateDbContextAsync();
        for (var attempt = 0; await verify.GpuMiniTasks.CountAsync() == 0 && attempt < 50; attempt++) await Task.Delay(10);
        var options = new GpuSchedulerOptions(4, 1024, TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(1));
        await store.RunAdmissionRoundAsync(Guid.NewGuid(), GpuSchedulerWakeReason.WorkReady, options,
            (candidate, _) => ValueTask.FromResult(new GpuAdmissionDecision(GpuAdmissionDisposition.Admit, "gpu-0", "owner", null, candidate.RequiredExecutorKey)), CancellationToken.None);
        var handle = Assert.Single(await store.ReadPendingDispatchesAsync(CancellationToken.None));
        await executor.DeliverAsync(handle, CancellationToken.None);
        if (disposalFails)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => response);
            Assert.DoesNotContain("ranking.open", models.Events);
            Assert.Empty(await verify.GpuExecutorResultReceipts.ToArrayAsync());
        }
        else
        {
            Assert.Equal([12L, 9L], (await response).Scores.Select(score => score.PassageId));
            Assert.Equal(["embedding.open", "embedding.run", "embedding.dispose", "ranking.open", "ranking.run", "ranking.dispose"], models.Events);
            Assert.Single(await verify.GpuExecutorResultReceipts.ToArrayAsync());
        }
        Assert.Equal(disposalFails ? (int)GpuCapacitySlotState.Uncertain : (int)GpuCapacitySlotState.Available,
            await verify.GpuCapacitySlots.AsNoTracking().Select(slot => slot.State).SingleAsync());
    }

    [NativeSqlServerFact]
    public async Task Callback_cannot_claim_release_while_a_registered_native_allocation_is_unconfirmed()
    {
        await SqlTestData.ClearPipelineAsync(fixture);
        var factory = SqlTestData.CreateFactory(fixture);
        var store = new SqlGpuSchedulerStore(factory);
        await using (var setup = await factory.CreateDbContextAsync())
        {
            setup.GpuCapacitySlots.Add(new GpuCapacitySlotEntity { SlotKey = "gpu-0", State = 0, UpdatedAtUtc = DateTimeOffset.UtcNow });
            await setup.SaveChangesAsync();
        }
        var executor = new GpuInteractiveExecutor(store, new Lifecycle(store), store, new ChannelGpuSchedulerWakeSignal(),
            TimeProvider.System, BgeOfflineModels.GpuRuntimeKey, BgeOfflineModels.GpuSettingsFingerprint, 10);
        var response = executor.ExecuteWithOwnershipAsync<int>(context =>
        {
            context.BeginNativeAllocation();
            return ValueTask.FromResult(new GpuInteractiveNativeResult<int>(42, true));
        }, CancellationToken.None).AsTask();
        await using var verify = await factory.CreateDbContextAsync();
        for (var attempt = 0; await verify.GpuMiniTasks.CountAsync() == 0 && attempt < 50; attempt++) await Task.Delay(10);
        var options = new GpuSchedulerOptions(4, 1024, TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(1));
        await store.RunAdmissionRoundAsync(Guid.NewGuid(), GpuSchedulerWakeReason.WorkReady, options,
            (candidate, _) => ValueTask.FromResult(new GpuAdmissionDecision(GpuAdmissionDisposition.Admit, "gpu-0", "owner", null, candidate.RequiredExecutorKey)), CancellationToken.None);
        await executor.DeliverAsync(Assert.Single(await store.ReadPendingDispatchesAsync(CancellationToken.None)), CancellationToken.None);
        await Assert.ThrowsAsync<InvalidOperationException>(() => response);
        Assert.Equal((int)GpuCapacitySlotState.Uncertain, await verify.GpuCapacitySlots.AsNoTracking().Select(slot => slot.State).SingleAsync());
        Assert.Empty(await verify.GpuExecutorResultReceipts.ToArrayAsync());
    }

    [NativeSqlServerFact]
    public async Task Owned_context_is_issued_after_acknowledgement_and_expires_before_capacity_release()
    {
        await SqlTestData.ClearPipelineAsync(fixture);
        var factory = SqlTestData.CreateFactory(fixture);
        var store = new SqlGpuSchedulerStore(factory);
        await using (var setup = await factory.CreateDbContextAsync())
        {
            setup.GpuCapacitySlots.Add(new GpuCapacitySlotEntity { SlotKey = "gpu-0", State = 0, UpdatedAtUtc = DateTimeOffset.UtcNow });
            await setup.SaveChangesAsync();
        }
        var executor = new GpuInteractiveExecutor(store, new Lifecycle(store), store, new ChannelGpuSchedulerWakeSignal(),
            TimeProvider.System, "synthetic-retrieval-v1", "synthetic-settings-v1", 10);
        GpuOwnedWorkContext? captured = null;
        var response = executor.ExecuteWithOwnershipAsync<int>(context =>
        {
            context.RequireActive("synthetic-retrieval-v1", "synthetic-settings-v1");
            captured = context;
            Assert.Throws<InvalidOperationException>(() => context.RequireActive("wrong-model", "synthetic-settings-v1"));
            return ValueTask.FromResult(new GpuInteractiveNativeResult<int>(42, true));
        }, CancellationToken.None).AsTask();
        await using var verify = await factory.CreateDbContextAsync();
        for (var attempt = 0; await verify.GpuMiniTasks.CountAsync() == 0 && attempt < 50; attempt++) await Task.Delay(10);
        var options = new GpuSchedulerOptions(4, 1024, TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(1));
        await store.RunAdmissionRoundAsync(Guid.NewGuid(), GpuSchedulerWakeReason.WorkReady, options,
            (candidate, _) => ValueTask.FromResult(new GpuAdmissionDecision(GpuAdmissionDisposition.Admit, "gpu-0", "owner", null, candidate.RequiredExecutorKey)), CancellationToken.None);
        var handle = Assert.Single(await store.ReadPendingDispatchesAsync(CancellationToken.None));
        await executor.DeliverAsync(handle, CancellationToken.None);
        Assert.Equal(42, await response);
        Assert.NotNull(captured);
        Assert.Equal(handle, captured.Handle);
        Assert.Throws<ObjectDisposedException>(() => captured.RequireActive("synthetic-retrieval-v1", "synthetic-settings-v1"));
        Assert.Equal((int)GpuCapacitySlotState.Available, await verify.GpuCapacitySlots.AsNoTracking().Select(s => s.State).SingleAsync());
    }

    [NativeSqlServerTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Cancellation_or_queue_expiry_recovers_undelivered_uncertain_admission_without_native_execution(bool cancelCaller)
    {
        await SqlTestData.ClearPipelineAsync(fixture);
        var factory = SqlTestData.CreateFactory(fixture);
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        var store = new SqlGpuSchedulerStore(factory, timeProvider: clock);
        await using (var setup = await factory.CreateDbContextAsync())
        {
            setup.GpuCapacitySlots.Add(new GpuCapacitySlotEntity { SlotKey = "gpu-0", State = 0, UpdatedAtUtc = DateTimeOffset.UtcNow });
            await setup.SaveChangesAsync();
        }
        using var caller = new CancellationTokenSource();
        var calls = 0;
        var executor = new GpuInteractiveExecutor(store, new Lifecycle(store), store, new ChannelGpuSchedulerWakeSignal(),
            clock, "synthetic-retrieval-v1", "synthetic-settings-v1", 10);
        var response = executor.ExecuteAsync<int>(_ => { Interlocked.Increment(ref calls); return ValueTask.FromResult(new GpuInteractiveNativeResult<int>(42, true)); }, caller.Token).AsTask();
        await using var context = await factory.CreateDbContextAsync();
        for (var attempt = 0; await context.GpuMiniTasks.CountAsync() == 0 && attempt < 50; attempt++) await Task.Delay(10);
        var options = new GpuSchedulerOptions(4, 1024, TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(1));
        Assert.Equal(GpuAdmissionDisposition.Admit, (await store.RunAdmissionRoundAsync(Guid.NewGuid(), GpuSchedulerWakeReason.WorkReady, options,
            (candidate, _) => ValueTask.FromResult(new GpuAdmissionDecision(GpuAdmissionDisposition.Admit, "gpu-0", "owner", null, candidate.RequiredExecutorKey)), CancellationToken.None)).Disposition);
        var reservation = Assert.Single(await store.ReadStaleCapacityReservationsAsync(DateTimeOffset.UtcNow, CancellationToken.None));
        Assert.True((await store.MarkCapacityUncertainAsync(Guid.NewGuid(), reservation, CancellationToken.None)).Committed);
        Assert.Empty(await store.ReadPendingDispatchesAsync(CancellationToken.None));
        // No DeliverAsync: the existing dispatcher cannot see an uncertain dispatch.
        if (cancelCaller) { caller.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => response); }
        else
        {
            Assert.False(response.IsCompleted);
            clock.Advance(TimeSpan.FromSeconds(2));
            await Assert.ThrowsAsync<TimeoutException>(() => response.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.True(response.IsFaulted); // The executor deadline fired, rather than the test watchdog.
        }
        for (var attempt = 0; await context.GpuCapacitySlots.AsNoTracking().Select(s => s.State).SingleAsync() != (int)GpuCapacitySlotState.Available && attempt < 100; attempt++) await Task.Delay(20);
        Assert.Equal((int)GpuCapacitySlotState.Available, await context.GpuCapacitySlots.AsNoTracking().Select(s => s.State).SingleAsync());
        Assert.Equal(0, calls);
        Assert.Equal((int)GpuMiniTaskExecutionState.OutcomeUncertain, await context.GpuMiniTasks.AsNoTracking().Select(t => t.ExecutionState).SingleAsync());
    }

    [NativeSqlServerTheory]
    [InlineData(false, 0)]
    [InlineData(true, 0)]
    [InlineData(false, 1)]
    [InlineData(false, 2)]
    [InlineData(false, 3)]
    [InlineData(false, 4)]
    [InlineData(false, 5)]
    [InlineData(false, 6)]
    [InlineData(false, 7)]
    public async Task Real_scheduler_dispatch_runs_once_and_releases_only_after_native_cleanup(bool cancelCaller, int failureMode)
    {
        await SqlTestData.ClearPipelineAsync(fixture);
        var factory = SqlTestData.CreateFactory(fixture);
        // The already-uncertain dispatch requires three SQL reads before refusal;
        // keep that ownership check independent of queue expiry (tested above).
        // Other cases retain real time so their durable retry delays still run.
        TimeProvider clock = failureMode == 5 ? new ManualClock(DateTimeOffset.UtcNow) : TimeProvider.System;
        var uncertaintyResponses = 0;
        var store = new SqlGpuSchedulerStore(factory, timeProvider: clock, afterLifecycleCommitted: _ =>
        {
            if (failureMode == 7 && Interlocked.Increment(ref uncertaintyResponses) == 1) throw new ResponseLostException();
            return ValueTask.CompletedTask;
        });
        await using (var setup = await factory.CreateDbContextAsync())
        {
            setup.GpuCapacitySlots.Add(new GpuCapacitySlotEntity { SlotKey = "gpu-0", State = 0, UpdatedAtUtc = DateTimeOffset.UtcNow });
            await setup.SaveChangesAsync();
        }
        async ValueTask MarkUncertain()
        {
            var reservation = Assert.Single(await store.ReadStaleCapacityReservationsAsync(DateTimeOffset.UtcNow, CancellationToken.None));
            Assert.True((await store.MarkCapacityUncertainAsync(Guid.NewGuid(), reservation, CancellationToken.None)).Committed);
        }
        var requests = new TransientRecoveryReads(store, failureMode == 7);
        var scheduler = new TransientReservationRead(store, failureMode == 7);
        var executor = new GpuInteractiveExecutor(requests, new Lifecycle(store, failureMode == 4, failureMode == 6 ? MarkUncertain : null, failureMode == 7), scheduler, new ChannelGpuSchedulerWakeSignal(),
            clock, "synthetic-retrieval-v1", "synthetic-settings-v1", 10);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleaned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var nativeCalls = 0;
        using var caller = new CancellationTokenSource();
        var result = executor.ExecuteAsync<int>(async token =>
        {
            Interlocked.Increment(ref nativeCalls);
            started.SetResult();
            await cleaned.Task; // Simulates native cleanup that cannot finish at caller cancellation.
            if (failureMode == 2) throw new InvalidOperationException("synthetic-native-failure");
            return new(42, failureMode != 1, token.IsCancellationRequested ? "request-cancelled" : null);
        }, caller.Token).AsTask();
        Assert.False(result.IsCompleted);
        await using (var waiting = await factory.CreateDbContextAsync())
        {
            for (var attempt = 0; await waiting.GpuMiniTasks.CountAsync() == 0 && attempt < 50; attempt++) await Task.Delay(10);
            Assert.Equal(1, await waiting.GpuMiniTasks.CountAsync());
        }
        var options = new GpuSchedulerOptions(4, 1024, TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(1));
        var admitted = await store.RunAdmissionRoundAsync(Guid.NewGuid(), GpuSchedulerWakeReason.WorkReady, options,
            (candidate, _) => ValueTask.FromResult(new GpuAdmissionDecision(GpuAdmissionDisposition.Admit, "gpu-0", "owner", null, candidate.RequiredExecutorKey)), CancellationToken.None);
        Assert.Equal(GpuAdmissionDisposition.Admit, admitted.Disposition);
        var handle = Assert.Single(await store.ReadPendingDispatchesAsync(CancellationToken.None));
        if (failureMode == 5) await MarkUncertain();
        var deliveries = Enumerable.Range(0, 6).Select(_ => executor.DeliverAsync(handle, CancellationToken.None).AsTask()).ToArray();
        if (failureMode is not 5 and not 6) await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (cancelCaller)
        {
            caller.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => result);
        }
        if (failureMode is not 5 and not 6) await using (var before = await factory.CreateDbContextAsync())
        {
            Assert.Equal((int)GpuCapacitySlotState.Reserved, (await before.GpuCapacitySlots.SingleAsync()).State);
            Assert.Empty(await before.GpuExecutorResultReceipts.ToArrayAsync());
        }
        if (failureMode == 3) await MarkUncertain();
        cleaned.SetResult();
        await Task.WhenAll(deliveries).WaitAsync(TimeSpan.FromSeconds(8));
        if (!cancelCaller && failureMode is 0 or 4) Assert.Equal(42, await result);
        if (!cancelCaller && failureMode is 1 or 2 or 3 or 5 or 6 or 7) await Assert.ThrowsAsync<InvalidOperationException>(() => result);
        await executor.DeliverAsync(handle, CancellationToken.None);
        Assert.Equal(failureMode is 5 or 6 ? 0 : 1, nativeCalls);
        if (failureMode == 7)
        {
            Assert.True(uncertaintyResponses >= 2);
            Assert.True(scheduler.ReservationReads >= 2);
            Assert.True(requests.RecoveryReads >= 2);
        }
        await using var verify = await factory.CreateDbContextAsync();
        Assert.Equal(failureMode is 1 or 2 ? (int)GpuCapacitySlotState.Uncertain : (int)GpuCapacitySlotState.Available, (await verify.GpuCapacitySlots.SingleAsync()).State);
        Assert.Equal(failureMode is 1 or 2 ? (int)GpuMiniTaskExecutionState.Active : cancelCaller || failureMode is 3 or 5 or 6 or 7 ? (int)GpuMiniTaskExecutionState.OutcomeUncertain : (int)GpuMiniTaskExecutionState.Completed,
            (await verify.GpuMiniTasks.SingleAsync()).ExecutionState);
    }

    private sealed class Lifecycle(SqlGpuSchedulerStore store, bool loseCommittedResponses = false, Func<ValueTask>? beforeAcknowledgement = null, bool refuseCallback = false) : IGpuExecutorLifecycleSink
    {
        private readonly HashSet<Guid> _lost = [];
        private async ValueTask<T> LoseOnce<T>(Guid operation, ValueTask<T> mutation)
        {
            var result = await mutation;
            if (loseCommittedResponses && _lost.Add(operation)) throw new ResponseLostException();
            return result;
        }
        public async ValueTask<GpuExecutorDispatchMutationResult> AcknowledgeAsync(GpuExecutorAcknowledgement request, CancellationToken ct)
        {
            if (beforeAcknowledgement is not null) await beforeAcknowledgement();
            return await LoseOnce(request.OperationId, store.AcknowledgeAsync(request, ct));
        }
        public ValueTask<GpuExecutorDispatchMutationResult> MarkDeliveryUncertainAsync(GpuExecutorDeliveryUncertainty request, CancellationToken ct) => store.MarkDeliveryUncertainAsync(request, ct);
        public ValueTask<GpuExecutorDispatchMutationResult> RecordReceiptAsync(GpuExecutorResultReceipt request, CancellationToken ct) => LoseOnce(request.OperationId, store.RecordReceiptAsync(request, ct));
        public ValueTask<GpuExecutorDispatchMutationResult> RecordTrustedEvidenceAsync(GpuExecutorTrustedEvidence request, CancellationToken ct) => store.RecordTrustedEvidenceAsync(request, ct);
        public ValueTask<GpuBatchCallbackResult> HandleCallbackAsync(Guid operation, GpuBatchCallback request, CancellationToken ct) => refuseCallback
            ? ValueTask.FromException<GpuBatchCallbackResult>(new InvalidOperationException("synthetic-callback-refusal"))
            : LoseOnce(operation, store.ApplyBatchCallbackAsync(operation, request, ct));
    }

    private sealed class TransientRecoveryReads(SqlGpuSchedulerStore store, bool failOnce) : IGpuInteractiveRequestStore
    {
        public int RecoveryReads { get; private set; }
        public ValueTask<GpuMiniTaskHandoffResult> HandoffInteractiveAsync(GpuInteractiveHandoffRequest request, CancellationToken ct) => store.HandoffInteractiveAsync(request, ct);
        public ValueTask<bool> CancelInteractiveAsync(Guid request, Guid instance, CancellationToken ct) => store.CancelInteractiveAsync(request, instance, ct);
        public ValueTask<GpuInteractiveExecutionWork?> ReadInteractiveExecutionAsync(GpuExecutorBatchHandle handle, Guid instance, GpuExecutorDispatchState state, CancellationToken ct) => store.ReadInteractiveExecutionAsync(handle, instance, state, ct);
        public ValueTask<bool> CancelLostInteractiveReadyAsync(Guid request, GpuInteractiveOwnerIdentity owner, CancellationToken ct) => store.CancelLostInteractiveReadyAsync(request, owner, ct);
        public async ValueTask<IReadOnlyList<GpuInteractiveRecoveryWork>> ReadInteractiveRecoveryAsync(CancellationToken ct)
        {
            var work = await store.ReadInteractiveRecoveryAsync(ct);
            if (++RecoveryReads == 1 && failOnce) throw new ResponseLostException();
            return work;
        }
    }

    private sealed class TransientReservationRead(SqlGpuSchedulerStore store, bool failOnce) : IGpuSchedulerStore
    {
        public int ReservationReads { get; private set; }
        public async ValueTask<IReadOnlyList<GpuCapacityUncertaintyRequest>> ReadStaleCapacityReservationsAsync(DateTimeOffset before, CancellationToken ct)
        {
            if (++ReservationReads == 1 && failOnce) throw new ResponseLostException();
            return await store.ReadStaleCapacityReservationsAsync(before, ct);
        }
        public ValueTask<GpuMiniTaskHandoffResult> GpuTaskHandoffAsync(GpuMiniTaskHandoffRequest request, CancellationToken ct) => store.GpuTaskHandoffAsync(request, ct);
        public ValueTask<GpuSchedulerAdmissionRoundResult> RunAdmissionRoundAsync(Guid operation, GpuSchedulerWakeReason reason, GpuSchedulerOptions options, Func<GpuBatchCandidate, CancellationToken, ValueTask<GpuAdmissionDecision>> gate, CancellationToken ct) => store.RunAdmissionRoundAsync(operation, reason, options, gate, ct);
        public ValueTask<GpuBatchCallbackResult> ApplyBatchCallbackAsync(Guid operation, GpuBatchCallback request, CancellationToken ct) => store.ApplyBatchCallbackAsync(operation, request, ct);
        public ValueTask<GpuDiagnosticTransitionResult> MarkCapacityUncertainAsync(Guid operation, GpuCapacityUncertaintyRequest request, CancellationToken ct) => store.MarkCapacityUncertainAsync(operation, request, ct);
        public ValueTask<GpuTrustedReconciliationResult> ReconcileCapacityAsync(Guid operation, GpuTrustedCapacityReconciliation request, CancellationToken ct) => store.ReconcileCapacityAsync(operation, request, ct);
        public ValueTask<GpuTrustedReconciliationResult> ReconcileTaskOutcomeAsync(Guid operation, GpuTaskOutcomeReconciliation request, CancellationToken ct) => store.ReconcileTaskOutcomeAsync(operation, request, ct);
        public ValueTask<GpuSchedulerWakeSnapshot> ReadWakeStateAsync(CancellationToken ct) => store.ReadWakeStateAsync(ct);
        public ValueTask<GpuSchedulerWakeConsumption> ConsumeWakeAsync(Guid operation, long generation, CancellationToken ct) => store.ConsumeWakeAsync(operation, generation, ct);
        public ValueTask<bool> AcknowledgeWakeAsync(Guid operation, Guid consumption, CancellationToken ct) => store.AcknowledgeWakeAsync(operation, consumption, ct);
        public ValueTask<GpuSchedulerStatusSnapshot> ReadGpuSchedulerStatusAsync(CancellationToken ct) => store.ReadGpuSchedulerStatusAsync(ct);
    }
    private sealed class ManualClock(DateTimeOffset now) : TimeProvider
    {
        private readonly object _sync = new();
        private readonly List<ManualTimer> _timers = [];
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() { lock (_sync) return _now; }
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            lock (_sync)
            {
                var timer = new ManualTimer(this, callback, state);
                timer.Change(dueTime, period);
                _timers.Add(timer);
                return timer;
            }
        }
        public void Advance(TimeSpan elapsed)
        {
            List<Action> callbacks = [];
            lock (_sync)
            {
                _now += elapsed;
                foreach (var timer in _timers)
                    if (timer.TakeIfDue(_now) is { } callback) callbacks.Add(callback);
            }
            foreach (var callback in callbacks) callback();
        }
        private sealed class ManualTimer(ManualClock clock, TimerCallback callback, object? state) : ITimer
        {
            private DateTimeOffset? _due;
            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                if (period != Timeout.InfiniteTimeSpan) throw new NotSupportedException("Only one-shot timers are used by these tests.");
                lock (clock._sync) { _due = dueTime == Timeout.InfiniteTimeSpan ? null : clock._now + dueTime; }
                return true;
            }
            public Action? TakeIfDue(DateTimeOffset current)
            {
                if (_due is null || _due > current) return null;
                _due = null;
                return () => callback(state);
            }
            public void Dispose() { lock (clock._sync) _due = null; }
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
    private sealed class ResponseLostException : System.Data.Common.DbException;
}
