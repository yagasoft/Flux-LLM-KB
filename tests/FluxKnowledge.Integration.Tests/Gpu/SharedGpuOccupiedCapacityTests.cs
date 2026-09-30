using FluxKnowledge.Application.Documents;
using FluxKnowledge.Application.Gpu;
using FluxKnowledge.Domain.Gpu;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using FluxKnowledge.Infrastructure.SqlServer.Workers;
using FluxKnowledge.Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Gpu;

public sealed class SharedGpuOccupiedCapacityTests(NativeSqlServerFixture fixture) : IClassFixture<NativeSqlServerFixture>
{
    private static readonly GpuWorkloadPolicy Policy = new([
        new(PaddleOcrVlmRuntimeContract.ModelRuntimeKey, PaddleOcrVlmRuntimeContract.SettingsFingerprint, GpuWorkloadKind.Ocr),
        new("synthetic-retrieval", "fixed-search-settings", GpuWorkloadKind.Retrieval)]);
    private static readonly SharedGpuAdmissionGate Gate = new(Policy, "retrieval-background", 4096);
    private static readonly GpuSchedulerOptions Options = new(1, 4096, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(10));

    [NativeSqlServerTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Occupied_shared_slot_returns_busy_replays_and_admits_after_confirmed_release(bool atSafeBoundary)
    {
        var (factory, first, second) = await CreateAsync();
        var store = new SqlGpuSchedulerStore(factory);
        Assert.Equal(GpuAdmissionDisposition.Admit, (await AdmitAsync(store)).Disposition);
        if (atSafeBoundary)
        {
            await using var boundary = await factory.CreateDbContextAsync();
            (await boundary.GpuBatches.SingleAsync()).State = (int)GpuBatchState.AtSafeBoundary;
            await boundary.SaveChangesAsync();
        }
        var busyOperation = Guid.NewGuid();
        var busy = await AdmitAsync(store, busyOperation);
        Assert.Equal(GpuAdmissionDisposition.Busy, busy.Disposition);
        Assert.False(busy.Committed);
        var replay = await store.RunAdmissionRoundAsync(busyOperation, GpuSchedulerWakeReason.WorkReady, Options,
            (_, _) => throw new InvalidOperationException("A replay must not call the gate."), CancellationToken.None);
        Assert.True(replay.IsIdempotentReplay);
        Assert.Equal(busy with { IsIdempotentReplay = true }, replay);
        GpuExecutorBatchHandle handle;
        await using (var verify = await factory.CreateDbContextAsync())
        {
            Assert.Single(await verify.GpuBatches.ToListAsync());
            var dispatch = Assert.Single(await verify.GpuExecutorDispatches.ToListAsync());
            handle = new(dispatch.BatchId, dispatch.CapacitySlotKey, dispatch.ExecutorKey, dispatch.AdmissionGeneration, dispatch.DispatchId);
            Assert.Equal((int)GpuMiniTaskExecutionState.Ready, (await verify.GpuMiniTasks.SingleAsync(task => task.Id == second)).ExecutionState);
            Assert.Equal(first, await verify.GpuMiniTasks.Where(task => task.ExecutionState == (int)GpuMiniTaskExecutionState.Active).Select(task => task.Id).SingleAsync());
        }
        Assert.True((await store.AcknowledgeAsync(new(Guid.NewGuid(), handle), CancellationToken.None)).Accepted);
        Assert.True((await store.RecordReceiptAsync(new(Guid.NewGuid(), handle, first, GpuMiniTaskBoundaryDisposition.Completed,
            null, GpuExecutorEvidenceClass.TaskOutcomeConfirmed), CancellationToken.None)).Accepted);
        Assert.True((await store.ApplyBatchCallbackAsync(Guid.NewGuid(), new(handle, GpuBatchCallbackKind.Completed,
            [new(first, GpuMiniTaskBoundaryDisposition.Completed)], true), CancellationToken.None)).Accepted);
        Assert.Equal(GpuAdmissionDisposition.Admit, (await AdmitAsync(store)).Disposition);
        await using var final = await factory.CreateDbContextAsync();
        Assert.Equal(2, await final.GpuBatches.CountAsync());
        Assert.Equal(second, await final.GpuMiniTasks.Where(task => task.ExecutionState == (int)GpuMiniTaskExecutionState.Active).Select(task => task.Id).SingleAsync());
    }

    [NativeSqlServerFact]
    public async Task Concurrent_production_gate_rounds_keep_one_reservation_and_one_ready_task()
    {
        var (factory, _, _) = await CreateAsync();
        var firstAtLock = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondAttempt = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = 0;
        var store = new SqlGpuSchedulerStore(factory,
            beforeAdmissionLockAttempt: _ => { if (Interlocked.Increment(ref attempts) == 2) secondAttempt.SetResult(); return ValueTask.CompletedTask; },
            afterAdmissionLockAcquired: async _ => { if (attempts == 1) { firstAtLock.SetResult(); await releaseFirst.Task; } });
        var first = AdmitAsync(store).AsTask();
        await firstAtLock.Task.WaitAsync(TimeSpan.FromSeconds(15));
        var second = AdmitAsync(store).AsTask();
        try { await secondAttempt.Task.WaitAsync(TimeSpan.FromSeconds(15)); Assert.False(second.IsCompleted); }
        finally { releaseFirst.SetResult(); }
        var results = await Task.WhenAll(first, second);
        Assert.Single(results, result => result.Disposition == GpuAdmissionDisposition.Admit);
        Assert.Single(results, result => result.Disposition == GpuAdmissionDisposition.Busy);
        await using var verify = await factory.CreateDbContextAsync();
        Assert.Single(await verify.GpuBatches.ToListAsync());
        Assert.Single(await verify.GpuExecutorDispatches.ToListAsync());
        Assert.Equal(1, await verify.GpuMiniTasks.CountAsync(task => task.ExecutionState == (int)GpuMiniTaskExecutionState.Active));
        Assert.Equal(1, await verify.GpuMiniTasks.CountAsync(task => task.ExecutionState == (int)GpuMiniTaskExecutionState.Ready));
    }

    [NativeSqlServerTheory]
    [InlineData("missing")]
    [InlineData("uncertain")]
    [InlineData("owner-mismatch")]
    public async Task Invalid_or_uncertain_capacity_still_refuses_admission(string fault)
    {
        var (factory, _, second) = await CreateAsync();
        var store = new SqlGpuSchedulerStore(factory);
        if (fault != "missing") Assert.Equal(GpuAdmissionDisposition.Admit, (await AdmitAsync(store)).Disposition);
        await using (var mutate = await factory.CreateDbContextAsync())
        {
            var slot = await mutate.GpuCapacitySlots.SingleAsync();
            if (fault == "missing") mutate.GpuCapacitySlots.Remove(slot);
            else if (fault == "uncertain") slot.State = (int)GpuCapacitySlotState.Uncertain;
            else slot.OwnerKey = "different-owner";
            await mutate.SaveChangesAsync();
        }
        await Assert.ThrowsAsync<InvalidOperationException>(() => AdmitAsync(store).AsTask());
        await using var verify = await factory.CreateDbContextAsync();
        Assert.Equal(fault == "missing" ? 0 : 1, await verify.GpuBatches.CountAsync());
        Assert.Equal((int)GpuMiniTaskExecutionState.Ready, (await verify.GpuMiniTasks.SingleAsync(task => task.Id == second)).ExecutionState);
    }

    private async Task<(IDbContextFactory<FluxKnowledgeDbContext> Factory, Guid First, Guid Second)> CreateAsync()
    {
        var existing = new SqlGpuAdmissionTests(fixture);
        var factory = await existing.CreateEnvironmentAsync();
        await using (var context = await factory.CreateDbContextAsync())
        {
            context.GpuCapacitySlots.Remove(await context.GpuCapacitySlots.SingleAsync());
            context.GpuCapacitySlots.Add(new GpuCapacitySlotEntity { SlotKey = PaddleOcrVlmRuntimeContract.CapacitySlotKey, State = (int)GpuCapacitySlotState.Available, UpdatedAtUtc = DateTimeOffset.UtcNow });
            await context.SaveChangesAsync();
        }
        var first = await existing.AddReadyAsync(factory, GpuPriorityLane.DocumentIndexing, "synthetic-retrieval", "fixed-search-settings", 1024);
        var second = await existing.AddReadyAsync(factory, GpuPriorityLane.DocumentIndexing, "synthetic-retrieval", "fixed-search-settings", 1024);
        return (factory, first, second);
    }

    private static ValueTask<GpuSchedulerAdmissionRoundResult> AdmitAsync(SqlGpuSchedulerStore store, Guid? operation = null) =>
        store.RunAdmissionRoundAsync(operation ?? Guid.NewGuid(), GpuSchedulerWakeReason.WorkReady, Options, Gate.DecideAsync, CancellationToken.None);
}
