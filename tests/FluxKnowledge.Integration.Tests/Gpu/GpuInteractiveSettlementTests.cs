using FluxKnowledge.Application.Gpu;
using FluxKnowledge.Domain.Gpu;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using FluxKnowledge.Infrastructure.SqlServer.Workers;
using FluxKnowledge.Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Xunit;
using System.Data.Common;
using System.Diagnostics;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;

namespace FluxKnowledge.Integration.Tests.Gpu;

[Collection("sql-hosted-recovery")]
public sealed partial class GpuInteractiveSettlementTests : IAsyncLifetime
{
    private readonly NativeSqlServerFixture _sql = new();
    public Task InitializeAsync() => _sql.InitializeAsync();
    public Task DisposeAsync() => _sql.DisposeAsync();

    [NativeSqlServerFact]
    public async Task Explicit_four_second_queue_wait_survives_one_index_quantum_without_extending_total_deadline()
    {
        var factory=SqlTestData.CreateFactory(_sql);
        var store=new SqlGpuSchedulerStore(factory,interactiveExecutionTimeout:TimeSpan.FromSeconds(20),interactiveQueueTimeout:TimeSpan.FromSeconds(4));
        await AddSlot(factory);using var caller=new CancellationTokenSource();
        var executor=new GpuInteractiveExecutor(store,new Lifecycle(store),store,new ChannelGpuSchedulerWakeSignal(),TimeProvider.System,
            "joined-profile","joined-settings",10,executionTimeout:TimeSpan.FromSeconds(20));
        var operation=executor.StartOwned<int>(_=>ValueTask.FromResult(new GpuInteractiveNativeResult<int>(42,true)),caller.Token,TimeSpan.FromSeconds(4));
        try
        {
            await WaitForRequest(factory,operation.RequestId);
            await using(var db=await factory.CreateDbContextAsync())
            {
                var row=await db.GpuMiniTasks.SingleAsync(t=>t.Id==operation.RequestId);
                Assert.Equal(TimeSpan.FromSeconds(16),row.ExecutionDeadlineUtc-row.QueueDeadlineUtc);
            }
            await Task.Delay(2200);Assert.False(operation.Result.IsCompleted);
            var handle=await Admit(store);await executor.DeliverAsync(handle,default);
            Assert.Equal(42,await operation.Result);Assert.Equal(GpuInteractiveSettlementKind.Released,(await operation.Settlement).Kind);
            Assert.Throws<ArgumentOutOfRangeException>(()=>executor.StartOwned<int>(_=>ValueTask.FromResult(new GpuInteractiveNativeResult<int>(1,true)),default,TimeSpan.FromSeconds(5)));
            var legacyStore=new SqlGpuSchedulerStore(factory,interactiveExecutionTimeout:TimeSpan.FromSeconds(20));
            var instance=Guid.NewGuid();var now=DateTimeOffset.UtcNow;
            await Assert.ThrowsAsync<ArgumentException>(()=>legacyStore.HandoffInteractiveAsync(new(Guid.NewGuid(),instance,$"retrieval-gpu:{instance:N}",
                "joined-profile","joined-settings",10,now.AddSeconds(4),now.AddSeconds(20)),default).AsTask());
        }
        finally{caller.Cancel();await operation.Settlement.WaitAsync(TimeSpan.FromSeconds(8));}
    }

