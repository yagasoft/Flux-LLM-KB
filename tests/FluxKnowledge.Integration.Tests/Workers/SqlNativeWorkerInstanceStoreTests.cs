using FluxKnowledge.Application.Gpu;
using FluxKnowledge.Domain.Gpu;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using FluxKnowledge.Integration.Tests.Gpu;
using FluxKnowledge.Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Workers;

public sealed class SqlNativeWorkerInstanceStoreTests(NativeSqlServerFixture fixture) : IClassFixture<NativeSqlServerFixture>
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-08-10T12:00:00+00:00");
    private const string ExecutableFingerprint = "6f4df0bb6b3536e0f503cf0a7e7ae2864a126c40f43fa43ed0de42d0d3d6fb4c";
    private readonly NativeSqlServerFixture _fixture = fixture;

    [NativeSqlServerFact]
    public async Task Create_persists_an_attested_private_instance_and_sanitised_launch_evidence()
    {
        await SqlTestData.ClearPipelineAsync(_fixture);
        var factory = SqlTestData.CreateFactory(_fixture);
        var instance = CreateInstance();
        var store = new SqlNativeWorkerInstanceStore(factory, new FixedTimeProvider(Now));

        var result = await store.CreateAsync(Guid.NewGuid(), Launch(instance), CancellationToken.None);

        Assert.True(result.Accepted);
        Assert.True(result.Committed);
        Assert.False(result.IsIdempotentReplay);
        await using var verify = await factory.CreateDbContextAsync();
        var persisted = await verify.NativeWorkerInstances.SingleAsync();
        Assert.Equal(instance.InstanceId, persisted.InstanceId);
        Assert.Equal(instance.ExecutorKey, persisted.ExecutorKey);
        Assert.Null(persisted.ProcessId);
        Assert.Null(persisted.ProcessStartedAtUtc);
        Assert.Equal(ExecutableFingerprint, persisted.ExecutableFingerprint);
        Assert.Equal(instance.ProtocolVersion, persisted.ProtocolVersion);
        Assert.Equal((int)NativeWorkerLifecycleClass.LaunchRequested, persisted.State);
        Assert.Equal(Now, persisted.LaunchedAtUtc);
        Assert.Null(persisted.ActiveDispatchId);
        var evidence = Assert.Single(await verify.NativeWorkerLifecycleEvidence.ToListAsync());
        Assert.Equal((int)NativeWorkerLifecycleClass.LaunchRequested, evidence.LifecycleClass);
        Assert.Equal(instance.InstanceId, evidence.InstanceId);
        Assert.Equal(Now, evidence.ObservedAtUtc);
        Assert.Equal(64, evidence.RequestFingerprint.Length);
        var audit = Assert.Single(await verify.AuditEvents.ToListAsync());
        Assert.Equal("native_worker.launch_requested", audit.EventType);
        Assert.DoesNotContain(instance.ProcessId.ToString(), audit.DetailsJson, StringComparison.Ordinal);
        Assert.DoesNotContain(ExecutableFingerprint, audit.DetailsJson, StringComparison.Ordinal);
    }

    [NativeSqlServerFact]
    public async Task Heartbeat_operation_replays_identically_and_rejects_a_divergent_request()
    {
        await SqlTestData.ClearPipelineAsync(_fixture);
        var factory = SqlTestData.CreateFactory(_fixture);
        var instance = CreateInstance();
        var store = new SqlNativeWorkerInstanceStore(factory, new FixedTimeProvider(Now));
        Assert.True((await store.CreateAsync(Guid.NewGuid(), Launch(instance), CancellationToken.None)).Accepted);
        var operationId = Guid.NewGuid();
        var observedAtUtc = Now.AddMinutes(1);

        var first = await store.RecordHeartbeatAsync(operationId, instance.InstanceId, observedAtUtc, CancellationToken.None);
        var replay = await store.RecordHeartbeatAsync(operationId, instance.InstanceId, observedAtUtc, CancellationToken.None);
        var divergent = () => store.RecordHeartbeatAsync(operationId, instance.InstanceId, observedAtUtc.AddSeconds(1), CancellationToken.None).AsTask();

        Assert.True(first.Accepted);
        Assert.True(replay.IsIdempotentReplay);
        await Assert.ThrowsAsync<InvalidOperationException>(divergent);
        await using var verify = await factory.CreateDbContextAsync();
        Assert.Equal(observedAtUtc, await verify.NativeWorkerInstances.Select(value => value.LastHeartbeatAtUtc).SingleAsync());
        Assert.Single(await verify.NativeWorkerLifecycleEvidence.Where(value => value.OperationId == operationId).ToListAsync());
        Assert.Single(await verify.AuditEvents.Where(value => value.EventType == "native_worker.heartbeat_observed").ToListAsync());
    }

    [NativeSqlServerFact]
    public async Task Failed_instance_mutation_rolls_back_instance_evidence_and_audit_together()
    {
        await SqlTestData.ClearPipelineAsync(_fixture);
        var factory = new InterceptingDbContextFactory(_fixture.ConnectionString, new ThrowOnSavingChangesInterceptor());
        var store = new SqlNativeWorkerInstanceStore(factory, new FixedTimeProvider(Now));

        await Assert.ThrowsAsync<InjectedSaveFailure>(async () =>
            await store.CreateAsync(Guid.NewGuid(), Launch(CreateInstance()), CancellationToken.None));

        await using var verify = await SqlTestData.CreateFactory(_fixture).CreateDbContextAsync();
        Assert.Empty(await verify.NativeWorkerInstances.ToListAsync());
        Assert.Empty(await verify.NativeWorkerLifecycleEvidence.ToListAsync());
        Assert.Empty(await verify.AuditEvents.ToListAsync());
    }

    [NativeSqlServerFact]
    public async Task Concurrent_distinct_exit_reconciliations_fence_to_one_transition_evidence_and_audit_append()
    {
        await SqlTestData.ClearPipelineAsync(_fixture);
        var factory = SqlTestData.CreateFactory(_fixture);
        var instance = CreateInstance();
        var store = new SqlNativeWorkerInstanceStore(factory, new FixedTimeProvider(Now));
        Assert.True((await store.CreateAsync(Guid.NewGuid(), Launch(instance), CancellationToken.None)).Accepted);

        var results = await Task.WhenAll(
            store.RecordExitAsync(Guid.NewGuid(), instance.InstanceId, Now.AddMinutes(2), 1, CancellationToken.None).AsTask(),
            store.RecordExitAsync(Guid.NewGuid(), instance.InstanceId, Now.AddMinutes(2), 1, CancellationToken.None).AsTask());

        Assert.Single(results, result => result.Accepted);
        Assert.Single(results, result => !result.Accepted || result.IsIdempotentReplay);
        await using var verify = await factory.CreateDbContextAsync();
        Assert.Equal((int)NativeWorkerLifecycleClass.Exited, await verify.NativeWorkerInstances.Select(value => value.State).SingleAsync());
        Assert.Single(await verify.NativeWorkerLifecycleEvidence.Where(value => value.LifecycleClass == (int)NativeWorkerLifecycleClass.Exited).ToListAsync());
        Assert.Single(await verify.AuditEvents.Where(value => value.EventType == "native_worker.exited").ToListAsync());
    }

    [NativeSqlServerFact]
    public async Task Connection_attests_post_launch_process_and_replays_when_the_clock_advances()
    {
        await SqlTestData.ClearPipelineAsync(_fixture);
        var factory = SqlTestData.CreateFactory(_fixture);
        var instance = CreateInstance();
        var store = new SqlNativeWorkerInstanceStore(factory, new FixedTimeProvider(Now));
        Assert.True((await store.CreateAsync(Guid.NewGuid(), Launch(instance), CancellationToken.None)).Accepted);
        var operationId = Guid.NewGuid();
        var first = await store.RecordConnectionAsync(operationId, new NativeWorkerConnectionAttestation(instance, ExecutableFingerprint), CancellationToken.None);
        var replay = await new SqlNativeWorkerInstanceStore(factory, new FixedTimeProvider(Now.AddHours(1)))
            .RecordConnectionAsync(operationId, new NativeWorkerConnectionAttestation(instance, ExecutableFingerprint), CancellationToken.None);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.RecordConnectionAsync(operationId, new NativeWorkerConnectionAttestation(instance, new string('a', 64)), CancellationToken.None).AsTask());
        Assert.True(first.Accepted); Assert.True(replay.IsIdempotentReplay);
        await using var verify = await factory.CreateDbContextAsync();
        var persisted = await verify.NativeWorkerInstances.SingleAsync();
        Assert.Equal(instance.ProcessId, persisted.ProcessId); Assert.Equal(instance.ProcessStartedAtUtc, persisted.ProcessStartedAtUtc);
    }

    [NativeSqlServerFact]
    public async Task Recovery_candidates_return_private_attestation_and_exact_active_handle_but_exclude_terminal_rows()
    {
        await SqlTestData.ClearPipelineAsync(_fixture);
        var factory = SqlTestData.CreateFactory(_fixture);
        var instance = CreateInstance();
        var store = new SqlNativeWorkerInstanceStore(factory, new FixedTimeProvider(Now));
        Assert.True((await store.CreateAsync(Guid.NewGuid(), Launch(instance), CancellationToken.None)).Accepted);
        Assert.True((await store.RecordConnectionAsync(Guid.NewGuid(), new NativeWorkerConnectionAttestation(instance, ExecutableFingerprint), CancellationToken.None)).Accepted);

        var candidate = Assert.Single(await store.ReadRecoveryCandidatesAsync(instance.ExecutorKey, CancellationToken.None));
        Assert.Equal(instance.InstanceId, candidate.InstanceId);
        Assert.Equal(instance, candidate.AttestedInstance);
        Assert.Null(candidate.ActiveHandle);

        Assert.True((await store.RecordExitAsync(Guid.NewGuid(), instance.InstanceId, Now.AddMinutes(1), 0, CancellationToken.None)).Accepted);
        Assert.Empty(await store.ReadRecoveryCandidatesAsync(instance.ExecutorKey, CancellationToken.None));
    }

    [NativeSqlServerFact]
    public async Task Exact_active_dispatch_binding_requires_attested_instance_and_unique_dispatch()
    {
        await SqlTestData.ClearPipelineAsync(_fixture);
        var factory = SqlTestData.CreateFactory(_fixture);
        var instance = CreateInstance();
        var store = new SqlNativeWorkerInstanceStore(factory, new FixedTimeProvider(Now));
        Assert.True((await store.CreateAsync(Guid.NewGuid(), Launch(instance), CancellationToken.None)).Accepted);
        Assert.True((await store.RecordConnectionAsync(Guid.NewGuid(), new NativeWorkerConnectionAttestation(instance, ExecutableFingerprint), CancellationToken.None)).Accepted);
        var handle = new GpuExecutorBatchHandle(Guid.NewGuid(), "slot", instance.ExecutorKey, 1, Guid.NewGuid());
        var rejected = await store.BindExactActiveDispatchAsync(Guid.NewGuid(), instance with { ProcessId = 43 }, handle, Now.AddMinutes(1), CancellationToken.None);
        Assert.False(rejected.Accepted);
    }

    [NativeSqlServerFact]
    public async Task Uncertainty_retries_scheduler_replay_after_worker_evidence_failure_without_duplicate_worker_transition()
    {
        var admission = new SqlGpuAdmissionTests(_fixture);
        var factory = await admission.CreateEnvironmentAsync();
        await admission.AddReadyAsync(factory, FluxKnowledge.Domain.Gpu.GpuPriorityLane.DocumentIndexing, "runtime", "settings", 1);
        await SqlGpuAdmissionTests.AdmitAsync(factory, SqlGpuAdmissionTests.Admit("slot-a"));
        await using var read = await factory.CreateDbContextAsync();
        var dispatch = await read.GpuExecutorDispatches.SingleAsync();
        var handle = new GpuExecutorBatchHandle(dispatch.BatchId, dispatch.CapacitySlotKey, dispatch.ExecutorKey, dispatch.AdmissionGeneration, dispatch.DispatchId);
        await new SqlGpuSchedulerStore(factory).AcknowledgeAsync(new GpuExecutorAcknowledgement(Guid.NewGuid(), handle), CancellationToken.None);
        var instance = NativeWorkerInstanceHandle.Create(Guid.NewGuid(), handle.ExecutorKey, 42, Now, NativeWorkerProtocol.SupportedVersion);
        var scheduler = new ReplayableUncertaintyStore();
        var setup = new SqlNativeWorkerInstanceStore(factory, new FixedTimeProvider(Now), scheduler);
        Assert.True((await setup.CreateAsync(Guid.NewGuid(), Launch(instance), CancellationToken.None)).Accepted);
        Assert.True((await setup.RecordConnectionAsync(Guid.NewGuid(), new NativeWorkerConnectionAttestation(instance, ExecutableFingerprint), CancellationToken.None)).Accepted);
        Assert.True((await setup.BindExactActiveDispatchAsync(Guid.NewGuid(), instance, handle, Now.AddMinutes(1), CancellationToken.None)).Accepted);
        var operationId = Guid.NewGuid();
        var failing = new SqlNativeWorkerInstanceStore(factory, new FixedTimeProvider(Now), scheduler, _ => ValueTask.FromException(new InjectedSaveFailure()));
        await Assert.ThrowsAsync<InjectedSaveFailure>(() => failing.MarkExactHandleUncertainAsync(operationId, instance, handle, Now.AddMinutes(2), CancellationToken.None).AsTask());
        await using (var beforeRetry = await factory.CreateDbContextAsync())
        {
            Assert.Empty(await beforeRetry.NativeWorkerLifecycleEvidence.Where(value => value.OperationId == operationId).ToListAsync());
            Assert.Equal(handle.DispatchId, await beforeRetry.NativeWorkerInstances.Select(value => value.ActiveDispatchId).SingleAsync());
        }
        var replay = await new SqlNativeWorkerInstanceStore(factory, new FixedTimeProvider(Now), scheduler)
            .MarkExactHandleUncertainAsync(operationId, instance, handle, Now.AddMinutes(2), CancellationToken.None);
        Assert.True(replay.Accepted);
        Assert.Equal(2, scheduler.Calls);
        Assert.Equal(1, scheduler.Mutations);
        await using var verify = await factory.CreateDbContextAsync();
        Assert.Single(await verify.NativeWorkerLifecycleEvidence.Where(value => value.OperationId == operationId).ToListAsync());
        Assert.Null(await verify.NativeWorkerInstances.Select(value => value.ActiveDispatchId).SingleAsync());
        Assert.Equal((int)GpuExecutorDispatchState.Acknowledged, await verify.GpuExecutorDispatches.Select(value => value.State).SingleAsync());
    }

    [NativeSqlServerFact]
    public async Task Real_sql_clear_and_rebind_cannot_interleave_with_fenced_uncertainty()
    {
        var (factory, instance, handle) = await CreateBoundAcknowledgedDispatchAsync();
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var uncertain = new SqlNativeWorkerInstanceStore(factory, new FixedTimeProvider(Now), null, async _ => { reached.SetResult(); await release.Task; });
        var mark = uncertain.MarkExactHandleUncertainAsync(Guid.NewGuid(), instance, handle, Now.AddMinutes(2), CancellationToken.None).AsTask();
        await reached.Task;
        var contender = new SqlNativeWorkerInstanceStore(factory, new FixedTimeProvider(Now));
        var clear = contender.ClearExactActiveDispatchAsync(Guid.NewGuid(), instance, handle, Now.AddMinutes(3), CancellationToken.None).AsTask();
        var rebind = contender.BindExactActiveDispatchAsync(Guid.NewGuid(), instance, handle, Now.AddMinutes(3), CancellationToken.None).AsTask();
        release.SetResult();
        Assert.True((await mark).Accepted);
        Assert.False((await clear).Accepted);
        Assert.False((await rebind).Accepted);
        await using var verify = await factory.CreateDbContextAsync();
        Assert.Equal((int)GpuExecutorDispatchState.DeliveryUncertain, await verify.GpuExecutorDispatches.Select(x => x.State).SingleAsync());
        Assert.Single(await verify.NativeWorkerLifecycleEvidence.Where(x => x.LifecycleClass == (int)NativeWorkerLifecycleClass.Lost).ToListAsync());
        Assert.Null(await verify.NativeWorkerInstances.Select(x => x.ActiveDispatchId).SingleAsync());
    }

    [NativeSqlServerFact]
    public async Task Bind_rejects_non_live_lifecycle_states_and_accepts_connected_instance()
    {
        var (factory, instance, handle) = await CreateBoundAcknowledgedDispatchAsync(bind: false);
        var store = new SqlNativeWorkerInstanceStore(factory, new FixedTimeProvider(Now));
        foreach (var state in new[] { NativeWorkerLifecycleClass.Exited, NativeWorkerLifecycleClass.Lost, NativeWorkerLifecycleClass.TerminationConfirmed, NativeWorkerLifecycleClass.Unresponsive })
        {
            await using (var change = await factory.CreateDbContextAsync()) { var row = await change.NativeWorkerInstances.SingleAsync(); row.State = (int)state; await change.SaveChangesAsync(); }
            Assert.False((await store.BindExactActiveDispatchAsync(Guid.NewGuid(), instance, handle, Now.AddMinutes(4), CancellationToken.None)).Accepted);
        }
        await using (var restore = await factory.CreateDbContextAsync()) { var row = await restore.NativeWorkerInstances.SingleAsync(); row.State = (int)NativeWorkerLifecycleClass.Connected; await restore.SaveChangesAsync(); }
        Assert.True((await store.BindExactActiveDispatchAsync(Guid.NewGuid(), instance, handle, Now.AddMinutes(5), CancellationToken.None)).Accepted);
    }

    [NativeSqlServerFact]
    public async Task Bind_and_clear_use_private_association_idempotency_without_lifecycle_evidence_or_instance_state_changes()
    {
        var (factory, instance, handle) = await CreateBoundAcknowledgedDispatchAsync(bind: false);
        var store = new SqlNativeWorkerInstanceStore(factory, new FixedTimeProvider(Now));
        await using (var before = await factory.CreateDbContextAsync())
        {
            Assert.Equal((int)NativeWorkerLifecycleClass.Connected, await before.NativeWorkerInstances.Select(row => row.State).SingleAsync());
            Assert.Equal(2, await before.NativeWorkerLifecycleEvidence.CountAsync());
            Assert.Equal(2, await before.AuditEvents.CountAsync());
        }

        var bindOperationId = Guid.NewGuid();
        var bound = await store.BindExactActiveDispatchAsync(bindOperationId, instance, handle, Now.AddMinutes(1), CancellationToken.None);
        var bindReplay = await store.BindExactActiveDispatchAsync(bindOperationId, instance, handle, Now.AddMinutes(1), CancellationToken.None);
        var divergentBind = () => store.BindExactActiveDispatchAsync(bindOperationId, instance, handle with { DispatchId = Guid.NewGuid() }, Now.AddMinutes(1), CancellationToken.None).AsTask();
        var clearOperationId = Guid.NewGuid();
        var cleared = await store.ClearExactActiveDispatchAsync(clearOperationId, instance, handle, Now.AddMinutes(2), CancellationToken.None);
        var clearReplay = await store.ClearExactActiveDispatchAsync(clearOperationId, instance, handle, Now.AddMinutes(2), CancellationToken.None);

        Assert.True(bound.Accepted);
        Assert.True(bindReplay.IsIdempotentReplay);
        await Assert.ThrowsAsync<InvalidOperationException>(divergentBind);
        Assert.True(cleared.Accepted);
        Assert.True(clearReplay.IsIdempotentReplay);
        await using var verify = await factory.CreateDbContextAsync();
        Assert.Equal((int)NativeWorkerLifecycleClass.Connected, await verify.NativeWorkerInstances.Select(row => row.State).SingleAsync());
        Assert.Null(await verify.NativeWorkerInstances.Select(row => row.ActiveDispatchId).SingleAsync());
        Assert.Equal(2, await verify.NativeWorkerLifecycleEvidence.CountAsync());
        Assert.Equal(2, await verify.AuditEvents.CountAsync());
    }

    [NativeSqlServerFact]
    public async Task Association_operation_history_replays_bind_a_after_later_bind_and_clear_operations()
    {
        var admission = new SqlGpuAdmissionTests(_fixture);
        var factory = await admission.CreateEnvironmentAsync();
        await admission.AddReadyAsync(factory, GpuPriorityLane.DocumentIndexing, "runtime-a", "settings", 1);
        await SqlGpuAdmissionTests.AdmitAsync(factory, SqlGpuAdmissionTests.Admit("slot-a"));
        await using var firstRead = await factory.CreateDbContextAsync();
        var firstDispatch = await firstRead.GpuExecutorDispatches.SingleAsync();
        var handleA = new GpuExecutorBatchHandle(firstDispatch.BatchId, firstDispatch.CapacitySlotKey, firstDispatch.ExecutorKey, firstDispatch.AdmissionGeneration, firstDispatch.DispatchId);
        await new SqlGpuSchedulerStore(factory).AcknowledgeAsync(new GpuExecutorAcknowledgement(Guid.NewGuid(), handleA), CancellationToken.None);

        await using (var addCapacity = await factory.CreateDbContextAsync())
        {
            addCapacity.GpuCapacitySlots.Add(new GpuCapacitySlotEntity { SlotKey = "slot-b", State = (int)GpuCapacitySlotState.Available, UpdatedAtUtc = Now });
            await addCapacity.SaveChangesAsync();
        }

        await admission.AddReadyAsync(factory, GpuPriorityLane.DocumentIndexing, "runtime-b", "settings", 1);
        await SqlGpuAdmissionTests.AdmitAsync(factory, SqlGpuAdmissionTests.Admit("slot-b"));
        await using var secondRead = await factory.CreateDbContextAsync();
        var secondDispatch = await secondRead.GpuExecutorDispatches.SingleAsync(dispatch => dispatch.DispatchId != handleA.DispatchId);
        var handleB = new GpuExecutorBatchHandle(secondDispatch.BatchId, secondDispatch.CapacitySlotKey, secondDispatch.ExecutorKey, secondDispatch.AdmissionGeneration, secondDispatch.DispatchId);
        await new SqlGpuSchedulerStore(factory).AcknowledgeAsync(new GpuExecutorAcknowledgement(Guid.NewGuid(), handleB), CancellationToken.None);

        var instance = NativeWorkerInstanceHandle.Create(Guid.NewGuid(), handleA.ExecutorKey, 42, Now, NativeWorkerProtocol.SupportedVersion);
        var store = new SqlNativeWorkerInstanceStore(factory, new FixedTimeProvider(Now));
        Assert.True((await store.CreateAsync(Guid.NewGuid(), Launch(instance), CancellationToken.None)).Accepted);
        Assert.True((await store.RecordConnectionAsync(Guid.NewGuid(), new NativeWorkerConnectionAttestation(instance, ExecutableFingerprint), CancellationToken.None)).Accepted);
        var bindA = Guid.NewGuid();
        Assert.True((await store.BindExactActiveDispatchAsync(bindA, instance, handleA, Now.AddMinutes(1), CancellationToken.None)).Accepted);
        Assert.True((await store.ClearExactActiveDispatchAsync(Guid.NewGuid(), instance, handleA, Now.AddMinutes(2), CancellationToken.None)).Accepted);
        Assert.True((await store.BindExactActiveDispatchAsync(Guid.NewGuid(), instance, handleB, Now.AddMinutes(3), CancellationToken.None)).Accepted);
        Assert.True((await store.ClearExactActiveDispatchAsync(Guid.NewGuid(), instance, handleB, Now.AddMinutes(4), CancellationToken.None)).Accepted);

        var replayA = await store.BindExactActiveDispatchAsync(bindA, instance, handleA, Now.AddMinutes(1), CancellationToken.None);

        Assert.True(replayA.Accepted);
        Assert.True(replayA.IsIdempotentReplay);
        await using var verify = await factory.CreateDbContextAsync();
        Assert.Equal((int)NativeWorkerLifecycleClass.Connected, await verify.NativeWorkerInstances.Select(row => row.State).SingleAsync());
        Assert.Null(await verify.NativeWorkerInstances.Select(row => row.ActiveDispatchId).SingleAsync());
        Assert.Equal(2, await verify.NativeWorkerLifecycleEvidence.CountAsync());
        Assert.Equal(2, await verify.AuditEvents.CountAsync());
    }

    private async Task<(IDbContextFactory<FluxKnowledgeDbContext> Factory, NativeWorkerInstanceHandle Instance, GpuExecutorBatchHandle Handle)> CreateBoundAcknowledgedDispatchAsync(bool bind = true)
    {
        var admission = new SqlGpuAdmissionTests(_fixture); var factory = await admission.CreateEnvironmentAsync();
        await admission.AddReadyAsync(factory, FluxKnowledge.Domain.Gpu.GpuPriorityLane.DocumentIndexing, "runtime", "settings", 1);
        await SqlGpuAdmissionTests.AdmitAsync(factory, SqlGpuAdmissionTests.Admit("slot-a"));
        await using var read = await factory.CreateDbContextAsync(); var dispatch = await read.GpuExecutorDispatches.SingleAsync();
        var handle = new GpuExecutorBatchHandle(dispatch.BatchId, dispatch.CapacitySlotKey, dispatch.ExecutorKey, dispatch.AdmissionGeneration, dispatch.DispatchId);
        await new SqlGpuSchedulerStore(factory).AcknowledgeAsync(new GpuExecutorAcknowledgement(Guid.NewGuid(), handle), CancellationToken.None);
        var instance = NativeWorkerInstanceHandle.Create(Guid.NewGuid(), handle.ExecutorKey, 42, Now, NativeWorkerProtocol.SupportedVersion);
        var store = new SqlNativeWorkerInstanceStore(factory, new FixedTimeProvider(Now));
        Assert.True((await store.CreateAsync(Guid.NewGuid(), Launch(instance), CancellationToken.None)).Accepted);
        Assert.True((await store.RecordConnectionAsync(Guid.NewGuid(), new NativeWorkerConnectionAttestation(instance, ExecutableFingerprint), CancellationToken.None)).Accepted);
        if (bind) Assert.True((await store.BindExactActiveDispatchAsync(Guid.NewGuid(), instance, handle, Now.AddMinutes(1), CancellationToken.None)).Accepted);
        return (factory, instance, handle);
    }

    private static NativeWorkerInstanceHandle CreateInstance() => NativeWorkerInstanceHandle.Create(
        Guid.NewGuid(), "native-executor", 42, Now, NativeWorkerProtocol.SupportedVersion);
    private static NativeWorkerLaunchRequest Launch(NativeWorkerInstanceHandle instance) => new(instance.InstanceId, instance.ExecutorKey, ExecutableFingerprint, instance.ProtocolVersion);

    private sealed class InterceptingDbContextFactory(string connectionString, IInterceptor interceptor) : IDbContextFactory<FluxKnowledgeDbContext>
    {
        private readonly DbContextOptions<FluxKnowledgeDbContext> _options = new DbContextOptionsBuilder<FluxKnowledgeDbContext>()
            .UseSqlServer(connectionString)
            .AddInterceptors(interceptor)
            .Options;

        public FluxKnowledgeDbContext CreateDbContext() => new(_options);
    }

    private sealed class ThrowOnSavingChangesInterceptor : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromException<InterceptionResult<int>>(new InjectedSaveFailure());
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class InjectedSaveFailure : Exception;

    private sealed class ReplayableUncertaintyStore : IGpuExecutorDispatchStore
    {
        public int Calls { get; private set; }
        public int Mutations { get; private set; }
        public ValueTask<IReadOnlyList<GpuExecutorBatchHandle>> ReadPendingDispatchesAsync(CancellationToken cancellationToken) => ValueTask.FromResult<IReadOnlyList<GpuExecutorBatchHandle>>([]);
        public ValueTask<GpuExecutorDispatchMutationResult> AcknowledgeAsync(GpuExecutorAcknowledgement acknowledgement, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<GpuExecutorDispatchMutationResult> RecordReceiptAsync(GpuExecutorResultReceipt receipt, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<GpuExecutorDispatchMutationResult> RecordTrustedEvidenceAsync(GpuExecutorTrustedEvidence evidence, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<GpuExecutorDispatchMutationResult> MarkDeliveryUncertainAsync(GpuExecutorDeliveryUncertainty uncertainty, CancellationToken cancellationToken)
        {
            Calls++;
            if (Calls == 1) { Mutations++; return ValueTask.FromResult(new GpuExecutorDispatchMutationResult(true, true)); }
            return ValueTask.FromResult(new GpuExecutorDispatchMutationResult(true, true));
        }
    }
}
