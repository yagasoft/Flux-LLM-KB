using FluxKnowledge.Application.Contracts;
using FluxKnowledge.Application.Gpu;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Sources;
using FluxKnowledge.Domain.Gpu;
using FluxKnowledge.Domain.Jobs;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using FluxKnowledge.Infrastructure.SqlServer.Workers;
using FluxKnowledge.Integrations.Files;
using FluxKnowledge.Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using System.Data;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Sources;

public sealed partial class DeletedSourceRecoveryIntegrationTests
{
    [NativeSqlServerFact]
    public async Task Startup_source_admission_wakes_existing_gpu_work_without_another_handoff()
    {
        await using var scenario = await Scenario.CreateAsync(fixture.ConnectionString);
        Guid inputId;
        await using (var db = scenario.Factory.CreateDbContext())
            inputId = await db.SourceRevisions.Where(x => x.CanonicalPath == Path.Combine(scenario.Files, "keeper.txt"))
                .Select(x => x.Id).SingleAsync();
        var jobId = await scenario.QueueAsync(inputId);
        await using (var db = scenario.Factory.CreateDbContext())
        {
            (await db.Jobs.SingleAsync(x => x.Id == jobId)).PublicState = (int)PublicJobState.GpuQueued;
            db.GpuMiniTasks.Add(new GpuMiniTaskEntity
            {
                Id = Guid.NewGuid(), ParentJobId = jobId, SourceRevision = 1,
                PriorityLane = (int)GpuPriorityLane.DocumentIndexing, ModelRuntimeKey = "recovery-fixture",
                SettingsFingerprint = "recovery-fixture-settings", EstimatedBytes = 1,
                IdempotencyKey = "recovery-fixture-task", CreatedAtUtc = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync();
        }
        var admission = new SourceWorkAdmission();
        var store = new SqlGpuSchedulerStore(scenario.Factory, sourceAdmission: admission);
        var signal = new ChannelGpuSchedulerWakeSignal();
        var gate = new RecoveryAdmissionGate();
        var services = new ServiceCollection();
        services.AddSingleton<IGpuSchedulerStore>(store);
        services.AddSingleton<IGpuAdmissionGate>(gate);
        services.AddSingleton<IStatusEventPublisher, RecoveryStatusPublisher>();
        services.AddSingleton<IGpuSchedulerWakeSignal>(signal);
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(new GpuSchedulerOptions(1, 1024, TimeSpan.FromMinutes(1),
            TimeSpan.FromMilliseconds(20), TimeSpan.FromDays(1)));
        services.AddScoped<GpuSchedulerCoordinator>();
        services.AddSingleton<GpuSchedulerService>();
        await using var provider = services.BuildServiceProvider();
        var service = provider.GetRequiredService<GpuSchedulerService>();
        await service.StartAsync(default);
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (true)
            {
                await using var db = scenario.Factory.CreateDbContext();
                if (await db.GpuSchedulerOperationReceipts.AnyAsync(x => x.OperationKind == "admission", deadline.Token)) break;
                await Task.Delay(10, deadline.Token);
            }
            Assert.False(gate.Selected.Task.IsCompleted);
            var before = await store.ReadWakeStateAsync(default);
            var observations = new WindowsSourceObservationReader();
            await new SqlSourceWorkRecovery(scenario.Factory, admission,
                new SqlSourceRemovalReconciler(scenario.Factory, TimeProvider.System, observations),
                observations, TimeProvider.System).AdvanceAsync(default);
            Assert.True(admission.IsAdmitted(scenario.Claim.SourceRoot.Id.Value, scenario.Claim.SourceRoot.ConfigurationRevision));
            var after = await store.ReadWakeStateAsync(default);
            Assert.True(after.Generation > before.Generation, "Opening source admission must durably wake the saved GPU queue.");
            await gate.Selected.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await using var verify = scenario.Factory.CreateDbContext();
            Assert.Equal(1, await verify.GpuMiniTasks.CountAsync());
            Assert.Equal((int)PublicJobState.GpuQueued, (await verify.Jobs.SingleAsync(x => x.Id == jobId)).PublicState);
        }
        finally { await service.StopAsync(default).WaitAsync(TimeSpan.FromSeconds(5)); }
    }

    [NativeSqlServerFact]
    public async Task Failed_admission_wake_retries_in_a_new_scope_and_only_changed_configurations_wake_again()
    {
        await using var scenario = await Scenario.CreateAsync(fixture.ConnectionString);
        var root = scenario.Claim.SourceRoot;
        var admission = new SourceWorkAdmission();
        admission.MarkPrepared(root.Id.Value, root.ConfigurationRevision);
        var progress = new SqlSourceWorkRecoveryProgress();
        var observations = new WindowsSourceObservationReader();
        var removals = new SqlSourceRemovalReconciler(scenario.Factory, TimeProvider.System, observations);
        await using var lookup = scenario.Factory.CreateDbContext();
        var failure = new RecoveryWakeFailure();
        var factory = new RecoveryContextFactory(lookup.Database.GetConnectionString()!, failure);
        var store = new SqlGpuSchedulerStore(scenario.Factory);
        var before = await store.ReadWakeStateAsync(default);
        await Assert.ThrowsAsync<RecoveryWakeWriteException>(() => new SqlSourceWorkRecovery(factory, admission,
            removals, observations, TimeProvider.System, recoveryProgress: progress).AdvanceAsync(default).AsTask());
        Assert.True(admission.IsAdmitted(root.Id.Value, root.ConfigurationRevision));
        Assert.Equal(before.Generation, (await store.ReadWakeStateAsync(default)).Generation);
        // A new scoped recovery object shares acknowledgement state; it must not skip an
        // unannounced root just because the in-memory admission is already open.
        await new SqlSourceWorkRecovery(factory, admission, removals, observations, TimeProvider.System,
            recoveryProgress: progress).AdvanceAsync(default);
        var announced = await store.ReadWakeStateAsync(default);
        Assert.Equal(before.Generation + 1, announced.Generation);
        await new SqlSourceWorkRecovery(factory, admission, removals, observations, TimeProvider.System,
            recoveryProgress: progress).AdvanceAsync(default);
        Assert.Equal(announced.Generation, (await store.ReadWakeStateAsync(default)).Generation);
        await using (var db = scenario.Factory.CreateDbContext())
        {
            (await db.SourceRootConfigurations.SingleAsync()).ConfigurationRevision++;
            await db.SaveChangesAsync();
        }
        admission.MarkPrepared(root.Id.Value, root.ConfigurationRevision + 1);
        await new SqlSourceWorkRecovery(factory, admission, removals, observations, TimeProvider.System,
            recoveryProgress: progress).AdvanceAsync(default);
        Assert.Equal(announced.Generation + 1, (await store.ReadWakeStateAsync(default)).Generation);
    }

    [NativeSqlServerFact]
    public async Task Removal_scope_clearance_and_its_wake_commit_or_roll_back_together()
    {
        await using var scenario = await Scenario.CreateAsync(fixture.ConnectionString);
        var pending = await scenario.PendingAsync("keeper.txt", false);
        var store = new SqlGpuSchedulerStore(scenario.Factory);
        var before = await store.ReadWakeStateAsync(default);
        await using var lookup = scenario.Factory.CreateDbContext();
        var failure = new RecoveryWakeFailure();
        var factory = new RecoveryContextFactory(lookup.Database.GetConnectionString()!, failure);
        var observations = new WindowsSourceObservationReader();
        await Assert.ThrowsAsync<RecoveryWakeWriteException>(() =>
            new SqlSourceRemovalReconciler(factory, TimeProvider.System, observations).ReconcileAsync(default).AsTask());
        await using (var db = scenario.Factory.CreateDbContext())
            Assert.NotEqual(0, (await db.SourcePendingChanges.SingleAsync(x => x.Id == pending)).ReasonFlags & SourcePendingReasons.RemovalPending);
        Assert.Equal(before.Generation, (await store.ReadWakeStateAsync(default)).Generation);
        var reconciler = new SqlSourceRemovalReconciler(scenario.Factory, TimeProvider.System, observations);
        await reconciler.ReconcileAsync(default);
        await using (var db = scenario.Factory.CreateDbContext())
            Assert.Equal(0, (await db.SourcePendingChanges.SingleAsync(x => x.Id == pending)).ReasonFlags & SourcePendingReasons.RemovalPending);
        Assert.Equal(before.Generation + 1, (await store.ReadWakeStateAsync(default)).Generation);
        await reconciler.ReconcileAsync(default);
        Assert.Equal(before.Generation + 1, (await store.ReadWakeStateAsync(default)).Generation);
    }

    [NativeSqlServerFact]
    public async Task Concurrent_wake_consumption_cannot_lose_source_admission_publication()
    {
        await using var scenario = await Scenario.CreateAsync(fixture.ConnectionString);
        var admission = new SourceWorkAdmission();
        var root = scenario.Claim.SourceRoot;
        admission.MarkPrepared(root.Id.Value, root.ConfigurationRevision);
        var store = new SqlGpuSchedulerStore(scenario.Factory);
        var before = await store.ReadWakeStateAsync(default);
        var observations = new WindowsSourceObservationReader();
        await using var lookup = scenario.Factory.CreateDbContext();
        await using var blocker = new FluxKnowledgeDbContext(new DbContextOptionsBuilder<FluxKnowledgeDbContext>()
            .UseSqlServer(lookup.Database.GetConnectionString()).Options);
        await using var transaction = await blocker.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        await SqlGpuSchedulerStore.AcquireMutationLockAsync(blocker, transaction.GetDbTransaction(), default);
        var consumption = store.ConsumeWakeAsync(Guid.NewGuid(), before.Generation, default).AsTask();
        var publication = new SqlSourceWorkRecovery(scenario.Factory, admission,
            new SqlSourceRemovalReconciler(scenario.Factory, TimeProvider.System, observations),
            observations, TimeProvider.System).AdvanceAsync(default).AsTask();
        await Task.Delay(100);
        Assert.False(consumption.IsCompleted);
        Assert.False(publication.IsCompleted);
        await transaction.CommitAsync();
        await Task.WhenAll(consumption, publication).WaitAsync(TimeSpan.FromSeconds(10));
        if (consumption.Result.Snapshot.ConsumptionOperationId is { } operation)
            await store.AcknowledgeWakeAsync(Guid.NewGuid(), operation, default);
        var after = await store.ReadWakeStateAsync(default);
        Assert.Equal(before.Generation + 1, after.Generation);
        Assert.True(after.Reasons.HasFlag(GpuSchedulerWakeReason.Reconciliation));
    }

    private sealed class RecoveryContextFactory(string connection, RecoveryWakeFailure failure)
        : IDbContextFactory<FluxKnowledgeDbContext>
    {
        public FluxKnowledgeDbContext CreateDbContext() => new(new DbContextOptionsBuilder<FluxKnowledgeDbContext>()
            .UseSqlServer(connection).AddInterceptors(failure).Options);
    }

    private sealed class RecoveryWakeFailure : SaveChangesInterceptor
    {
        private int _failures;
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (eventData.Context!.ChangeTracker.Entries<GpuSchedulerStateEntity>().Any(x => x.State == EntityState.Modified) &&
                Interlocked.Increment(ref _failures) == 1) throw new RecoveryWakeWriteException();
            return ValueTask.FromResult(result);
        }
    }

    private sealed class RecoveryWakeWriteException : Exception;

    private sealed class RecoveryAdmissionGate : IGpuAdmissionGate
    {
        internal TaskCompletionSource Selected { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ValueTask<GpuAdmissionDecision> DecideAsync(GpuBatchCandidate candidate, CancellationToken cancellationToken)
        {
            Selected.TrySetResult();
            return ValueTask.FromResult(new GpuAdmissionDecision(GpuAdmissionDisposition.Busy, null, null, null));
        }
    }

    private sealed class RecoveryStatusPublisher : IStatusEventPublisher
    {
        public ValueTask PublishAsync(StatusChanged statusChanged, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }
}
