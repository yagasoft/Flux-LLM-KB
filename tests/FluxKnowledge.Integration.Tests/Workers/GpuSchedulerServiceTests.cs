using FluxKnowledge.Application.Gpu;
using FluxKnowledge.Application.Contracts;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Domain.Gpu;
using FluxKnowledge.Domain.Jobs;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Integration.Tests.Gpu;
using FluxKnowledge.Integration.Tests.Support;
using FluxKnowledge.Infrastructure.SqlServer.Workers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Threading.Channels;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Workers;

public sealed class GpuSchedulerServiceTests(NativeSqlServerFixture fixture)
    : IClassFixture<NativeSqlServerFixture>
{
    private readonly NativeSqlServerFixture _fixture = fixture;

    [Fact]
    public async Task Coalesced_signal_preserves_capacity_released_when_other_reasons_arrive()
    {
        var signal = new ChannelGpuSchedulerWakeSignal();

        signal.Notify(GpuSchedulerWakeReason.WorkReady);
        signal.Notify(GpuSchedulerWakeReason.CapacityReleased);

        var observed = await signal.WaitAsync(CancellationToken.None);

        Assert.Equal(
            GpuSchedulerWakeReason.WorkReady | GpuSchedulerWakeReason.CapacityReleased,
            observed);
    }

    [Fact]
    public async Task Capacity_released_signal_reconsiders_durable_state_before_a_future_deferred_due_time()
    {
        var signal = new ChannelGpuSchedulerWakeSignal();
        var clock = new FixedTimeProvider(DateTimeOffset.Parse("2026-07-29T12:00:00+00:00"));
        var store = new RecordingStore(
            new GpuSchedulerWakeSnapshot(0, 0, clock.GetUtcNow().AddHours(1)));
        var services = new ServiceCollection();
        services.AddSingleton<IGpuSchedulerStore>(store);
        services.AddSingleton<IGpuAdmissionGate, NoGpuAdmissionGate>();
        services.AddSingleton<IStatusEventPublisher, NullPublisher>();
        services.AddSingleton<IGpuSchedulerWakeSignal>(signal);
        services.AddSingleton<TimeProvider>(clock);
        services.AddSingleton(new GpuSchedulerOptions(1, 1, TimeSpan.FromMinutes(1), TimeSpan.FromHours(1), TimeSpan.FromHours(1)));
        services.AddScoped<GpuSchedulerCoordinator>();
        services.AddSingleton<GpuSchedulerService>();
        await using var provider = services.BuildServiceProvider();
        var service = provider.GetRequiredService<GpuSchedulerService>();

        await service.StartAsync(CancellationToken.None);
        try
        {
            Assert.Equal(GpuSchedulerWakeReason.StartupRecovery, await store.FirstAdmission.Task.WaitAsync(TimeSpan.FromSeconds(2)));
            store.SetWake(new GpuSchedulerWakeSnapshot(1, GpuSchedulerWakeReason.CapacityReleased, clock.GetUtcNow().AddHours(1)));
            signal.Notify(GpuSchedulerWakeReason.CapacityReleased);

            Assert.Equal(GpuSchedulerWakeReason.CapacityReleased, await store.SecondAdmission.Task.WaitAsync(TimeSpan.FromSeconds(2)));
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task First_successful_pass_consumes_pending_durable_reasons_instead_of_adding_startup_recovery()
    {
        var signal = new ChannelGpuSchedulerWakeSignal();
        var clock = new FixedTimeProvider(DateTimeOffset.Parse("2026-07-29T12:00:00+00:00"));
        var store = new ScriptedStore(new GpuSchedulerWakeSnapshot(7, GpuSchedulerWakeReason.WorkReady, null));
        await using var provider = CreateProvider(store, signal, clock, fallbackInterval: TimeSpan.FromHours(1));
        var service = provider.GetRequiredService<GpuSchedulerService>();

        await service.StartAsync(CancellationToken.None);
        try
        {
            Assert.Equal(
                GpuSchedulerWakeReason.WorkReady,
                await store.Admissions.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.Equal(1, store.ConsumeCount);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Restart_after_durable_capacity_released_consumption_replays_it_before_future_due_time_then_acknowledges_once()
    {
        var now = DateTimeOffset.Parse("2026-07-29T12:00:00+00:00");
        var clock = new FixedTimeProvider(now);
        var store = new RestartableWakeStore(new GpuSchedulerWakeSnapshot(
            7, GpuSchedulerWakeReason.CapacityReleased, now.AddHours(1)))
        {
            BlockConsumption = true
        };
        var firstSignal = new ChannelGpuSchedulerWakeSignal();
        await using (var firstProvider = CreateProvider(store, firstSignal, clock, TimeSpan.FromHours(2)))
        {
            var firstService = firstProvider.GetRequiredService<GpuSchedulerService>();
            await firstService.StartAsync(CancellationToken.None);
            await store.FirstConsumptionRecorded.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await firstService.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));
        }

        Assert.Equal(0, store.AdmissionCount);
        Assert.Equal(GpuSchedulerWakeReason.CapacityReleased, store.CurrentWake.Reasons);
        store.BlockConsumption = false;
        var secondSignal = new ChannelGpuSchedulerWakeSignal();
        await using var secondProvider = CreateProvider(store, secondSignal, clock, TimeSpan.FromHours(2));
        var secondService = secondProvider.GetRequiredService<GpuSchedulerService>();
        await secondService.StartAsync(CancellationToken.None);
        try
        {
            Assert.Equal(
                GpuSchedulerWakeReason.CapacityReleased,
                await store.Admissions.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2)));
            await store.Acknowledged.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(1, store.AcknowledgementCount);
            Assert.Equal((GpuSchedulerWakeReason)0, store.CurrentWake.Reasons);
        }
        finally
        {
            await secondService.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Generation_mismatch_is_reread_and_consumed_before_the_next_admission()
    {
        var signal = new ChannelGpuSchedulerWakeSignal();
        var clock = new FixedTimeProvider(DateTimeOffset.Parse("2026-07-29T12:00:00+00:00"));
        var store = new ScriptedStore(new GpuSchedulerWakeSnapshot(0, 0, null));
        var mismatchInjected = false;
        store.ConsumeOverride = expectedGeneration =>
        {
            if (expectedGeneration != 1 || mismatchInjected)
            {
                return null;
            }

            mismatchInjected = true;
            var newer = new GpuSchedulerWakeSnapshot(2, GpuSchedulerWakeReason.CapacityReleased, null);
            store.SetWake(newer);
            return new GpuSchedulerWakeConsumption(false, newer);
        };
        await using var provider = CreateProvider(store, signal, clock, fallbackInterval: TimeSpan.FromHours(1));
        var service = provider.GetRequiredService<GpuSchedulerService>();

        await service.StartAsync(CancellationToken.None);
        try
        {
            Assert.Equal(
                GpuSchedulerWakeReason.StartupRecovery,
                await store.Admissions.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2)));
            store.SetWake(new GpuSchedulerWakeSnapshot(1, GpuSchedulerWakeReason.WorkReady, null));
            signal.Notify(GpuSchedulerWakeReason.WorkReady);

            Assert.Equal(
                GpuSchedulerWakeReason.CapacityReleased,
                await store.Admissions.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.Equal(2, store.ConsumeCount);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    [Theory]
    [InlineData(GpuSchedulerWakeReason.WorkReady)]
    [InlineData(GpuSchedulerWakeReason.CapacityReleased)]
    public async Task Wake_that_defers_waits_without_repeating_admission_before_its_durable_due_time(
        GpuSchedulerWakeReason wakeReason)
    {
        var signal = new ChannelGpuSchedulerWakeSignal();
        var now = DateTimeOffset.Parse("2026-07-29T12:00:00+00:00");
        var deferredDueAtUtc = now.AddHours(1);
        var clock = new ManualTimeProvider(now, () => 0);
        var store = new ScriptedStore(new GpuSchedulerWakeSnapshot(0, 0, null));
        store.AfterAdmission = reason =>
        {
            if (reason == wakeReason)
            {
                store.SetWake(new GpuSchedulerWakeSnapshot(
                    2,
                    GpuSchedulerWakeReason.DeferredRetry,
                    deferredDueAtUtc));
            }
        };
        await using var provider = CreateProvider(store, signal, clock, fallbackInterval: TimeSpan.FromHours(2));
        var service = provider.GetRequiredService<GpuSchedulerService>();

        await service.StartAsync(CancellationToken.None);
        try
        {
            Assert.Equal(
                GpuSchedulerWakeReason.StartupRecovery,
                await store.Admissions.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.Equal(
                now.AddHours(2),
                (await clock.ScheduledTimers.Reader
                    .ReadAsync()
                    .AsTask()
                    .WaitAsync(TimeSpan.FromSeconds(2))).DueAtUtc);
            store.SetWake(new GpuSchedulerWakeSnapshot(1, wakeReason, null));
            signal.Notify(wakeReason);
            Assert.Equal(
                wakeReason,
                await store.Admissions.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.Equal(
                deferredDueAtUtc,
                (await clock.ScheduledTimers.Reader
                    .ReadAsync()
                    .AsTask()
                    .WaitAsync(TimeSpan.FromSeconds(2))).DueAtUtc);

            signal.Notify(GpuSchedulerWakeReason.WorkReady);
            Assert.Equal(
                deferredDueAtUtc,
                (await clock.ScheduledTimers.Reader
                    .ReadAsync()
                    .AsTask()
                    .WaitAsync(TimeSpan.FromSeconds(2))).DueAtUtc);

            Assert.Equal(2, store.AdmissionCount);
            Assert.Equal(1, store.ConsumeCount);
            Assert.False(store.Admissions.Reader.TryRead(out _));
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Startup_future_deferred_retry_waits_without_consuming_until_it_is_due()
    {
        var signal = new ChannelGpuSchedulerWakeSignal();
        var sequence = 0;
        var consumptionCompletedOrder = 0;
        var now = DateTimeOffset.Parse("2026-07-29T12:00:00+00:00");
        var dueAtUtc = DateTimeOffset.Parse("2026-07-29T13:00:00+00:00");
        var store = new ScriptedStore(new GpuSchedulerWakeSnapshot(
            1,
            GpuSchedulerWakeReason.DeferredRetry,
            dueAtUtc));
        store.AfterConsumptionCompleted = () =>
            consumptionCompletedOrder = Interlocked.Increment(ref sequence);
        var clock = new ManualTimeProvider(
            now,
            () => Interlocked.Increment(ref sequence));
        store.AfterAdmission = reason =>
        {
            if (reason == GpuSchedulerWakeReason.DeferredRetry)
            {
                store.SetWake(new GpuSchedulerWakeSnapshot(1, 0, null));
            }
        };
        await using var provider = CreateProvider(
            store,
            signal,
            clock,
            fallbackInterval: TimeSpan.FromHours(2));
        var service = provider.GetRequiredService<GpuSchedulerService>();

        await service.StartAsync(CancellationToken.None);
        try
        {
            var initialSchedule = await clock.ScheduledTimers.Reader
                .ReadAsync()
                .AsTask()
                .WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(dueAtUtc, initialSchedule.DueAtUtc);
            Assert.Equal(0, consumptionCompletedOrder);
            Assert.Equal(0, store.ConsumeCount);
            Assert.Equal(0, store.AdmissionCount);

            signal.Notify(GpuSchedulerWakeReason.WorkReady);
            Assert.Equal(
                dueAtUtc,
                (await clock.ScheduledTimers.Reader
                    .ReadAsync()
                    .AsTask()
                    .WaitAsync(TimeSpan.FromSeconds(2))).DueAtUtc);
            Assert.Equal(0, store.AdmissionCount);
            Assert.False(store.Admissions.Reader.TryRead(out _));

            clock.AdvanceTo(dueAtUtc);

            Assert.Equal(
                GpuSchedulerWakeReason.DeferredRetry,
                await store.Admissions.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.Equal(1, store.ConsumeCount);
            Assert.Equal(
                dueAtUtc.AddHours(2),
                (await clock.ScheduledTimers.Reader
                    .ReadAsync()
                    .AsTask()
                    .WaitAsync(TimeSpan.FromSeconds(2))).DueAtUtc);
            Assert.Equal(1, store.AdmissionCount);
            Assert.False(store.Admissions.Reader.TryRead(out _));
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Startup_future_deferred_retry_with_work_and_capacity_release_admits_immediately()
    {
        var signal = new ChannelGpuSchedulerWakeSignal();
        var clock = new FixedTimeProvider(DateTimeOffset.Parse("2026-07-29T12:00:00+00:00"));
        var store = new ScriptedStore(new GpuSchedulerWakeSnapshot(
            1,
            GpuSchedulerWakeReason.DeferredRetry |
            GpuSchedulerWakeReason.WorkReady |
            GpuSchedulerWakeReason.CapacityReleased,
            clock.GetUtcNow().AddHours(1)));
        store.AfterAdmission = _ => store.SetWake(new GpuSchedulerWakeSnapshot(1, 0, null));
        await using var provider = CreateProvider(
            store,
            signal,
            clock,
            fallbackInterval: TimeSpan.FromHours(2));
        var service = provider.GetRequiredService<GpuSchedulerService>();

        await service.StartAsync(CancellationToken.None);
        try
        {
            Assert.Equal(
                GpuSchedulerWakeReason.WorkReady | GpuSchedulerWakeReason.CapacityReleased,
                await store.Admissions.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.Equal(1, store.AdmissionCount);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    [NativeSqlServerFact]
    public async Task Committed_deferral_status_failure_locally_retries_then_schedules_the_durable_short_retry_without_manual_wake()
    {
        var admission = new SqlGpuAdmissionTests(_fixture);
        var factory = await admission.CreateEnvironmentAsync();
        var taskId = await admission.AddReadyAsync(
            factory,
            GpuPriorityLane.InteractiveRetrieval,
            "runtime",
            "settings",
            10);
        await using (var arrange = await factory.CreateDbContextAsync())
        {
            var scheduler = await arrange.GpuSchedulerStates.SingleAsync(candidate => candidate.Id == 1);
            scheduler.WakeGeneration = 1;
            scheduler.PendingWakeReasons = (int)GpuSchedulerWakeReason.WorkReady;
            await arrange.SaveChangesAsync();
        }

        var signal = new ChannelGpuSchedulerWakeSignal();
        var now = DateTimeOffset.Parse("2026-07-29T12:00:00+00:00");
        var dueAtUtc = now.AddMinutes(3);
        var clock = new ManualTimeProvider(now, () => 0);
        var publisher = new FailOncePublisher();
        var gate = new CountingDeferredGate(TimeSpan.FromMinutes(3));
        var services = new ServiceCollection();
        services.AddSingleton(factory);
        services.AddScoped<IGpuSchedulerStore>(_ => new SqlGpuSchedulerStore(factory, timeProvider: clock));
        services.AddSingleton<IGpuAdmissionGate>(gate);
        services.AddSingleton<IStatusEventPublisher>(publisher);
        services.AddSingleton<IGpuSchedulerWakeSignal>(signal);
        services.AddSingleton<TimeProvider>(clock);
        services.AddSingleton(new GpuSchedulerOptions(
            1,
            100,
            TimeSpan.FromMinutes(5),
            TimeSpan.FromHours(1),
            TimeSpan.FromHours(1)));
        services.AddScoped<GpuSchedulerCoordinator>();
        services.AddSingleton<GpuSchedulerService>();
        await using var provider = services.BuildServiceProvider();
        var service = provider.GetRequiredService<GpuSchedulerService>();

        await service.StartAsync(CancellationToken.None);
        try
        {
            await publisher.FirstAttempt.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await publisher.SecondAttempt.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(
                now.AddHours(1),
                (await clock.ScheduledTimers.Reader
                    .ReadAsync()
                    .AsTask()
                    .WaitAsync(TimeSpan.FromSeconds(2))).DueAtUtc);
            Assert.Equal(
                dueAtUtc,
                (await clock.ScheduledTimers.Reader
                    .ReadAsync()
                    .AsTask()
                    .WaitAsync(TimeSpan.FromSeconds(2))).DueAtUtc);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }

        Assert.Equal(1, gate.DecisionCount);
        Assert.Equal(2, publisher.AttemptCount);
        await using var verify = await factory.CreateDbContextAsync();
        var task = await verify.GpuMiniTasks.SingleAsync(candidate => candidate.Id == taskId);
        Assert.Equal(1, task.ReservationAttemptCount);
        Assert.Equal(dueAtUtc, task.DeferredUntilUtc);
        Assert.Empty(await verify.GpuBatches.ToListAsync());
        Assert.Single(await verify.GpuSchedulerOperationReceipts
            .Where(receipt => receipt.OperationKind == "admission")
            .ToListAsync());
    }

    [NativeSqlServerFact]
    public async Task Due_stale_reservation_diagnostic_marks_only_uncertain_and_trusted_reconciliation_frees_capacity()
    {
        var admission = new SqlGpuAdmissionTests(_fixture);
        var factory = await admission.CreateEnvironmentAsync();
        var activeTaskId = await admission.AddReadyAsync(
            factory,
            GpuPriorityLane.DocumentIndexing,
            "runtime",
            "settings",
            10);
        await SqlGpuAdmissionTests.AdmitAsync(factory, SqlGpuAdmissionTests.Admit("slot-a"));
        await using var read = await factory.CreateDbContextAsync();
        var batch = await read.GpuBatches.SingleAsync();
        var activeParentJobId = await read.GpuMiniTasks
            .Where(task => task.Id == activeTaskId)
            .Select(task => task.ParentJobId)
            .SingleAsync();

        var now = DateTimeOffset.Parse("2026-07-29T10:00:00+00:00");
        var diagnosticAge = TimeSpan.FromMinutes(5);
        var clock = new ManualTimeProvider(now, () => 0);
        var signal = new ChannelGpuSchedulerWakeSignal();
        var publisher = new RecordingStatusPublisher();
        var services = new ServiceCollection();
        services.AddSingleton(factory);
        services.AddScoped<IGpuSchedulerStore>(_ => new SqlGpuSchedulerStore(factory, timeProvider: clock));
        services.AddSingleton<IGpuAdmissionGate, NoGpuAdmissionGate>();
        services.AddSingleton<IStatusEventPublisher>(publisher);
        services.AddSingleton<IGpuSchedulerWakeSignal>(signal);
        services.AddSingleton<TimeProvider>(clock);
        services.AddSingleton(new GpuSchedulerOptions(
            1,
            100,
            TimeSpan.FromMinutes(1),
            TimeSpan.FromHours(1),
            diagnosticAge));
        services.AddScoped<GpuSchedulerCoordinator>();
        services.AddSingleton<GpuSchedulerService>();
        await using var provider = services.BuildServiceProvider();
        var service = provider.GetRequiredService<GpuSchedulerService>();

        await service.StartAsync(CancellationToken.None);
        try
        {
            Assert.Equal(
                now.Add(diagnosticAge),
                (await clock.ScheduledTimers.Reader
                    .ReadAsync()
                    .AsTask()
                    .WaitAsync(TimeSpan.FromSeconds(2))).DueAtUtc);

            await using (var fresh = await factory.CreateDbContextAsync())
            {
                Assert.Equal(
                    (int)GpuCapacitySlotState.Reserved,
                    await fresh.GpuCapacitySlots
                        .Where(slot => slot.SlotKey == "slot-a")
                        .Select(slot => slot.State)
                        .SingleAsync());
                Assert.Equal(
                    (int)GpuBatchState.Active,
                    await fresh.GpuBatches
                        .Where(candidate => candidate.Id == batch.Id)
                        .Select(candidate => candidate.State)
                        .SingleAsync());
                Assert.Equal(
                    (int)GpuMiniTaskExecutionState.Active,
                    await fresh.GpuMiniTasks
                        .Where(task => task.Id == activeTaskId)
                        .Select(task => task.ExecutionState)
                        .SingleAsync());
            }

            var waitingTaskId = await admission.AddReadyAsync(
                factory,
                GpuPriorityLane.InteractiveRetrieval,
                "runtime",
                "settings",
                10);
            clock.AdvanceTo(now.Add(diagnosticAge));
            await publisher.Published.Task.WaitAsync(TimeSpan.FromSeconds(2));

            await using (var uncertain = await factory.CreateDbContextAsync())
            {
                Assert.Equal(
                    (int)GpuCapacitySlotState.Uncertain,
                    await uncertain.GpuCapacitySlots
                        .Where(slot => slot.SlotKey == "slot-a")
                        .Select(slot => slot.State)
                        .SingleAsync());
                Assert.Equal(
                    (int)GpuBatchState.CapacityUncertain,
                    await uncertain.GpuBatches
                        .Where(candidate => candidate.Id == batch.Id)
                        .Select(candidate => candidate.State)
                        .SingleAsync());
                Assert.Equal(
                    (int)GpuMiniTaskExecutionState.Active,
                    await uncertain.GpuMiniTasks
                        .Where(task => task.Id == activeTaskId)
                        .Select(task => task.ExecutionState)
                        .SingleAsync());
                Assert.Equal(
                    (int)GpuMiniTaskExecutionState.Ready,
                    await uncertain.GpuMiniTasks
                        .Where(task => task.Id == waitingTaskId)
                        .Select(task => task.ExecutionState)
                        .SingleAsync());
                Assert.Equal(
                    (int)PublicJobState.GpuProcessing,
                    await uncertain.Jobs
                        .Where(job => job.Id == activeParentJobId)
                        .Select(job => job.PublicState)
                        .SingleAsync());
            }

            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await SqlGpuAdmissionTests.AdmitAsync(factory, SqlGpuAdmissionTests.Admit("slot-a")));

            var reconciliationStore = new SqlGpuSchedulerStore(factory, timeProvider: clock);
            var handle = new GpuExecutorBatchHandle(
                batch.Id, "slot-a", "test-executor", batch.AdmissionGeneration, batch.Id);
            var evidenceOperationId = Guid.NewGuid();
            Assert.True((await reconciliationStore.RecordTrustedEvidenceAsync(
                new GpuExecutorTrustedEvidence(
                    evidenceOperationId,
                    handle,
                    "test-verifier",
                    now,
                    GpuExecutorEvidenceClass.CapacityReleaseConfirmed),
                CancellationToken.None)).Accepted);
            var reconciliation = await reconciliationStore.ReconcileCapacityAsync(
                Guid.NewGuid(),
                new GpuTrustedCapacityReconciliation(handle, evidenceOperationId),
                CancellationToken.None);
            Assert.True(reconciliation.Committed);

            await using var released = await factory.CreateDbContextAsync();
            Assert.Equal(
                (int)GpuCapacitySlotState.Available,
                await released.GpuCapacitySlots
                    .Where(slot => slot.SlotKey == "slot-a")
                    .Select(slot => slot.State)
                    .SingleAsync());
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    [NativeSqlServerFact]
    public async Task Elapsed_fallback_diagnostic_and_heartbeat_thresholds_do_not_admit_release_requeue_or_replace_results()
    {
        var admission = new SqlGpuAdmissionTests(_fixture);
        var factory = await admission.CreateEnvironmentAsync();
        var activeTaskId = await admission.AddReadyAsync(
            factory,
            GpuPriorityLane.DocumentIndexing,
            "runtime",
            "settings",
            10);
        await SqlGpuAdmissionTests.AdmitAsync(factory, SqlGpuAdmissionTests.Admit("slot-a"));
        var waitingTaskId = await admission.AddReadyAsync(
            factory,
            GpuPriorityLane.InteractiveRetrieval,
            "runtime",
            "settings",
            10);
        await using var read = await factory.CreateDbContextAsync();
        var batch = await read.GpuBatches.SingleAsync();
        var handle = new GpuExecutorBatchHandle(
            batch.Id,
            "slot-a",
            "test-executor",
            batch.AdmissionGeneration,
            batch.Id);

        var now = DateTimeOffset.Parse("2026-07-29T10:00:00+00:00");
        var fallbackInterval = TimeSpan.FromMinutes(1);
        var diagnosticAge = TimeSpan.FromMinutes(5);
        var clock = new ManualTimeProvider(now, () => 0);
        var setupStore = new SqlGpuSchedulerStore(factory, timeProvider: clock);
        Assert.True((await setupStore.AcknowledgeAsync(
            new GpuExecutorAcknowledgement(Guid.NewGuid(), handle),
            CancellationToken.None)).Accepted);
        Assert.True((await setupStore.RecordReceiptAsync(
            new GpuExecutorResultReceipt(
                Guid.NewGuid(),
                handle,
                activeTaskId,
                GpuMiniTaskBoundaryDisposition.Completed,
                new byte[32],
                GpuExecutorEvidenceClass.TaskOutcomeConfirmed),
            CancellationToken.None)).Accepted);

        var signal = new ChannelGpuSchedulerWakeSignal();
        var publisher = new RecordingStatusPublisher();
        var services = new ServiceCollection();
        services.AddSingleton(factory);
        services.AddScoped<IGpuSchedulerStore>(_ => new SqlGpuSchedulerStore(factory, timeProvider: clock));
        services.AddSingleton<IGpuAdmissionGate, NoGpuAdmissionGate>();
        services.AddSingleton<IStatusEventPublisher>(publisher);
        services.AddSingleton<IGpuSchedulerWakeSignal>(signal);
        services.AddSingleton<TimeProvider>(clock);
        services.AddSingleton(new GpuSchedulerOptions(
            1,
            100,
            TimeSpan.FromMinutes(1),
            fallbackInterval,
            diagnosticAge));
        services.AddScoped<GpuSchedulerCoordinator>();
        services.AddSingleton<GpuSchedulerService>();
        await using var provider = services.BuildServiceProvider();
        var service = provider.GetRequiredService<GpuSchedulerService>();

        await service.StartAsync(CancellationToken.None);
        try
        {
            Assert.Equal(now.Add(fallbackInterval), await ReadScheduledDueAsync(clock));
            var beforeElapsedTime = await ReadElapsedTimeSnapshotAsync(factory, activeTaskId, waitingTaskId);
            Assert.Equal(1, beforeElapsedTime.BatchCount);
            Assert.Equal(1, beforeElapsedTime.ResultReceiptCount);
            Assert.NotNull(beforeElapsedTime.AcceptedReceipt);

            clock.AdvanceTo(now.Add(fallbackInterval));
            Assert.Equal(now.Add(fallbackInterval + fallbackInterval), await ReadScheduledDueAsync(clock));
            Assert.Equal(beforeElapsedTime, await ReadElapsedTimeSnapshotAsync(factory, activeTaskId, waitingTaskId));

            clock.AdvanceTo(now.AddMinutes(4));
            Assert.Equal(now.Add(diagnosticAge), await ReadScheduledDueAsync(clock));
            Assert.Equal(beforeElapsedTime, await ReadElapsedTimeSnapshotAsync(factory, activeTaskId, waitingTaskId));

            clock.AdvanceTo(now.Add(diagnosticAge));
            await publisher.Published.Task.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.Equal(beforeElapsedTime with
            {
                DispatchState = (int)GpuExecutorDispatchState.DeliveryUncertain,
                BatchState = (int)GpuBatchState.CapacityUncertain,
                SlotState = (int)GpuCapacitySlotState.Uncertain
            }, await ReadElapsedTimeSnapshotAsync(factory, activeTaskId, waitingTaskId));
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Stale_diagnostic_status_failure_reuses_the_pending_operation_id_on_retry()
    {
        var signal = new ChannelGpuSchedulerWakeSignal();
        var now = DateTimeOffset.Parse("2026-07-29T10:00:00+00:00");
        var clock = new ManualTimeProvider(now, () => 0);
        var request = new GpuCapacityUncertaintyRequest(
            Guid.NewGuid(),
            "slot-a",
            "owner-a",
            1,
            now,
            new byte[8]);
        var store = new ScriptedStore(new GpuSchedulerWakeSnapshot(0, 0, null))
        {
            StaleCapacityReservations = [request]
        };
        var publisher = new FailOncePublisher();
        var services = new ServiceCollection();
        services.AddSingleton<IGpuSchedulerStore>(store);
        services.AddSingleton<IGpuAdmissionGate, NoGpuAdmissionGate>();
        services.AddSingleton<IStatusEventPublisher>(publisher);
        services.AddSingleton<IGpuSchedulerWakeSignal>(signal);
        services.AddSingleton<TimeProvider>(clock);
        services.AddSingleton(new GpuSchedulerOptions(
            1,
            1,
            TimeSpan.FromMinutes(1),
            TimeSpan.FromHours(1),
            TimeSpan.FromMinutes(5)));
        services.AddScoped<GpuSchedulerCoordinator>();
        services.AddSingleton<GpuSchedulerService>();
        await using var provider = services.BuildServiceProvider();
        var service = provider.GetRequiredService<GpuSchedulerService>();

        await service.StartAsync(CancellationToken.None);
        try
        {
            await publisher.FirstAttempt.Task.WaitAsync(TimeSpan.FromSeconds(2));
            signal.Notify(GpuSchedulerWakeReason.WorkReady);
            await publisher.SecondAttempt.Task.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.Equal(2, store.UncertaintyOperationIds.Count);
            Assert.All(store.UncertaintyOperationIds, operationId =>
                Assert.Equal(store.UncertaintyOperationIds[0], operationId));
            Assert.All(store.UncertaintyRequests, observed => Assert.Equal(request, observed));
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Ordinary_busy_work_and_unchanged_fallback_do_not_trigger_another_admission_round()
    {
        var signal = new ChannelGpuSchedulerWakeSignal();
        var now = DateTimeOffset.Parse("2026-07-29T12:00:00+00:00");
        var fallbackInterval = TimeSpan.FromMilliseconds(10);
        var clock = new ManualTimeProvider(now, () => 0);
        var store = new ScriptedStore(new GpuSchedulerWakeSnapshot(0, 0, null));
        await using var provider = CreateProvider(store, signal, clock, fallbackInterval);
        var service = provider.GetRequiredService<GpuSchedulerService>();

        await service.StartAsync(CancellationToken.None);
        try
        {
            Assert.Equal(GpuSchedulerWakeReason.StartupRecovery, await store.Admissions.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.Equal(
                now.Add(fallbackInterval),
                (await clock.ScheduledTimers.Reader
                    .ReadAsync()
                    .AsTask()
                    .WaitAsync(TimeSpan.FromSeconds(2))).DueAtUtc);
            signal.Notify(GpuSchedulerWakeReason.WorkReady);
            Assert.Equal(
                now.Add(fallbackInterval),
                (await clock.ScheduledTimers.Reader
                    .ReadAsync()
                    .AsTask()
                    .WaitAsync(TimeSpan.FromSeconds(2))).DueAtUtc);

            clock.AdvanceTo(now.Add(fallbackInterval));
            Assert.Equal(
                now.Add(fallbackInterval + fallbackInterval),
                (await clock.ScheduledTimers.Reader
                    .ReadAsync()
                    .AsTask()
                    .WaitAsync(TimeSpan.FromSeconds(2))).DueAtUtc);

            Assert.Equal(1, store.AdmissionCount);
            Assert.False(store.Admissions.Reader.TryRead(out _));
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Due_deferred_work_runs_one_deferred_retry_then_recomputes_away_the_zero_delay_wait()
    {
        var signal = new ChannelGpuSchedulerWakeSignal();
        var now = DateTimeOffset.Parse("2026-07-29T12:00:00+00:00");
        var fallbackInterval = TimeSpan.FromMilliseconds(10);
        var clock = new ManualTimeProvider(now, () => 0);
        var store = new ScriptedStore(new GpuSchedulerWakeSnapshot(0, 0, null));
        store.AfterAdmission = reason => store.SetWake(reason == GpuSchedulerWakeReason.StartupRecovery
            ? new GpuSchedulerWakeSnapshot(0, 0, clock.GetUtcNow())
            : new GpuSchedulerWakeSnapshot(0, 0, null));
        await using var provider = CreateProvider(store, signal, clock, fallbackInterval);
        var service = provider.GetRequiredService<GpuSchedulerService>();

        await service.StartAsync(CancellationToken.None);
        try
        {
            Assert.Equal(GpuSchedulerWakeReason.StartupRecovery, await store.Admissions.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.Equal(GpuSchedulerWakeReason.DeferredRetry, await store.Admissions.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.Equal(
                now.Add(fallbackInterval),
                (await clock.ScheduledTimers.Reader
                    .ReadAsync()
                    .AsTask()
                    .WaitAsync(TimeSpan.FromSeconds(2))).DueAtUtc);

            clock.AdvanceTo(now.Add(fallbackInterval));
            Assert.Equal(
                now.Add(fallbackInterval + fallbackInterval),
                (await clock.ScheduledTimers.Reader
                    .ReadAsync()
                    .AsTask()
                    .WaitAsync(TimeSpan.FromSeconds(2))).DueAtUtc);

            Assert.Equal(2, store.AdmissionCount);
            Assert.False(store.Admissions.Reader.TryRead(out _));
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Each_successful_service_start_performs_one_startup_recovery_read()
    {
        var signal = new ChannelGpuSchedulerWakeSignal();
        var clock = new FixedTimeProvider(DateTimeOffset.Parse("2026-07-29T12:00:00+00:00"));
        var store = new ScriptedStore(new GpuSchedulerWakeSnapshot(0, 0, null));

        await using (var firstProvider = CreateProvider(store, signal, clock, fallbackInterval: TimeSpan.FromHours(1)))
        {
            var firstService = firstProvider.GetRequiredService<GpuSchedulerService>();
            await firstService.StartAsync(CancellationToken.None);
            Assert.Equal(GpuSchedulerWakeReason.StartupRecovery, await store.Admissions.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2)));
            await firstService.StopAsync(CancellationToken.None);
        }

        await using (var restartedProvider = CreateProvider(store, signal, clock, fallbackInterval: TimeSpan.FromHours(1)))
        {
            var restartedService = restartedProvider.GetRequiredService<GpuSchedulerService>();
            await restartedService.StartAsync(CancellationToken.None);
            Assert.Equal(GpuSchedulerWakeReason.StartupRecovery, await store.Admissions.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2)));
            await restartedService.StopAsync(CancellationToken.None);
        }

        Assert.Equal(2, store.AdmissionCount);
    }

    [Fact]
    public async Task Wake_consumption_retry_reuses_the_same_operation_id_after_a_store_exception()
    {
        var signal = new ChannelGpuSchedulerWakeSignal();
        var clock = new FixedTimeProvider(DateTimeOffset.Parse("2026-07-29T12:00:00+00:00"));
        var store = new ScriptedStore(
            new GpuSchedulerWakeSnapshot(1, GpuSchedulerWakeReason.WorkReady, null),
            consumptionFailuresBeforeSuccess: 1);
        await using var provider = CreateProvider(store, signal, clock, fallbackInterval: TimeSpan.FromHours(1));
        var service = provider.GetRequiredService<GpuSchedulerService>();

        await service.StartAsync(CancellationToken.None);
        try
        {
            await store.FirstConsumptionAttempt.Task.WaitAsync(TimeSpan.FromSeconds(2));
            signal.Notify(GpuSchedulerWakeReason.WorkReady);

            Assert.Equal(
                GpuSchedulerWakeReason.WorkReady,
                await store.Admissions.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.Equal(2, store.ConsumptionOperationIds.Count);
            Assert.Single(store.ConsumptionOperationIds.Distinct());
            Assert.NotEqual(Guid.Empty, store.ConsumptionOperationIds[0]);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Committed_wake_consumption_retry_replays_capacity_released_before_startup_recovery()
    {
        var signal = new ChannelGpuSchedulerWakeSignal();
        var clock = new FixedTimeProvider(DateTimeOffset.Parse("2026-07-29T12:00:00+00:00"));
        var originalReasons = GpuSchedulerWakeReason.DeferredRetry | GpuSchedulerWakeReason.CapacityReleased;
        var store = new ScriptedStore(
            new GpuSchedulerWakeSnapshot(7, originalReasons, clock.GetUtcNow().AddHours(1)),
            commitUnknownConsumptionFailuresBeforeSuccess: 1);
        await using var provider = CreateProvider(store, signal, clock, fallbackInterval: TimeSpan.FromHours(1));
        var service = provider.GetRequiredService<GpuSchedulerService>();

        await service.StartAsync(CancellationToken.None);
        try
        {
            await store.CommittedWakeConsumptionThenThrown.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal((GpuSchedulerWakeReason)0, store.CurrentWake.Reasons);
            signal.Notify(GpuSchedulerWakeReason.WorkReady);

            Assert.Equal(
                GpuSchedulerWakeReason.CapacityReleased,
                await store.Admissions.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.Equal(2, store.ConsumeCount);
            Assert.Single(store.ConsumptionOperationIds.Distinct());
            Assert.Equal([7L, 7L], store.ConsumptionExpectedGenerations);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    [Theory]
    [InlineData(GpuSchedulerWakeReason.WorkReady)]
    [InlineData(GpuSchedulerWakeReason.CapacityReleased)]
    public async Task Restart_after_committed_wake_admission_before_acknowledgement_replays_the_same_admission_receipt_once(
        GpuSchedulerWakeReason wakeReason)
    {
        var now = DateTimeOffset.Parse("2026-07-29T12:00:00+00:00");
        var dueAtUtc = now.AddHours(1);
        var clock = new FixedTimeProvider(now);
        var store = new RestartAfterCommittedAdmissionStore(
            new GpuSchedulerWakeSnapshot(7, wakeReason, null),
            dueAtUtc)
        {
            BlockAcknowledgement = true
        };

        await using (var firstProvider = CreateProvider(
            store,
            new ChannelGpuSchedulerWakeSignal(),
            clock,
            fallbackInterval: TimeSpan.FromHours(2)))
        {
            var firstService = firstProvider.GetRequiredService<GpuSchedulerService>();
            await firstService.StartAsync(CancellationToken.None);
            await store.FirstAdmissionCommitted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await firstService.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));
        }

        Assert.Equal(1, store.NewAdmissionDecisionCount);
        Assert.NotNull(store.CurrentWake.ConsumptionOperationId);
        store.BlockAcknowledgement = false;
        await using var secondProvider = CreateProvider(
            store,
            new ChannelGpuSchedulerWakeSignal(),
            clock,
            fallbackInterval: TimeSpan.FromHours(2));
        var secondService = secondProvider.GetRequiredService<GpuSchedulerService>();
        await secondService.StartAsync(CancellationToken.None);
        try
        {
            await store.Acknowledged.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(2, store.AdmissionOperationIds.Count);
            Assert.Single(store.AdmissionOperationIds.Distinct());
            Assert.Equal(1, store.NewAdmissionDecisionCount);
            Assert.Equal(dueAtUtc, store.CurrentWake.NextDeferredAtUtc);
            Assert.Equal(GpuSchedulerWakeReason.DeferredRetry, store.CurrentWake.Reasons);
        }
        finally
        {
            await secondService.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Restart_after_a_composite_wake_admission_crosses_its_old_due_time_but_replays_the_original_effective_reason()
    {
        var now = DateTimeOffset.Parse("2026-07-29T12:00:00+00:00");
        var dueAtUtc = now.AddHours(1);
        var clock = new ManualTimeProvider(now, () => 0);
        var store = new RestartAfterCommittedAdmissionStore(
            new GpuSchedulerWakeSnapshot(
                7,
                GpuSchedulerWakeReason.WorkReady | GpuSchedulerWakeReason.DeferredRetry,
                dueAtUtc,
                EffectiveAdmissionReasons: GpuSchedulerWakeReason.WorkReady),
            dueAtUtc)
        {
            BlockAcknowledgement = true
        };

        await using (var firstProvider = CreateProvider(
            store,
            new ChannelGpuSchedulerWakeSignal(),
            clock,
            fallbackInterval: TimeSpan.FromHours(2)))
        {
            var firstService = firstProvider.GetRequiredService<GpuSchedulerService>();
            await firstService.StartAsync(CancellationToken.None);
            await store.FirstAdmissionCommitted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await firstService.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));
        }

        clock.AdvanceTo(dueAtUtc.AddMinutes(1));
        store.BlockAcknowledgement = false;
        store.BlockNewAdmissionDecision = true;
        await using var secondProvider = CreateProvider(
            store,
            new ChannelGpuSchedulerWakeSignal(),
            clock,
            fallbackInterval: TimeSpan.FromHours(2));
        var secondService = secondProvider.GetRequiredService<GpuSchedulerService>();
        await secondService.StartAsync(CancellationToken.None);
        try
        {
            await store.Acknowledged.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await store.SubsequentAdmissionStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.Equal(
                [
                    GpuSchedulerWakeReason.WorkReady,
                    GpuSchedulerWakeReason.WorkReady,
                    GpuSchedulerWakeReason.DeferredRetry
                ],
                store.AdmissionReasons);
            Assert.Equal(1, store.NewAdmissionDecisionCount);
            Assert.Equal(3, store.AdmissionOperationIds.Count);
            Assert.Single(store.AdmissionOperationIds.Take(2).Distinct());
            Assert.NotEqual(store.AdmissionOperationIds[0], store.AdmissionOperationIds[2]);
        }
        finally
        {
            await secondService.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Distinct_durable_wake_tokens_receive_distinct_admission_operation_ids()
    {
        var now = DateTimeOffset.Parse("2026-07-29T12:00:00+00:00");
        var clock = new FixedTimeProvider(now);

        async Task<(Guid ConsumptionOperationId, Guid AdmissionOperationId)> CommitUntilAcknowledgement(Guid consumptionOperationId)
        {
            var store = new RestartAfterCommittedAdmissionStore(
                new GpuSchedulerWakeSnapshot(
                    7,
                    GpuSchedulerWakeReason.WorkReady,
                    null,
                    consumptionOperationId),
                now.AddHours(1))
            {
                BlockAcknowledgement = true
            };
            await using var provider = CreateProvider(
                store,
                new ChannelGpuSchedulerWakeSignal(),
                clock,
                fallbackInterval: TimeSpan.FromHours(2));
            var service = provider.GetRequiredService<GpuSchedulerService>();
            await service.StartAsync(CancellationToken.None);
            try
            {
                await store.FirstAdmissionCommitted.Task.WaitAsync(TimeSpan.FromSeconds(2));
                return (
                    store.CurrentWake.ConsumptionOperationId!.Value,
                    Assert.Single(store.AdmissionOperationIds));
            }
            finally
            {
                await service.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));
            }
        }

        var first = await CommitUntilAcknowledgement(Guid.Parse("11111111-1111-1111-1111-111111111111"));
        var second = await CommitUntilAcknowledgement(Guid.Parse("22222222-2222-2222-2222-222222222222"));

        Assert.NotEqual(first.ConsumptionOperationId, second.ConsumptionOperationId);
        Assert.NotEqual(first.AdmissionOperationId, second.AdmissionOperationId);
        Assert.NotEqual(first.ConsumptionOperationId, first.AdmissionOperationId);
        Assert.NotEqual(second.ConsumptionOperationId, second.AdmissionOperationId);
    }

    [Fact]
    public async Task Wake_consumption_mismatch_retries_the_original_request_before_consuming_a_new_generation()
    {
        var signal = new ChannelGpuSchedulerWakeSignal();
        var clock = new FixedTimeProvider(DateTimeOffset.Parse("2026-07-29T12:00:00+00:00"));
        var store = new ScriptedStore(
            new GpuSchedulerWakeSnapshot(1, GpuSchedulerWakeReason.WorkReady, null),
            consumptionFailuresBeforeSuccess: 1);
        await using var provider = CreateProvider(store, signal, clock, fallbackInterval: TimeSpan.FromHours(1));
        var service = provider.GetRequiredService<GpuSchedulerService>();

        await service.StartAsync(CancellationToken.None);
        try
        {
            await store.PreCommitWakeConsumptionThenThrown.Task.WaitAsync(TimeSpan.FromSeconds(2));
            store.SetWake(new GpuSchedulerWakeSnapshot(
                2,
                GpuSchedulerWakeReason.CapacityReleased,
                clock.GetUtcNow().AddHours(1)));
            signal.Notify(GpuSchedulerWakeReason.CapacityReleased);

            Assert.Equal(
                GpuSchedulerWakeReason.CapacityReleased,
                await store.Admissions.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.Equal([1L, 1L, 2L], store.ConsumptionExpectedGenerations);
            Assert.Equal(store.ConsumptionOperationIds[0], store.ConsumptionOperationIds[1]);
            Assert.NotEqual(store.ConsumptionOperationIds[1], store.ConsumptionOperationIds[2]);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Scheduler_exception_is_logged_and_retries_startup_recovery_without_mutating_the_store()
    {
        var signal = new ChannelGpuSchedulerWakeSignal();
        var clock = new FixedTimeProvider(DateTimeOffset.Parse("2026-07-29T12:00:00+00:00"));
        var store = new ScriptedStore(new GpuSchedulerWakeSnapshot(0, 0, null), failuresBeforeSuccess: 1);
        var logger = new RecordingLogger();
        await using var provider = CreateProvider(store, signal, clock, fallbackInterval: TimeSpan.FromHours(1), logger);
        var service = provider.GetRequiredService<GpuSchedulerService>();

        await service.StartAsync(CancellationToken.None);
        try
        {
            await store.FirstAttempt.Task.WaitAsync(TimeSpan.FromSeconds(2));
            signal.Notify(GpuSchedulerWakeReason.WorkReady);

            Assert.Equal(GpuSchedulerWakeReason.StartupRecovery, await store.Admissions.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.Equal(1, logger.ErrorCount);
            Assert.Equal(2, store.AdmissionOperationIds.Count);
            Assert.Single(store.AdmissionOperationIds.Distinct());
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Cancellation_disposes_the_pending_wake_wait_and_stops_cleanly()
    {
        var signal = new CancellationAwareWakeSignal();
        var clock = new FixedTimeProvider(DateTimeOffset.Parse("2026-07-29T12:00:00+00:00"));
        var store = new ScriptedStore(new GpuSchedulerWakeSnapshot(0, 0, null));
        await using var provider = CreateProvider(store, signal, clock, fallbackInterval: TimeSpan.FromHours(1));
        var service = provider.GetRequiredService<GpuSchedulerService>();

        await service.StartAsync(CancellationToken.None);
        Assert.Equal(GpuSchedulerWakeReason.StartupRecovery, await store.Admissions.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2)));
        await signal.WaitStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        await service.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));
        await signal.WaitCancelled.Task.WaitAsync(TimeSpan.FromSeconds(2));
    }

    private static ServiceProvider CreateProvider(
        IGpuSchedulerStore store,
        IGpuSchedulerWakeSignal signal,
        TimeProvider clock,
        TimeSpan fallbackInterval,
        ILogger<GpuSchedulerService>? logger = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton(store);
        services.AddSingleton<IGpuAdmissionGate, NoGpuAdmissionGate>();
        services.AddSingleton<IStatusEventPublisher, NullPublisher>();
        services.AddSingleton(signal);
        services.AddSingleton<TimeProvider>(clock);
        services.AddSingleton(new GpuSchedulerOptions(
            1,
            1,
            TimeSpan.FromMinutes(1),
            fallbackInterval,
            TimeSpan.FromDays(1)));
        services.AddScoped<GpuSchedulerCoordinator>();
        services.AddSingleton<GpuSchedulerService>();
        if (logger is not null)
        {
            services.AddSingleton(logger);
        }

        return services.BuildServiceProvider();
    }

    private static async Task<DateTimeOffset> ReadScheduledDueAsync(ManualTimeProvider clock) =>
        (await clock.ScheduledTimers.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2))).DueAtUtc;

    private static async Task<ElapsedTimeSnapshot> ReadElapsedTimeSnapshotAsync(
        IDbContextFactory<FluxKnowledgeDbContext> factory,
        Guid activeTaskId,
        Guid waitingTaskId)
    {
        await using var context = await factory.CreateDbContextAsync();
        var activeParentJobId = await context.GpuMiniTasks
            .Where(task => task.Id == activeTaskId)
            .Select(task => task.ParentJobId)
            .SingleAsync();
        var receipt = await context.GpuExecutorResultReceipts.SingleOrDefaultAsync();
        return new ElapsedTimeSnapshot(
            await context.GpuExecutorDispatches.Select(dispatch => dispatch.State).SingleAsync(),
            await context.GpuBatches.Select(candidate => candidate.State).SingleAsync(),
            await context.GpuCapacitySlots.Select(slot => slot.State).SingleAsync(),
            await context.GpuMiniTasks.Where(task => task.Id == activeTaskId).Select(task => task.ExecutionState).SingleAsync(),
            await context.GpuMiniTasks.Where(task => task.Id == waitingTaskId).Select(task => task.ExecutionState).SingleAsync(),
            await context.Jobs.Where(job => job.Id == activeParentJobId).Select(job => job.PublicState).SingleAsync(),
            await context.GpuExecutorResultReceipts.CountAsync(),
            await context.GpuExecutorEvidence.CountAsync(),
            await context.GpuBatches.CountAsync(),
            receipt is null
                ? null
                : new AcceptedReceiptSnapshot(
                    receipt.OperationId,
                    receipt.DispatchId,
                    receipt.BatchId,
                    receipt.MiniTaskId,
                    receipt.ExecutorKey,
                    receipt.AdmissionGeneration,
                    receipt.Disposition,
                    receipt.EvidenceClass,
                    receipt.OpaqueResultDigest is null ? null : Convert.ToHexString(receipt.OpaqueResultDigest),
                    receipt.RequestFingerprint,
                    receipt.CreatedAtUtc));
    }

    private sealed record ElapsedTimeSnapshot(
        int DispatchState,
        int BatchState,
        int SlotState,
        int ActiveTaskState,
        int WaitingTaskState,
        int ActiveParentJobState,
        int ResultReceiptCount,
        int EvidenceCount,
        int BatchCount,
        AcceptedReceiptSnapshot? AcceptedReceipt);

    private sealed record AcceptedReceiptSnapshot(
        Guid OperationId,
        Guid DispatchId,
        Guid BatchId,
        Guid MiniTaskId,
        string ExecutorKey,
        long AdmissionGeneration,
        int Disposition,
        int EvidenceClass,
        string? OpaqueResultDigest,
        string RequestFingerprint,
        DateTimeOffset CreatedAtUtc);

    private sealed class RestartAfterCommittedAdmissionStore(
        GpuSchedulerWakeSnapshot wake,
        DateTimeOffset deferredUntilUtc) : IGpuSchedulerStore
    {
        private GpuSchedulerWakeSnapshot _pendingWake = wake with { ConsumptionOperationId = null };
        private GpuSchedulerWakeSnapshot? _inFlightWake = wake.ConsumptionOperationId is null ? null : wake;
        private Guid? _admissionOperationId;

        public bool BlockAcknowledgement { get; set; }
        public bool BlockNewAdmissionDecision { get; set; }
        public int NewAdmissionDecisionCount { get; private set; }
        public List<Guid> AdmissionOperationIds { get; } = [];
        public List<GpuSchedulerWakeReason> AdmissionReasons { get; } = [];
        public Channel<GpuSchedulerWakeReason> Admissions { get; } = Channel.CreateUnbounded<GpuSchedulerWakeReason>();
        public TaskCompletionSource FirstAdmissionCommitted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Acknowledged { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource SubsequentAdmissionStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public GpuSchedulerWakeSnapshot CurrentWake => _inFlightWake ?? _pendingWake;

        public ValueTask<GpuMiniTaskHandoffResult> GpuTaskHandoffAsync(GpuMiniTaskHandoffRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<GpuBatchCallbackResult> ApplyBatchCallbackAsync(Guid operationId, GpuBatchCallback callback, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<GpuDiagnosticTransitionResult> MarkCapacityUncertainAsync(Guid operationId, GpuCapacityUncertaintyRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<GpuTrustedReconciliationResult> ReconcileCapacityAsync(Guid operationId, GpuTrustedCapacityReconciliation request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<GpuTrustedReconciliationResult> ReconcileTaskOutcomeAsync(Guid operationId, GpuTaskOutcomeReconciliation request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<IReadOnlyList<GpuCapacityUncertaintyRequest>> ReadStaleCapacityReservationsAsync(DateTimeOffset heartbeatNotAfterUtc, CancellationToken cancellationToken) => ValueTask.FromResult<IReadOnlyList<GpuCapacityUncertaintyRequest>>([]);
        public ValueTask<GpuSchedulerWakeSnapshot> ReadWakeStateAsync(CancellationToken cancellationToken) => ValueTask.FromResult(CurrentWake);

        public ValueTask<GpuSchedulerWakeConsumption> ConsumeWakeAsync(Guid operationId, long expectedGeneration, CancellationToken cancellationToken)
        {
            if (_inFlightWake is not null)
            {
                return ValueTask.FromResult(new GpuSchedulerWakeConsumption(true, _inFlightWake));
            }

            if (_pendingWake.Generation != expectedGeneration)
            {
                return ValueTask.FromResult(new GpuSchedulerWakeConsumption(false, _pendingWake));
            }

            _inFlightWake = _pendingWake with { ConsumptionOperationId = operationId };
            _pendingWake = _pendingWake with { Reasons = 0, NextDeferredAtUtc = null };
            return ValueTask.FromResult(new GpuSchedulerWakeConsumption(true, _inFlightWake));
        }

        public async ValueTask<GpuSchedulerAdmissionRoundResult> RunAdmissionRoundAsync(
            Guid operationId,
            GpuSchedulerWakeReason wakeReason,
            GpuSchedulerOptions options,
            Func<GpuBatchCandidate, CancellationToken, ValueTask<GpuAdmissionDecision>> decideAdmission,
            CancellationToken cancellationToken)
        {
            AdmissionOperationIds.Add(operationId);
            AdmissionReasons.Add(wakeReason);
            Admissions.Writer.TryWrite(wakeReason);
            if (_admissionOperationId is not null &&
                _admissionOperationId != operationId &&
                BlockNewAdmissionDecision)
            {
                SubsequentAdmissionStarted.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            if (_admissionOperationId is null)
            {
                _admissionOperationId = operationId;
                NewAdmissionDecisionCount++;
                _pendingWake = _pendingWake with
                {
                    Generation = _pendingWake.Generation + 1,
                    Reasons = GpuSchedulerWakeReason.DeferredRetry,
                    NextDeferredAtUtc = deferredUntilUtc,
                    EffectiveAdmissionReasons = null
                };
                FirstAdmissionCommitted.TrySetResult();
            }

            if (_admissionOperationId != operationId)
            {
                NewAdmissionDecisionCount++;
            }

            return new GpuSchedulerAdmissionRoundResult(
                true,
                GpuAdmissionDisposition.Defer,
                deferredUntilUtc);
        }

        public async ValueTask<bool> AcknowledgeWakeAsync(Guid operationId, Guid consumptionOperationId, CancellationToken cancellationToken)
        {
            if (BlockAcknowledgement)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            if (_inFlightWake?.ConsumptionOperationId != consumptionOperationId)
            {
                return false;
            }

            _inFlightWake = null;
            Acknowledged.TrySetResult();
            return true;
        }

        public ValueTask<GpuSchedulerStatusSnapshot> ReadGpuSchedulerStatusAsync(CancellationToken cancellationToken) => ValueTask.FromResult(GpuSchedulerStatusSnapshot.Empty);
    }

    private sealed class RestartableWakeStore(GpuSchedulerWakeSnapshot wake) : IGpuSchedulerStore
    {
        private GpuSchedulerWakeSnapshot _wake = wake;

        public bool BlockConsumption { get; set; }
        public int AdmissionCount { get; private set; }
        public int AcknowledgementCount { get; private set; }
        public GpuSchedulerWakeSnapshot CurrentWake => _wake;
        public TaskCompletionSource FirstConsumptionRecorded { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Acknowledged { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Channel<GpuSchedulerWakeReason> Admissions { get; } = Channel.CreateUnbounded<GpuSchedulerWakeReason>();

        public ValueTask<GpuMiniTaskHandoffResult> GpuTaskHandoffAsync(GpuMiniTaskHandoffRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<GpuSchedulerAdmissionRoundResult> RunAdmissionRoundAsync(Guid operationId, GpuSchedulerWakeReason wakeReason, GpuSchedulerOptions options, Func<GpuBatchCandidate, CancellationToken, ValueTask<GpuAdmissionDecision>> decideAdmission, CancellationToken cancellationToken)
        {
            AdmissionCount++;
            Admissions.Writer.TryWrite(wakeReason);
            return ValueTask.FromResult(new GpuSchedulerAdmissionRoundResult(false, GpuAdmissionDisposition.Busy, null));
        }
        public ValueTask<GpuBatchCallbackResult> ApplyBatchCallbackAsync(Guid operationId, GpuBatchCallback callback, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<GpuDiagnosticTransitionResult> MarkCapacityUncertainAsync(Guid operationId, GpuCapacityUncertaintyRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<GpuTrustedReconciliationResult> ReconcileCapacityAsync(Guid operationId, GpuTrustedCapacityReconciliation request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<GpuTrustedReconciliationResult> ReconcileTaskOutcomeAsync(Guid operationId, GpuTaskOutcomeReconciliation request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<IReadOnlyList<GpuCapacityUncertaintyRequest>> ReadStaleCapacityReservationsAsync(DateTimeOffset heartbeatNotAfterUtc, CancellationToken cancellationToken) => ValueTask.FromResult<IReadOnlyList<GpuCapacityUncertaintyRequest>>([]);
        public ValueTask<GpuSchedulerWakeSnapshot> ReadWakeStateAsync(CancellationToken cancellationToken) => ValueTask.FromResult(_wake);
        public async ValueTask<GpuSchedulerWakeConsumption> ConsumeWakeAsync(Guid operationId, long expectedGeneration, CancellationToken cancellationToken)
        {
            if (_wake.Generation != expectedGeneration)
            {
                return new GpuSchedulerWakeConsumption(false, _wake);
            }

            var snapshot = _wake with { ConsumptionOperationId = operationId };
            FirstConsumptionRecorded.TrySetResult();
            if (BlockConsumption)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            return new GpuSchedulerWakeConsumption(true, snapshot);
        }
        public ValueTask<bool> AcknowledgeWakeAsync(Guid operationId, Guid consumptionOperationId, CancellationToken cancellationToken)
        {
            if (consumptionOperationId == Guid.Empty)
            {
                return ValueTask.FromResult(false);
            }

            AcknowledgementCount++;
            _wake = _wake with { Reasons = 0 };
            Acknowledged.TrySetResult();
            return ValueTask.FromResult(true);
        }
        public ValueTask<GpuSchedulerStatusSnapshot> ReadGpuSchedulerStatusAsync(CancellationToken cancellationToken) => ValueTask.FromResult(GpuSchedulerStatusSnapshot.Empty);
    }

    private sealed class RecordingStore(GpuSchedulerWakeSnapshot wake) : IGpuSchedulerStore
    {
        private GpuSchedulerWakeSnapshot _wake = wake;
        private int _admissions;

        public TaskCompletionSource<GpuSchedulerWakeReason> FirstAdmission { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<GpuSchedulerWakeReason> SecondAdmission { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void SetWake(GpuSchedulerWakeSnapshot wake) => _wake = wake;

        public ValueTask<GpuMiniTaskHandoffResult> GpuTaskHandoffAsync(GpuMiniTaskHandoffRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<GpuSchedulerAdmissionRoundResult> RunAdmissionRoundAsync(Guid operationId, GpuSchedulerWakeReason wakeReason, GpuSchedulerOptions options, Func<GpuBatchCandidate, CancellationToken, ValueTask<GpuAdmissionDecision>> decideAdmission, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _admissions) == 1)
            {
                FirstAdmission.TrySetResult(wakeReason);
            }
            else
            {
                SecondAdmission.TrySetResult(wakeReason);
            }

            return ValueTask.FromResult(new GpuSchedulerAdmissionRoundResult(false, GpuAdmissionDisposition.Busy, null));
        }

        public ValueTask<GpuBatchCallbackResult> ApplyBatchCallbackAsync(Guid operationId, GpuBatchCallback callback, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<GpuDiagnosticTransitionResult> MarkCapacityUncertainAsync(Guid operationId, GpuCapacityUncertaintyRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<GpuTrustedReconciliationResult> ReconcileCapacityAsync(Guid operationId, GpuTrustedCapacityReconciliation request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<GpuTrustedReconciliationResult> ReconcileTaskOutcomeAsync(Guid operationId, GpuTaskOutcomeReconciliation request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<IReadOnlyList<GpuCapacityUncertaintyRequest>> ReadStaleCapacityReservationsAsync(DateTimeOffset heartbeatNotAfterUtc, CancellationToken cancellationToken) => ValueTask.FromResult<IReadOnlyList<GpuCapacityUncertaintyRequest>>([]);
        public ValueTask<GpuSchedulerWakeSnapshot> ReadWakeStateAsync(CancellationToken cancellationToken) => ValueTask.FromResult(_wake);
        public ValueTask<GpuSchedulerWakeConsumption> ConsumeWakeAsync(Guid operationId, long expectedGeneration, CancellationToken cancellationToken)
        {
            if (_wake.Generation != expectedGeneration)
            {
                return ValueTask.FromResult(new GpuSchedulerWakeConsumption(false, _wake));
            }

            var consumed = _wake with { ConsumptionOperationId = operationId };
            _wake = _wake with { Reasons = 0 };
            return ValueTask.FromResult(new GpuSchedulerWakeConsumption(true, consumed));
        }
        public ValueTask<bool> AcknowledgeWakeAsync(Guid operationId, Guid consumptionOperationId, CancellationToken cancellationToken)
        {
            if (consumptionOperationId == Guid.Empty)
            {
                return ValueTask.FromResult(false);
            }

            _wake = _wake with { Reasons = 0 };
            return ValueTask.FromResult(true);
        }
        public ValueTask<GpuSchedulerStatusSnapshot> ReadGpuSchedulerStatusAsync(CancellationToken cancellationToken) => ValueTask.FromResult(GpuSchedulerStatusSnapshot.Empty);
    }

    private sealed class ScriptedStore(
        GpuSchedulerWakeSnapshot wake,
        int failuresBeforeSuccess = 0,
        int consumptionFailuresBeforeSuccess = 0,
        int commitUnknownConsumptionFailuresBeforeSuccess = 0) : IGpuSchedulerStore
    {
        private GpuSchedulerWakeSnapshot _wake = wake;
        private int _remainingFailures = failuresBeforeSuccess;
        private int _remainingConsumptionFailures = consumptionFailuresBeforeSuccess;
        private int _remainingCommitUnknownConsumptionFailures = commitUnknownConsumptionFailuresBeforeSuccess;
        private Guid? _committedConsumptionOperationId;
        private long? _committedConsumptionExpectedGeneration;
        private GpuSchedulerWakeConsumption? _committedConsumptionResult;

        public Channel<GpuSchedulerWakeReason> Admissions { get; } = Channel.CreateUnbounded<GpuSchedulerWakeReason>();
        public Channel<Task<GpuSchedulerWakeConsumption>> ConsumptionOperations { get; } =
            Channel.CreateUnbounded<Task<GpuSchedulerWakeConsumption>>();
        public TaskCompletionSource FirstAttempt { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource FirstConsumptionAttempt { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource CommittedWakeConsumptionThenThrown { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource PreCommitWakeConsumptionThenThrown { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Action? AfterConsumptionCompleted { get; set; }
        public Action<GpuSchedulerWakeReason>? AfterAdmission { get; set; }
        public Func<long, GpuSchedulerWakeConsumption?>? ConsumeOverride { get; set; }
        public List<Guid> AdmissionOperationIds { get; } = [];
        public List<Guid> ConsumptionOperationIds { get; } = [];
        public List<long> ConsumptionExpectedGenerations { get; } = [];
        public List<Guid> UncertaintyOperationIds { get; } = [];
        public List<GpuCapacityUncertaintyRequest> UncertaintyRequests { get; } = [];
        public IReadOnlyList<GpuCapacityUncertaintyRequest> StaleCapacityReservations { get; set; } = [];
        public int AdmissionCount { get; private set; }
        public int ConsumeCount { get; private set; }
        public GpuSchedulerWakeSnapshot CurrentWake => _wake;

        public void SetWake(GpuSchedulerWakeSnapshot wake) => _wake = wake;

        public ValueTask<GpuMiniTaskHandoffResult> GpuTaskHandoffAsync(GpuMiniTaskHandoffRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<GpuSchedulerAdmissionRoundResult> RunAdmissionRoundAsync(Guid operationId, GpuSchedulerWakeReason wakeReason, GpuSchedulerOptions options, Func<GpuBatchCandidate, CancellationToken, ValueTask<GpuAdmissionDecision>> decideAdmission, CancellationToken cancellationToken)
        {
            AdmissionOperationIds.Add(operationId);
            FirstAttempt.TrySetResult();
            if (Interlocked.Decrement(ref _remainingFailures) >= 0)
            {
                throw new InvalidOperationException("test scheduler failure");
            }

            AdmissionCount++;
            Admissions.Writer.TryWrite(wakeReason);
            AfterAdmission?.Invoke(wakeReason);
            return ValueTask.FromResult(new GpuSchedulerAdmissionRoundResult(false, GpuAdmissionDisposition.Busy, null));
        }

        public ValueTask<GpuBatchCallbackResult> ApplyBatchCallbackAsync(Guid operationId, GpuBatchCallback callback, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<GpuDiagnosticTransitionResult> MarkCapacityUncertainAsync(Guid operationId, GpuCapacityUncertaintyRequest request, CancellationToken cancellationToken)
        {
            UncertaintyOperationIds.Add(operationId);
            UncertaintyRequests.Add(request);
            StaleCapacityReservations = StaleCapacityReservations
                .Where(candidate => candidate != request)
                .ToArray();
            return ValueTask.FromResult(new GpuDiagnosticTransitionResult(true));
        }
        public ValueTask<GpuTrustedReconciliationResult> ReconcileCapacityAsync(Guid operationId, GpuTrustedCapacityReconciliation request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<GpuTrustedReconciliationResult> ReconcileTaskOutcomeAsync(Guid operationId, GpuTaskOutcomeReconciliation request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<IReadOnlyList<GpuCapacityUncertaintyRequest>> ReadStaleCapacityReservationsAsync(DateTimeOffset heartbeatNotAfterUtc, CancellationToken cancellationToken) => ValueTask.FromResult(StaleCapacityReservations);
        public ValueTask<GpuSchedulerWakeSnapshot> ReadWakeStateAsync(CancellationToken cancellationToken) => ValueTask.FromResult(_wake);
        public ValueTask<GpuSchedulerWakeConsumption> ConsumeWakeAsync(Guid operationId, long expectedGeneration, CancellationToken cancellationToken)
        {
            ConsumeCount++;
            ConsumptionOperationIds.Add(operationId);
            ConsumptionExpectedGenerations.Add(expectedGeneration);
            FirstConsumptionAttempt.TrySetResult();
            if (_committedConsumptionOperationId is { } committedOperationId)
            {
                if (operationId == committedOperationId &&
                    expectedGeneration == _committedConsumptionExpectedGeneration)
                {
                    return ValueTask.FromResult(_committedConsumptionResult!);
                }
            }

            if (Interlocked.Decrement(ref _remainingCommitUnknownConsumptionFailures) >= 0)
            {
                if (_wake.Generation != expectedGeneration)
                {
                    return ValueTask.FromResult(new GpuSchedulerWakeConsumption(false, _wake));
                }

                _committedConsumptionOperationId = operationId;
                _committedConsumptionExpectedGeneration = expectedGeneration;
                _committedConsumptionResult = new GpuSchedulerWakeConsumption(true, _wake with { ConsumptionOperationId = operationId });
                _wake = _wake with { Reasons = 0 };
                CommittedWakeConsumptionThenThrown.TrySetResult();
                return ValueTask.FromException<GpuSchedulerWakeConsumption>(
                    new InvalidOperationException("test post-commit wake-consumption failure"));
            }

            if (Interlocked.Decrement(ref _remainingConsumptionFailures) >= 0)
            {
                PreCommitWakeConsumptionThenThrown.TrySetResult();
                return ValueTask.FromException<GpuSchedulerWakeConsumption>(
                    new InvalidOperationException("test wake-consumption failure"));
            }

            var completion = new TaskCompletionSource<GpuSchedulerWakeConsumption>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            ConsumptionOperations.Writer.TryWrite(completion.Task);
            var overridden = ConsumeOverride?.Invoke(expectedGeneration);
            if (overridden is not null)
            {
                return CompleteConsumption(completion, overridden);
            }

            if (_wake.Generation != expectedGeneration)
            {
                var mismatch = new GpuSchedulerWakeConsumption(false, _wake);
                return CompleteConsumption(completion, mismatch);
            }

            var consumed = _wake with { ConsumptionOperationId = operationId };
            _wake = _wake with { Reasons = 0 };
            var result = new GpuSchedulerWakeConsumption(true, consumed);
            return CompleteConsumption(completion, result);
        }

        private ValueTask<GpuSchedulerWakeConsumption> CompleteConsumption(
            TaskCompletionSource<GpuSchedulerWakeConsumption> completion,
            GpuSchedulerWakeConsumption result)
        {
            AfterConsumptionCompleted?.Invoke();
            completion.SetResult(result);
            return new ValueTask<GpuSchedulerWakeConsumption>(completion.Task);
        }

        public ValueTask<bool> AcknowledgeWakeAsync(Guid operationId, Guid consumptionOperationId, CancellationToken cancellationToken)
        {
            if (consumptionOperationId == Guid.Empty)
            {
                return ValueTask.FromResult(false);
            }

            _wake = _wake with { Reasons = 0 };
            return ValueTask.FromResult(true);
        }

        public ValueTask<GpuSchedulerStatusSnapshot> ReadGpuSchedulerStatusAsync(CancellationToken cancellationToken) => ValueTask.FromResult(GpuSchedulerStatusSnapshot.Empty);
    }

    private sealed class CancellationAwareWakeSignal : IGpuSchedulerWakeSignal
    {
        public TaskCompletionSource WaitStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource WaitCancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Notify(GpuSchedulerWakeReason reason)
        {
        }

        public async ValueTask<GpuSchedulerWakeReason> WaitAsync(CancellationToken cancellationToken)
        {
            WaitStarted.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                WaitCancelled.TrySetResult();
                throw;
            }

            return 0;
        }
    }

    private sealed class RecordingLogger : ILogger<GpuSchedulerService>
    {
        public int ErrorCount { get; private set; }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Error)
            {
                ErrorCount++;
            }
        }
    }

    private sealed class NullPublisher : IStatusEventPublisher
    {
        public ValueTask PublishAsync(StatusChanged statusChanged, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }

    private sealed class FailOncePublisher : IStatusEventPublisher
    {
        private int _attemptCount;

        public TaskCompletionSource FirstAttempt { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource SecondAttempt { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int AttemptCount => _attemptCount;

        public ValueTask PublishAsync(StatusChanged statusChanged, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _attemptCount) == 1)
            {
                FirstAttempt.TrySetResult();
                return ValueTask.FromException(new InvalidOperationException("test publication failure"));
            }

            SecondAttempt.TrySetResult();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingStatusPublisher : IStatusEventPublisher
    {
        public TaskCompletionSource Published { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask PublishAsync(StatusChanged statusChanged, CancellationToken cancellationToken)
        {
            Published.TrySetResult();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class CountingDeferredGate(TimeSpan retryAfter) : IGpuAdmissionGate
    {
        private int _decisionCount;

        public int DecisionCount => _decisionCount;

        public ValueTask<GpuAdmissionDecision> DecideAsync(
            GpuBatchCandidate candidate,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _decisionCount);
            return ValueTask.FromResult(new GpuAdmissionDecision(
                GpuAdmissionDisposition.Defer,
                null,
                null,
                retryAfter));
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class ManualTimeProvider(
        DateTimeOffset now,
        Func<int> recordSchedule) : TimeProvider
    {
        private readonly object _sync = new();
        private readonly List<ManualTimer> _timers = [];
        private DateTimeOffset _now = now;

        public Channel<(DateTimeOffset DueAtUtc, int ObservationOrder)> ScheduledTimers { get; } =
            Channel.CreateUnbounded<(DateTimeOffset DueAtUtc, int ObservationOrder)>();

        public override DateTimeOffset GetUtcNow()
        {
            lock (_sync)
            {
                return _now;
            }
        }

        public override ITimer CreateTimer(
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period)
        {
            ArgumentNullException.ThrowIfNull(callback);
            if (period != Timeout.InfiniteTimeSpan)
            {
                throw new NotSupportedException("This test clock supports one-shot timers only.");
            }

            lock (_sync)
            {
                var dueAtUtc = dueTime == Timeout.InfiniteTimeSpan
                    ? DateTimeOffset.MaxValue
                    : _now.Add(dueTime);
                var timer = new ManualTimer(callback, state, dueAtUtc);
                _timers.Add(timer);
                if (dueTime != Timeout.InfiniteTimeSpan)
                {
                    ScheduledTimers.Writer.TryWrite((dueAtUtc, recordSchedule()));
                }

                return timer;
            }
        }

        public void AdvanceTo(DateTimeOffset now)
        {
            List<(TimerCallback Callback, object? State)> callbacks = [];
            lock (_sync)
            {
                if (now < _now)
                {
                    throw new ArgumentOutOfRangeException(nameof(now));
                }

                _now = now;
                foreach (var timer in _timers)
                {
                    if (timer.TryTake(now, out var callback, out var state))
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

        private sealed class ManualTimer(
            TimerCallback callback,
            object? state,
            DateTimeOffset dueAtUtc) : ITimer
        {
            private int _completed;

            public bool Change(TimeSpan dueTime, TimeSpan period) =>
                throw new NotSupportedException("This test clock does not reschedule timers.");

            public void Dispose() => Interlocked.Exchange(ref _completed, 1);

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }

            public bool TryTake(
                DateTimeOffset now,
                out TimerCallback takenCallback,
                out object? takenState)
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
}
