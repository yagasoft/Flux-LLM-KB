using FluxKnowledge.Application.Gpu;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Sources;
using FluxKnowledge.Domain.Gpu;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using FluxKnowledge.Infrastructure.SqlServer.Workers;
using FluxKnowledge.Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Workers;

public sealed partial class GpuSchedulerServiceTests
{
    [Fact]
    public async Task Startup_recovery_waits_for_deployment_release_without_needing_a_new_work_notification()
    {
        var hold = new SchedulerDeploymentHold();
        var store = new RecordingStore(new GpuSchedulerWakeSnapshot(0, 0, null));
        await using var provider = CreateProvider(store, new ChannelGpuSchedulerWakeSignal(), TimeProvider.System,
            TimeSpan.FromHours(1), deploymentHold: hold);
        var service = provider.GetRequiredService<GpuSchedulerService>();
        await service.StartAsync(CancellationToken.None);
        try
        {
            var first = await Task.WhenAny(hold.Observed.Task, store.FirstAdmission.Task).WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Same(hold.Observed.Task, first);
            Assert.False(store.FirstAdmission.Task.IsCompleted);
            hold.Release();
            Assert.Equal(GpuSchedulerWakeReason.StartupRecovery,
                await store.FirstAdmission.Task.WaitAsync(TimeSpan.FromSeconds(2)));
        }
        finally
        {
            await service.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));
        }
    }

    [Fact]
    public async Task Cancelling_a_held_scheduler_does_not_spend_startup_recovery()
    {
        var hold = new SchedulerDeploymentHold();
        var store = new RecordingStore(new GpuSchedulerWakeSnapshot(0, 0, null));
        await using var provider = CreateProvider(store, new ChannelGpuSchedulerWakeSignal(), TimeProvider.System,
            TimeSpan.FromHours(1), deploymentHold: hold);
        var service = provider.GetRequiredService<GpuSchedulerService>();
        await service.StartAsync(CancellationToken.None);
        await hold.Observed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await service.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(store.FirstAdmission.Task.IsCompleted);
    }

    [Fact]
    public async Task Permitted_rebuild_admission_does_not_spend_recovery_for_full_release()
    {
        var hold = new SchedulerDeploymentHold();
        hold.PermitRebuild();
        var store = new ScriptedStore(new GpuSchedulerWakeSnapshot(0, 0, null));
        await using var provider = CreateProvider(store, new ChannelGpuSchedulerWakeSignal(), TimeProvider.System,
            TimeSpan.FromMilliseconds(10), deploymentHold: hold);
        var service = provider.GetRequiredService<GpuSchedulerService>();
        await service.StartAsync(CancellationToken.None);
        try
        {
            Assert.Equal(GpuSchedulerWakeReason.StartupRecovery,
                await store.Admissions.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.True(hold.IsHeld);
            hold.Release();
            Assert.Equal(GpuSchedulerWakeReason.StartupRecovery,
                await store.Admissions.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.Equal(2, store.AdmissionOperationIds.Distinct().Count());
        }
        finally { await service.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2)); }
    }

    [Fact]
    public async Task Full_release_reconciles_a_persisted_inflight_wake_before_a_fresh_recovery_round()
    {
        var hold = new SchedulerDeploymentHold();
        var store = new RestartAfterCommittedAdmissionStore(
            new GpuSchedulerWakeSnapshot(7, GpuSchedulerWakeReason.WorkReady, null,
                ConsumptionOperationId: Guid.NewGuid()), DateTimeOffset.UtcNow.AddHours(1))
            { BlockNewAdmissionDecision = true };
        await using var provider = CreateProvider(store, new ChannelGpuSchedulerWakeSignal(), TimeProvider.System,
            TimeSpan.FromMilliseconds(10), deploymentHold: hold);
        var service = provider.GetRequiredService<GpuSchedulerService>();
        await service.StartAsync(CancellationToken.None);
        try
        {
            await hold.Observed.Task.WaitAsync(TimeSpan.FromSeconds(2));
            hold.Release();
            Assert.Equal(GpuSchedulerWakeReason.WorkReady,
                await store.Admissions.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2)));
            await store.SubsequentAdmissionStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(GpuSchedulerWakeReason.StartupRecovery, store.AdmissionReasons[1]);
            Assert.NotEqual(store.AdmissionOperationIds[0], store.AdmissionOperationIds[1]);
        }
        finally { await service.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2)); }
    }

    [Fact]
    public async Task Hold_introduced_during_admission_is_recovered_after_release_without_a_new_wake()
    {
        var hold = new SchedulerDeploymentHold();
        hold.Release();
        var store = new ScriptedStore(new GpuSchedulerWakeSnapshot(0, 0, null));
        store.AfterAdmission = _ => { if (store.AdmissionCount == 1) hold.Hold(); };
        await using var provider = CreateProvider(store, new ChannelGpuSchedulerWakeSignal(), TimeProvider.System,
            TimeSpan.FromMilliseconds(10), deploymentHold: hold);
        var service = provider.GetRequiredService<GpuSchedulerService>();
        await service.StartAsync(CancellationToken.None);
        try
        {
            await store.Admissions.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
            await hold.HeldObserved.Task.WaitAsync(TimeSpan.FromSeconds(2));
            hold.Release();
            Assert.Equal(GpuSchedulerWakeReason.StartupRecovery,
                await store.Admissions.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.Equal(2, store.AdmissionOperationIds.Distinct().Count());
        }
        finally { await service.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2)); }
    }

    [Fact]
    public async Task Lost_admission_response_keeps_its_operation_identity_then_uses_a_distinct_release_recovery()
    {
        var hold = new SchedulerDeploymentHold();
        hold.Release();
        var failed = 0;
        var store = new RestartAfterCommittedAdmissionStore(
            new GpuSchedulerWakeSnapshot(7, GpuSchedulerWakeReason.WorkReady, null), DateTimeOffset.UtcNow.AddHours(1))
            { BlockNewAdmissionDecision = true };
        store.AfterAdmissionCommitted = () =>
        {
            if (Interlocked.Increment(ref failed) != 1) return;
            hold.Hold();
            throw new InvalidOperationException("Synthetic lost response after durable admission.");
        };
        await using var provider = CreateProvider(store, new ChannelGpuSchedulerWakeSignal(), TimeProvider.System,
            TimeSpan.FromMilliseconds(10), deploymentHold: hold);
        var service = provider.GetRequiredService<GpuSchedulerService>();
        await service.StartAsync(CancellationToken.None);
        try
        {
            await hold.HeldObserved.Task.WaitAsync(TimeSpan.FromSeconds(2));
            hold.Release();
            await store.SubsequentAdmissionStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(3, store.AdmissionOperationIds.Count);
            Assert.Equal(store.AdmissionOperationIds[0], store.AdmissionOperationIds[1]);
            Assert.NotEqual(store.AdmissionOperationIds[1], store.AdmissionOperationIds[2]);
            Assert.Equal([GpuSchedulerWakeReason.WorkReady, GpuSchedulerWakeReason.WorkReady,
                GpuSchedulerWakeReason.StartupRecovery], store.AdmissionReasons);
            Assert.Equal(1, store.NewAdmissionDecisionCount);
        }
        finally { await service.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2)); }
    }

    [NativeSqlServerFact]
    public async Task Hosted_scheduler_admits_preexisting_ready_work_after_ordinary_deployment_hold_release()
    {
        await SqlTestData.ClearPipelineAsync(_fixture);
        var factory = SqlTestData.CreateFactory(_fixture);
        var hold = new SchedulerDeploymentHold();
        var clock = new FixedTimeProvider(DateTimeOffset.UtcNow);
        var admitted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using (var setup = await factory.CreateDbContextAsync())
        {
            setup.GpuCapacitySlots.Add(new GpuCapacitySlotEntity { SlotKey = "gpu-0",
                State = (int)GpuCapacitySlotState.Available, UpdatedAtUtc = DateTimeOffset.UtcNow });
            await setup.SaveChangesAsync();
        }
        var sql = new SqlGpuSchedulerStore(factory, timeProvider: clock, deploymentValidationHold: hold,
            interactiveExecutionTimeout: TimeSpan.FromSeconds(20),
            afterAdmissionCommitted: _ => { admitted.TrySetResult(); return ValueTask.CompletedTask; });
        var instance = Guid.NewGuid();
        var now = clock.GetUtcNow();
        var request = new GpuInteractiveHandoffRequest(Guid.NewGuid(), instance, $"retrieval-gpu:{instance:N}",
            "synthetic-retrieval-v1", "synthetic-settings-v1", 10, now.AddSeconds(2), now.AddSeconds(20), true);
        Assert.True((await sql.HandoffInteractiveAsync(request, CancellationToken.None)).Committed);
        var services = new ServiceCollection();
        services.AddSingleton<IGpuSchedulerStore>(sql);
        services.AddSingleton<IDeploymentValidationHold>(hold);
        services.AddSingleton<IGpuSchedulerWakeSignal, ChannelGpuSchedulerWakeSignal>();
        services.AddSingleton<IGpuAdmissionGate, AdmittingInteractiveGate>();
        services.AddSingleton<IStatusEventPublisher, NullPublisher>();
        services.AddSingleton<TimeProvider>(clock);
        services.AddSingleton(new GpuSchedulerOptions(1, 1024, TimeSpan.FromMinutes(5),
            TimeSpan.FromHours(1), TimeSpan.FromHours(2)));
        services.AddScoped<GpuSchedulerCoordinator>();
        services.AddSingleton<GpuSchedulerService>();
        await using var provider = services.BuildServiceProvider();
        var service = provider.GetRequiredService<GpuSchedulerService>();
        await service.StartAsync(CancellationToken.None);
        try
        {
            await hold.Observed.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.False(admitted.Task.IsCompleted);
            await using (var verify = await factory.CreateDbContextAsync())
                Assert.Equal((int)GpuMiniTaskExecutionState.Ready,
                    (await verify.GpuMiniTasks.SingleAsync(value => value.Id == request.RequestId)).ExecutionState);
            hold.Release(); // No new task, signal, source scan or dispatch is issued.
            await admitted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await using var final = await factory.CreateDbContextAsync();
            var task = await final.GpuMiniTasks.SingleAsync(value => value.Id == request.RequestId);
            Assert.Equal((int)GpuMiniTaskExecutionState.Active, task.ExecutionState);
            Assert.NotNull(task.BatchId);
            Assert.Single(await final.GpuBatches.ToArrayAsync());
            Assert.Empty(await final.GpuExecutorResultReceipts.ToArrayAsync());
        }
        finally { await service.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2)); }
    }

    private sealed class SchedulerDeploymentHold : IDeploymentValidationHold
    {
        private DeploymentHoldAdmission _admission = new(true, null);
        public TaskCompletionSource Observed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource HeldObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool IsHeld => ReadAdmissionState().IsHeld;
        public DeploymentHoldAdmission ReadAdmissionState()
        {
            Observed.TrySetResult();
            var value = Volatile.Read(ref _admission);
            if (value.IsHeld) HeldObserved.TrySetResult();
            return value;
        }
        public void Release() => Volatile.Write(ref _admission, new(false, null));
        public void Hold() => Volatile.Write(ref _admission, new(true, null));
        public void PermitRebuild() => Volatile.Write(ref _admission, new(true, Guid.NewGuid()));
        public async ValueTask WaitUntilReleasedAsync(CancellationToken cancellationToken)
        {
            while (IsHeld) await Task.Delay(10, cancellationToken);
        }
    }
}
