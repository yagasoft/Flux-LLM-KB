using FluxKnowledge.Application.Gpu;
using FluxKnowledge.Application.Sources;
using FluxKnowledge.Application.Indexing;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using FluxKnowledge.Infrastructure.Usearch;
using FluxKnowledge.Domain.Gpu;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Integration.Tests.Support;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Gpu;

[Collection("sql-full-text")]
public sealed class SqlGpuMaintenanceDrainTests(NativeSqlServerFixture fixture) : IClassFixture<NativeSqlServerFixture>
{
    [NativeSqlServerFact]
    public async Task Deployment_hold_and_active_rebuild_block_previously_queued_ocr_without_changing_its_reservation_state()
    {
        var seed = new SqlGpuAdmissionTests(fixture);
        var factory = await seed.CreateEnvironmentAsync();
        await seed.AddReadyAsync(factory, GpuPriorityLane.DocumentIndexing, "ocr-runtime", "ocr-settings", 10);
        var hold = new Hold();
        var scheduler = new SqlGpuSchedulerStore(factory, deploymentValidationHold: hold);
        async Task AssertWaitingAsync()
        {
            _ = await scheduler.RunAdmissionRoundAsync(GpuSchedulerWakeReason.WorkReady,
                new(1, 100, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(10)),
                SqlGpuAdmissionTests.Admit("slot-a"), CancellationToken.None);
            await using var context = factory.CreateDbContext();
            Assert.Equal((int)GpuCapacitySlotState.Available, (await context.GpuCapacitySlots.SingleAsync()).State);
            Assert.Equal((int)GpuMiniTaskExecutionState.Ready, (await context.GpuMiniTasks.SingleAsync()).ExecutionState);
        }
        await AssertWaitingAsync();
        var store = new SqlCorpusRebuildStore(factory);
        var plan = await store.ReadPlanAsync(Guid.NewGuid(), new("test-profile", 2), new string('a', 64), CancellationToken.None);
        await store.CommitAsync(plan, "slot-a", CancellationToken.None);
        hold.Current = new(true, plan.OperationId);
        await AssertWaitingAsync();
        hold.Current = new(false, null);
        await AssertWaitingAsync(); // The SQL rebuild boundary also blocks queued OCR if the file is removed.
    }

    private sealed class Hold : IDeploymentValidationHold
    {
        public DeploymentHoldAdmission Current { get; set; } = new(true, null);
        public bool IsHeld => Current.IsHeld;
        public DeploymentHoldAdmission ReadAdmissionState() => Current;
        public ValueTask WaitUntilReleasedAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }

    [NativeSqlServerFact]
    public async Task Maintenance_fence_stops_new_admission_while_an_active_page_finishes_through_its_existing_lifecycle()
    {
        var seed = new SqlGpuAdmissionTests(fixture);
        var factory = await seed.CreateEnvironmentAsync();
        var first = await seed.AddReadyAsync(factory, GpuPriorityLane.DocumentIndexing, "ocr-runtime", "ocr-settings", 10);
        Assert.True((await SqlGpuAdmissionTests.AdmitAsync(factory, SqlGpuAdmissionTests.Admit("slot-a"))).Committed);
        var lifecycle = new SqlGpuSchedulerStore(factory);
        var handle = Assert.Single(await lifecycle.ReadPendingDispatchesAsync(CancellationToken.None));
        Assert.True((await lifecycle.AcknowledgeAsync(new(Guid.NewGuid(), handle), CancellationToken.None)).Committed);
        var drain = await SqlGpuMaintenanceDrainLease.AcquireAsync(factory, CancellationToken.None);
        var atFence = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var next = new SqlGpuSchedulerStore(factory, beforeAdmissionLockAttempt: _ => { atFence.TrySetResult(); return ValueTask.CompletedTask; });
        Task<GpuSchedulerAdmissionRoundResult>? pending = null;
        try
        {
            Assert.False(await drain.IsDrainedAsync("slot-a", CancellationToken.None));
            await seed.AddReadyAsync(factory, GpuPriorityLane.DocumentIndexing, "ocr-runtime", "ocr-settings", 10);
            pending = next.RunAdmissionRoundAsync(GpuSchedulerWakeReason.WorkReady,
                new(1, 100, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(10)),
                SqlGpuAdmissionTests.Admit("slot-a"), CancellationToken.None).AsTask();
            await atFence.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.False(pending.IsCompleted);
            Assert.True((await lifecycle.RecordReceiptAsync(new(Guid.NewGuid(), handle, first,
                GpuMiniTaskBoundaryDisposition.Completed, null, GpuExecutorEvidenceClass.TaskOutcomeConfirmed), CancellationToken.None)).Committed);
            Assert.True((await lifecycle.ApplyBatchCallbackAsync(Guid.NewGuid(), new(handle, GpuBatchCallbackKind.Completed,
                [new(first, GpuMiniTaskBoundaryDisposition.Completed)], true), CancellationToken.None)).Committed);
            Assert.True(await drain.IsDrainedAsync("slot-a", CancellationToken.None));
            Assert.False(pending.IsCompleted);
        }
        finally { await drain.DisposeAsync(); }
        Assert.NotNull(pending);
        Assert.True((await pending.WaitAsync(TimeSpan.FromSeconds(3))).Committed);
    }

    [NativeSqlServerFact]
    public async Task Uncertain_capacity_and_a_missing_slot_never_count_as_a_drained_gpu()
    {
        var factory = await new SqlGpuAdmissionTests(fixture).CreateEnvironmentAsync(GpuCapacitySlotState.Uncertain);
        await using var drain = await SqlGpuMaintenanceDrainLease.AcquireAsync(factory, CancellationToken.None);
        Assert.False(await drain.IsDrainedAsync("slot-a", CancellationToken.None));
        Assert.False(await drain.IsDrainedAsync("absent", CancellationToken.None));
    }

    [NativeSqlServerFact]
    public async Task Lost_maintenance_session_cannot_confirm_safe_drain_or_reconnect()
    {
        var factory = await new SqlGpuAdmissionTests(fixture).CreateEnvironmentAsync();
        await using var drain = await SqlGpuMaintenanceDrainLease.AcquireAsync(factory, CancellationToken.None);
        Assert.True(await drain.IsDrainedAsync("slot-a", CancellationToken.None));
        await using var killer = new SqlConnection(fixture.ConnectionString);
        await killer.OpenAsync();
        await using var kill = new SqlCommand($"KILL {drain.SessionId}", killer);
        await kill.ExecuteNonQueryAsync();
        await Assert.ThrowsAnyAsync<Exception>(() => drain.IsDrainedAsync("slot-a", CancellationToken.None).AsTask());
    }
}