    [NativeSqlServerFact]
    public async Task Observation_cancellation_interrupts_retry_backoff()
    {
        using var cancel=new CancellationTokenSource();
        var factory=new BackoffFactory(_sql.ConnectionString,new ObservationFault(cancel));
        var store=new SqlGpuSchedulerStore(factory);var elapsed=Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>store.ReadInteractiveRequestAsync(Guid.NewGuid(),Guid.NewGuid(),cancel.Token).AsTask());
        Assert.True(elapsed.Elapsed<TimeSpan.FromSeconds(1),$"Observation cancellation waited through retry backoff: {elapsed.Elapsed}.");
    }
    private sealed class ObservationReadFault:Exception;
    private sealed class ObservationFault(CancellationTokenSource cancellation):DbCommandInterceptor
    {
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,CommandEventData eventData,
            InterceptionResult<DbDataReader> result,CancellationToken cancellationToken=default)
        {
            if(command.CommandText.Contains("[GpuMiniTasks]",StringComparison.Ordinal))
            {cancellation.CancelAfter(TimeSpan.FromMilliseconds(100));throw new ObservationReadFault();}
            return base.ReaderExecutingAsync(command,eventData,result,cancellationToken);
        }
    }
    private sealed class ObservationRetry(ExecutionStrategyDependencies dependencies):ExecutionStrategy(dependencies,2,TimeSpan.FromSeconds(2))
    {
        protected override bool ShouldRetryOn(Exception exception)=>exception is ObservationReadFault;
        protected override TimeSpan? GetNextDelay(Exception lastException)=>TimeSpan.FromSeconds(2);
    }
    private sealed class BackoffFactory(string connection,DbCommandInterceptor interceptor):IDbContextFactory<FluxKnowledgeDbContext>
    {
        public FluxKnowledgeDbContext CreateDbContext()=>new(new DbContextOptionsBuilder<FluxKnowledgeDbContext>()
            .UseSqlServer(connection,sql=>sql.ExecutionStrategy(d=>new ObservationRetry(d))).AddInterceptors(interceptor).Options);
    }

    [NativeSqlServerFact]
    public async Task Retry_enabled_normal_sql_observation_confirms_completed_native_release()
    {
        var factory=new RetryFactory(_sql.ConnectionString);var store=new SqlGpuSchedulerStore(factory);
        await AddSlot(factory);
        var executor=new GpuInteractiveExecutor(store,new Lifecycle(store),store,new ChannelGpuSchedulerWakeSignal(),TimeProvider.System,"joined-profile","joined-settings",10);
        var operation=executor.StartOwned<int>(owner=>
        {
            var allocation=owner.BeginNativeAllocation();allocation.ConfirmReleased();
            return ValueTask.FromResult(new GpuInteractiveNativeResult<int>(42,true));
        },default);
        await WaitForRequest(factory,operation.RequestId);
        var handle=await Admit(store);await executor.DeliverAsync(handle,default);
        Assert.Equal(42,await operation.Result);
        var settlement=await operation.Settlement.WaitAsync(TimeSpan.FromSeconds(8));
        Assert.Equal(GpuInteractiveSettlementKind.Released,settlement.Kind);
        Assert.True(settlement.NativeCapacityReleased);
        Assert.Null(settlement.ObservationFailureType);
    }
    private sealed class RetryFactory(string connection):IDbContextFactory<FluxKnowledgeDbContext>
    {public FluxKnowledgeDbContext CreateDbContext()=>new(new DbContextOptionsBuilder<FluxKnowledgeDbContext>().UseSqlServer(connection,sql=>sql.EnableRetryOnFailure()).Options);}

    [NativeSqlServerFact]
    public async Task Cancelled_result_joins_actual_native_disposal_and_durable_release()
    {
        var factory = SqlTestData.CreateFactory(_sql);
        var store = new SqlGpuSchedulerStore(factory);
        await using (var db = await factory.CreateDbContextAsync())
        { db.GpuCapacitySlots.Add(new() { SlotKey = "gpu-0", State = 0, UpdatedAtUtc = DateTimeOffset.UtcNow }); await db.SaveChangesAsync(); }
        using var stopping = new CancellationTokenSource(); using var caller = new CancellationTokenSource();
        var executor = new GpuInteractiveExecutor(store, new Lifecycle(store), store, new ChannelGpuSchedulerWakeSignal(),
            TimeProvider.System, "joined-profile", "joined-settings", 10, stopping.Token);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var operation = executor.StartOwned<int>(async owner =>
        { entered.TrySetResult(); await release.Task; return new(42, true, "cancelled"); }, caller.Token);
        Task? delivery = null;
        try
        {
            await WaitForRequest(factory, operation.RequestId);
            var handle = await Admit(store);
            delivery = executor.DeliverAsync(handle, default).AsTask();
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); caller.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation.Result);
            Assert.False(operation.Settlement.IsCompleted);
            await using (var db = await factory.CreateDbContextAsync())
                Assert.Equal((int)GpuCapacitySlotState.Reserved, (await db.GpuCapacitySlots.SingleAsync()).State);
            release.SetResult(); await delivery.WaitAsync(TimeSpan.FromSeconds(8));
            var settled = await operation.Settlement.WaitAsync(TimeSpan.FromSeconds(8));
            Assert.Equal(operation.RequestId, settled.RequestId); Assert.Equal(handle, settled.Handle);
            Assert.Equal(GpuInteractiveSettlementKind.Released, settled.Kind);
            Assert.True(settled.NativeCallbackEntered); Assert.True(settled.NativeCapacityReleased);
        }
        finally
        {
            release.TrySetResult(); caller.Cancel();
            if (delivery is not null) await delivery.WaitAsync(TimeSpan.FromSeconds(8));
            await operation.Settlement.WaitAsync(TimeSpan.FromSeconds(8));
        }
    }

    [NativeSqlServerFact]
    public async Task Queued_cancellation_settles_without_native_execution()
    {
        var factory = SqlTestData.CreateFactory(_sql); var store = new SqlGpuSchedulerStore(factory);
        using var caller = new CancellationTokenSource(); var calls = 0;
        var executor = new GpuInteractiveExecutor(store, new Lifecycle(store), store, new ChannelGpuSchedulerWakeSignal(),
            TimeProvider.System, "joined-profile", "joined-settings", 10);
        var operation = executor.StartOwned<int>(_ => { calls++; return ValueTask.FromResult(new GpuInteractiveNativeResult<int>(42, true)); }, caller.Token);
        await WaitForRequest(factory, operation.RequestId); caller.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation.Result);
        var settled = await operation.Settlement.WaitAsync(TimeSpan.FromSeconds(8));
        Assert.Equal(GpuInteractiveSettlementKind.CancelledBeforeExecution, settled.Kind);
        Assert.False(settled.NativeCallbackEntered); Assert.Null(settled.Handle); Assert.Equal(0, calls);
    }

    [NativeSqlServerTheory]
    [InlineData("native-disposal", false)]
    [InlineData("receipt-rejected", true)]
    [InlineData("callback-rejected", true)]
    [InlineData("trusted-proof-rejected", false)]
    [InlineData("throw-after-disposal", true)]
    [InlineData("throw-with-live-allocation", false)]
    public async Task Failed_results_only_settle_when_native_and_exact_durable_release_are_proved(string fault, bool released)
    {
        var factory = SqlTestData.CreateFactory(_sql); var store = new SqlGpuSchedulerStore(factory);
        await AddSlot(factory);
        var lifecycle = new Lifecycle(store) { RefuseReceipt = fault is "receipt-rejected" or "trusted-proof-rejected",
            RefuseCallback = fault == "callback-rejected", RefuseTrustedProof = fault == "trusted-proof-rejected" };
        var executor = new GpuInteractiveExecutor(store, lifecycle, store, new ChannelGpuSchedulerWakeSignal(),
            TimeProvider.System, "joined-profile", "joined-settings", 10);
        var calls = 0;
        var operation = executor.StartOwned<int>(owner =>
        {
            calls++;
            if (fault == "native-disposal") owner.BeginNativeAllocation();
            if (fault is "throw-after-disposal" or "throw-with-live-allocation")
            {
                var allocation = owner.BeginNativeAllocation();
                if (fault == "throw-after-disposal") allocation.ConfirmReleased();
                throw new IOException("synthetic-callback-failure");
            }
            return ValueTask.FromResult(new GpuInteractiveNativeResult<int>(42, true));
        }, default);
        await WaitForRequest(factory, operation.RequestId);
        var handle = await Admit(store); await executor.DeliverAsync(handle, default);
        await Assert.ThrowsAsync<InvalidOperationException>(() => operation.Result);
        var settlement = await operation.Settlement.WaitAsync(TimeSpan.FromSeconds(8));
        Assert.Equal(released ? GpuInteractiveSettlementKind.Released : GpuInteractiveSettlementKind.RetainedUncertain, settlement.Kind);
        Assert.Equal(1, calls);
        await executor.DeliverAsync(handle, default); Assert.Equal(1, calls);
    }

    [NativeSqlServerTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancellation_cannot_finish_settlement_before_delayed_receipt_or_callback(bool delayCallback)
    {
        var factory = SqlTestData.CreateFactory(_sql); var store = new SqlGpuSchedulerStore(factory); await AddSlot(factory);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task Delay() { entered.TrySetResult(); await release.Task; }
        var lifecycle = new Lifecycle(store) { BeforeReceipt = delayCallback ? null : Delay, BeforeCallback = delayCallback ? Delay : null };
        using var caller = new CancellationTokenSource();
        var executor = new GpuInteractiveExecutor(store, lifecycle, store, new ChannelGpuSchedulerWakeSignal(),
            TimeProvider.System, "joined-profile", "joined-settings", 10);
        var operation = executor.StartOwned<int>(_ => ValueTask.FromResult(new GpuInteractiveNativeResult<int>(42, true)), caller.Token);
        Task? delivery = null;
        try
        {
            await WaitForRequest(factory, operation.RequestId); delivery = executor.DeliverAsync(await Admit(store), default).AsTask();
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); caller.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation.Result);
            Assert.False(operation.Settlement.IsCompleted);
            release.TrySetResult(); await delivery.WaitAsync(TimeSpan.FromSeconds(8));
            Assert.Equal(GpuInteractiveSettlementKind.Released, (await operation.Settlement.WaitAsync(TimeSpan.FromSeconds(8))).Kind);
        }
        finally { release.TrySetResult(); caller.Cancel(); if (delivery is not null) await delivery.WaitAsync(TimeSpan.FromSeconds(8)); await operation.Settlement.WaitAsync(TimeSpan.FromSeconds(8)); }
    }

    [NativeSqlServerFact]
    public async Task Exact_release_remains_proved_after_a_later_batch_reuses_the_slot_and_owner_key()
    {
        var factory = SqlTestData.CreateFactory(_sql); var store = new SqlGpuSchedulerStore(factory); await AddSlot(factory);
        var executor = new GpuInteractiveExecutor(store, new Lifecycle(store), store, new ChannelGpuSchedulerWakeSignal(),
            TimeProvider.System, "joined-profile", "joined-settings", 10);
        var first = executor.StartOwned<int>(_ => ValueTask.FromResult(new GpuInteractiveNativeResult<int>(42, true)), default);
        await WaitForRequest(factory, first.RequestId); var firstHandle = await Admit(store); await executor.DeliverAsync(firstHandle, default);
        Assert.Equal(42, await first.Result); Assert.Equal(GpuInteractiveSettlementKind.Released, (await first.Settlement).Kind);
        var second = executor.StartOwned<int>(_ => ValueTask.FromResult(new GpuInteractiveNativeResult<int>(43, true)), default);
        Task? delivery = null;
        try
        {
            await WaitForRequest(factory, second.RequestId); var secondHandle = await Admit(store);
            Assert.NotEqual(firstHandle, secondHandle);
            var snapshot = await ((IGpuInteractiveSettlementStore)store).ReadInteractiveRequestAsync(first.RequestId, first.ExecutorInstanceId, default);
            Assert.Equal(firstHandle, snapshot.Handle); Assert.Equal(GpuInteractiveRequestState.Released, snapshot.State);
            var foreign = await ((IGpuInteractiveSettlementStore)store).ReadInteractiveRequestAsync(second.RequestId, first.ExecutorInstanceId, default);
            Assert.Equal(secondHandle, foreign.Handle); Assert.Equal(GpuInteractiveRequestState.Owned, foreign.State);
            Assert.Equal(GpuInteractiveRequestState.Uncertain, (await ((IGpuInteractiveSettlementStore)store)
                .ReadInteractiveRequestAsync(first.RequestId, Guid.NewGuid(), default)).State);
            delivery = executor.DeliverAsync(secondHandle, default).AsTask(); await delivery;
            Assert.Equal(43, await second.Result); Assert.Equal(GpuInteractiveSettlementKind.Released, (await second.Settlement).Kind);
        }
        finally
        {
            // An assertion failure must not leave the second exact admitted request unobserved.
            foreach (var handle in await store.ReadPendingDispatchesAsync(default)) await executor.DeliverAsync(handle, default);
            if (delivery is not null) await delivery;
            await second.Settlement.WaitAsync(TimeSpan.FromSeconds(8));
        }
    }

    private static async Task AddSlot(IDbContextFactory<FluxKnowledgeDbContext> factory)
    { await using var db = await factory.CreateDbContextAsync(); db.GpuCapacitySlots.Add(new() { SlotKey = "gpu-0", State = 0, UpdatedAtUtc = DateTimeOffset.UtcNow }); await db.SaveChangesAsync(); }

    [NativeSqlServerTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancellation_joins_handoff_even_when_commit_response_is_lost(bool loseCommittedResponse)
    {
        var factory = SqlTestData.CreateFactory(_sql); var store = new SqlGpuSchedulerStore(factory);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var requests = new Requests(store) { BeforeHandoff = loseCommittedResponse ? null : async () => { entered.TrySetResult(); await release.Task; },
            LoseCommittedHandoff = loseCommittedResponse };
        using var caller = new CancellationTokenSource(); var calls = 0;
        var executor = new GpuInteractiveExecutor(requests, new Lifecycle(store), store, new ChannelGpuSchedulerWakeSignal(),
            TimeProvider.System, "joined-profile", "joined-settings", 10);
        var operation = executor.StartOwned<int>(_ => { calls++; return ValueTask.FromResult(new GpuInteractiveNativeResult<int>(42, true)); }, caller.Token);
        try
        {
            if (!loseCommittedResponse)
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); caller.Cancel();
                Assert.False(operation.Result.IsCompleted); Assert.False(operation.Settlement.IsCompleted);
                release.TrySetResult(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation.Result);
            }
            else await Assert.ThrowsAsync<IOException>(() => operation.Result);
            var settlement = await operation.Settlement.WaitAsync(TimeSpan.FromSeconds(8));
            Assert.Equal(loseCommittedResponse ? GpuInteractiveSettlementKind.CancelledBeforeExecution : GpuInteractiveSettlementKind.NoHandoff, settlement.Kind);
            Assert.Equal(0, calls);
            await using var db = await factory.CreateDbContextAsync();
            Assert.Equal(loseCommittedResponse ? 1 : 0, await db.GpuMiniTasks.CountAsync());
        }
        finally { release.TrySetResult(); caller.Cancel(); await operation.Settlement.WaitAsync(TimeSpan.FromSeconds(8)); }
    }

    [NativeSqlServerFact]
    public async Task One_executor_admits_explicit_profiles_and_preserves_its_default_and_exact_request_identity()
    {
        var factory = SqlTestData.CreateFactory(_sql); var store = new SqlGpuSchedulerStore(factory); await AddSlot(factory);
        var extra = new GpuInteractiveExecutionProfile("joined-fp16-profile", "joined-settings", 10);
        var executor = new GpuInteractiveExecutor(store, new Lifecycle(store), store, new ChannelGpuSchedulerWakeSignal(),
            TimeProvider.System, "joined-profile", "joined-settings", 10, new[] { extra });
        Assert.Throws<ArgumentException>(() => executor.StartOwned<int>(_ => throw new InvalidOperationException(), default,
            extra with { RuntimeKey = "unapproved-profile" }));
        foreach (var profile in new GpuInteractiveExecutionProfile?[] { extra, null })
        {
            var calls = 0;
            var operation = executor.StartOwned<int>(owner =>
            { owner.RequireActive(profile?.RuntimeKey ?? "joined-profile", "joined-settings"); calls++; return ValueTask.FromResult(new GpuInteractiveNativeResult<int>(42, true)); }, default, profile);
            await WaitForRequest(factory, operation.RequestId); var handle = await Admit(store); await executor.DeliverAsync(handle, default);
            Assert.Equal(42, await operation.Result); Assert.Equal(GpuInteractiveSettlementKind.Released, (await operation.Settlement).Kind);
            var snapshot = await ((IGpuInteractiveSettlementStore)store).ReadInteractiveRequestAsync(operation.RequestId, operation.ExecutorInstanceId, default);
            Assert.Equal(profile?.RuntimeKey ?? "joined-profile", snapshot.RuntimeKey); Assert.Equal(1, calls);
        }
    }

    private sealed class Requests(SqlGpuSchedulerStore store) : IGpuInteractiveRequestStore, IGpuInteractiveSettlementStore
    {
        internal Func<Task>? BeforeHandoff;
        internal bool LoseCommittedHandoff;
        public async ValueTask<GpuMiniTaskHandoffResult> HandoffInteractiveAsync(GpuInteractiveHandoffRequest r, CancellationToken ct)
        { if (BeforeHandoff is not null) await BeforeHandoff(); var result = await store.HandoffInteractiveAsync(r, ct); if (LoseCommittedHandoff) throw new IOException("synthetic-committed-response-loss"); return result; }
        public ValueTask<bool> CancelInteractiveAsync(Guid request, Guid instance, CancellationToken ct) => store.CancelInteractiveAsync(request, instance, ct);
        public ValueTask<GpuInteractiveExecutionWork?> ReadInteractiveExecutionAsync(GpuExecutorBatchHandle h, Guid instance, GpuExecutorDispatchState state, CancellationToken ct) => store.ReadInteractiveExecutionAsync(h, instance, state, ct);
        public ValueTask<IReadOnlyList<GpuInteractiveRecoveryWork>> ReadInteractiveRecoveryAsync(CancellationToken ct) => store.ReadInteractiveRecoveryAsync(ct);
        public ValueTask<bool> CancelLostInteractiveReadyAsync(Guid request, GpuInteractiveOwnerIdentity owner, CancellationToken ct) => store.CancelLostInteractiveReadyAsync(request, owner, ct);
        public ValueTask<GpuInteractiveRequestSnapshot> ReadInteractiveRequestAsync(Guid request, Guid instance, CancellationToken ct) => store.ReadInteractiveRequestAsync(request, instance, ct);
    }

    private static async Task WaitForRequest(IDbContextFactory<FluxKnowledgeDbContext> factory, Guid id)
    {
        await using var db = await factory.CreateDbContextAsync();
        for (var i = 0; i < 100 && !await db.GpuMiniTasks.AnyAsync(t => t.Id == id); i++) await Task.Delay(10);
        Assert.True(await db.GpuMiniTasks.AnyAsync(t => t.Id == id));
    }
    private static async Task<GpuExecutorBatchHandle> Admit(SqlGpuSchedulerStore store, long capacityBytes = 1024)
    {
        var result = await store.RunAdmissionRoundAsync(Guid.NewGuid(), GpuSchedulerWakeReason.WorkReady,
            new(4, capacityBytes, TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(1)),
            (c, _) => ValueTask.FromResult(new GpuAdmissionDecision(GpuAdmissionDisposition.Admit, "gpu-0", "joined-owner", null, c.RequiredExecutorKey)), default);
        Assert.Equal(GpuAdmissionDisposition.Admit, result.Disposition);
        return Assert.Single(await store.ReadPendingDispatchesAsync(default));
    }
    private sealed class Lifecycle(SqlGpuSchedulerStore store) : IGpuExecutorLifecycleSink
    {
        internal bool RefuseReceipt, RefuseCallback, RefuseTrustedProof;
        internal Func<Task>? BeforeReceipt, BeforeCallback;
        public ValueTask<GpuExecutorDispatchMutationResult> AcknowledgeAsync(GpuExecutorAcknowledgement r, CancellationToken ct) => store.AcknowledgeAsync(r, ct);
        public ValueTask<GpuExecutorDispatchMutationResult> MarkDeliveryUncertainAsync(GpuExecutorDeliveryUncertainty r, CancellationToken ct) => store.MarkDeliveryUncertainAsync(r, ct);
        public async ValueTask<GpuExecutorDispatchMutationResult> RecordReceiptAsync(GpuExecutorResultReceipt r, CancellationToken ct)
        { if (BeforeReceipt is not null) await BeforeReceipt(); return RefuseReceipt ? new(false, false) : await store.RecordReceiptAsync(r, ct); }
        public ValueTask<GpuExecutorDispatchMutationResult> RecordTrustedEvidenceAsync(GpuExecutorTrustedEvidence r, CancellationToken ct) =>
            RefuseTrustedProof ? ValueTask.FromResult(new GpuExecutorDispatchMutationResult(false, false)) : store.RecordTrustedEvidenceAsync(r, ct);
        public async ValueTask<GpuBatchCallbackResult> HandleCallbackAsync(Guid id, GpuBatchCallback r, CancellationToken ct)
        { if (BeforeCallback is not null) await BeforeCallback(); if (RefuseCallback) throw new InvalidOperationException("synthetic-callback-refusal"); return await store.ApplyBatchCallbackAsync(id, r, ct); }
    }
}
