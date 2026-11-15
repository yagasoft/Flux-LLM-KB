using FluxKnowledge.Application.Gpu;
using FluxKnowledge.Domain.Gpu;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.SqlServer.Workers;
using FluxKnowledge.Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Gpu;

public sealed partial class GpuInteractiveSettlementTests
{
    [NativeSqlServerFact]
    public async Task Dispatch_timeout_runs_recovery_without_releasing_a_live_native_owner()
    {
        var factory = SqlTestData.CreateFactory(_sql);
        var store = new SqlGpuSchedulerStore(factory, interactiveExecutionTimeout: TimeSpan.FromSeconds(20),
            interactiveQueueTimeout: TimeSpan.FromSeconds(4));
        await AddSlot(factory);
        var wake = new ChannelGpuSchedulerWakeSignal();
        var probe = new DispatchOwnerProbe();
        var recovery = new GpuInteractiveOwnerRecovery(store, store, store, probe, wake, TimeProvider.System);
        var executor = new GpuInteractiveExecutor(store, new Lifecycle(store), store, wake, TimeProvider.System,
            "joined-profile", "joined-settings", 10, ownerRecovery: recovery, executionTimeout: TimeSpan.FromSeconds(20));
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var observed = new RecoveryObservedAdapter(executor, started.Task);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(new GpuSchedulerOptions(1, 1024, TimeSpan.FromMinutes(1),
            TimeSpan.FromMilliseconds(50), TimeSpan.FromMinutes(10)));
        services.AddSingleton<IGpuExecutorDispatchStore>(store);
        services.AddSingleton<ChannelGpuExecutorDispatchSignal>();
        services.AddSingleton<IGpuExecutorAdapter>(observed);
        services.AddSingleton<GpuExecutorDispatchRecoveryService>();
        await using var provider = services.BuildServiceProvider();
        var service = provider.GetRequiredService<GpuExecutorDispatchRecoveryService>();
        var operation = executor.StartOwned<int>(async owner =>
        {
            var allocation = owner.BeginNativeAllocation();
            started.TrySetResult();
            await release.Task.ConfigureAwait(false);
            allocation.ConfirmReleased();
            return new(42, true);
        }, CancellationToken.None, TimeSpan.FromSeconds(4));
        var serviceStarted = false;
        try
        {
            await WaitForRequest(factory, operation.RequestId);
            await using (var db = await factory.CreateDbContextAsync())
            {
                var request = await db.GpuMiniTasks.AsNoTracking().SingleAsync(t => t.Id == operation.RequestId);
                probe.Expected = new(request.InteractiveOwnerProcessId!.Value, request.InteractiveOwnerStartedAtUtc!.Value,
                    request.InteractiveOwnerMachineFingerprint!);
            }
            var handle = await Admit(store);
            await service.StartAsync(CancellationToken.None);
            serviceStarted = true;
            await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await observed.RecoveryDuringNativeWork.Task.WaitAsync(TimeSpan.FromSeconds(3));

            Assert.False(operation.Result.IsCompleted);
            Assert.False(operation.Settlement.IsCompleted);
            Assert.True(probe.Observations > 0);
            await using (var db = await factory.CreateDbContextAsync())
            {
                var slot = await db.GpuCapacitySlots.AsNoTracking().SingleAsync();
                Assert.Equal((int)GpuCapacitySlotState.Reserved, slot.State);
                Assert.Equal(handle.BatchId, slot.ActiveBatchId);
                Assert.Equal((int)GpuMiniTaskExecutionState.Active,
                    (await db.GpuMiniTasks.AsNoTracking().SingleAsync()).ExecutionState);
                Assert.Equal((int)GpuExecutorDispatchState.Acknowledged,
                    (await db.GpuExecutorDispatches.AsNoTracking().SingleAsync()).State);
                Assert.Empty(await db.GpuExecutorResultReceipts.ToArrayAsync());
                Assert.Empty(await db.GpuExecutorEvidence.ToArrayAsync());
            }

            release.TrySetResult();
            Assert.Equal(42, await operation.Result.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(GpuInteractiveSettlementKind.Released,
                (await operation.Settlement.WaitAsync(TimeSpan.FromSeconds(5))).Kind);
            await using (var db = await factory.CreateDbContextAsync())
            {
                Assert.Equal((int)GpuCapacitySlotState.Available, (await db.GpuCapacitySlots.SingleAsync()).State);
                Assert.Single(await db.GpuExecutorResultReceipts.ToArrayAsync());
            }
        }
        finally
        {
            release.TrySetResult();
            await operation.Settlement.WaitAsync(TimeSpan.FromSeconds(8));
            if (serviceStarted) await service.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(3));
        }
    }

    private sealed class DispatchOwnerProbe : IGpuInteractiveOwnerProbe
    {
        public GpuInteractiveOwnerIdentity? Expected { get; set; }
        public int Observations;
        public GpuInteractiveOwnerIdentity Current => Expected ?? throw new InvalidOperationException("Owner not captured");
        public GpuInteractiveOwnerObservation Observe(GpuInteractiveOwnerIdentity owner)
        {
            Assert.Equal(Current, owner);
            Interlocked.Increment(ref Observations);
            return GpuInteractiveOwnerObservation.Alive;
        }
    }

    private sealed class RecoveryObservedAdapter(GpuInteractiveExecutor inner, Task nativeStarted)
        : IGpuExecutorAdapter, IGpuExecutorRecoveryAdapter
    {
        public string ExecutorKey => inner.ExecutorKey;
        public TaskCompletionSource RecoveryDuringNativeWork { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ValueTask DeliverAsync(GpuExecutorBatchHandle handle, CancellationToken ct) => inner.DeliverAsync(handle, ct);
        public async ValueTask RecoverAsync(CancellationToken ct)
        {
            await inner.RecoverAsync(ct);
            if (nativeStarted.IsCompleted) RecoveryDuringNativeWork.TrySetResult();
        }
    }
}
