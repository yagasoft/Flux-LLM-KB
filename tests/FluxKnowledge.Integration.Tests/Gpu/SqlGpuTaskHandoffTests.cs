using FluxKnowledge.Application.Gpu;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Domain.Common;
using FluxKnowledge.Domain.Gpu;
using FluxKnowledge.Domain.Jobs;
using FluxKnowledge.Domain.Pipeline;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using FluxKnowledge.Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Gpu;

public sealed class SqlGpuTaskHandoffTests(NativeSqlServerFixture fixture)
    : IClassFixture<NativeSqlServerFixture>
{
    private readonly NativeSqlServerFixture _fixture = fixture;

    [NativeSqlServerFact]
    public async Task Handoff_commits_mini_task_parent_transition_and_work_ready_wake_together()
    {
        var (factory, claim, request) = await CreateClaimedRequestAsync("handoff:once");

        var result = await new SqlGpuSchedulerStore(factory).GpuTaskHandoffAsync(
            request,
            CancellationToken.None);

        Assert.True(result.Committed);
        Assert.False(result.IsIdempotentReplay);
        await using var verification = await factory.CreateDbContextAsync();
        var task = await verification.GpuMiniTasks.SingleAsync();
        Assert.Equal(result.MiniTaskId, task.Id);
        Assert.Equal((int)GpuMiniTaskExecutionState.Ready, task.ExecutionState);
        Assert.True(task.CreatedSequence > 0);
        var parent = await verification.Jobs.SingleAsync(job => job.Id == claim.JobId.Value);
        Assert.Equal((int)PublicJobState.GpuQueued, parent.PublicState);
        Assert.Null(parent.LeaseOwner);
        var wake = await verification.GpuSchedulerStates.SingleAsync(state => state.Id == 1);
        Assert.Equal(1, wake.WakeGeneration);
        Assert.Equal((int)GpuSchedulerWakeReason.WorkReady, wake.PendingWakeReasons);
    }

    [NativeSqlServerFact]
    public async Task Handoff_failure_after_task_insert_rolls_back_task_parent_and_wake_evidence()
    {
        var (factory, claim, request) = await CreateClaimedRequestAsync("handoff:rollback");
        var store = new SqlGpuSchedulerStore(
            factory,
            _ => ValueTask.FromException(new InvalidOperationException("injected hand-off failure")));

        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await store.GpuTaskHandoffAsync(request, CancellationToken.None));

        await AssertNoSchedulerMutationAsync(factory, claim.JobId.Value);
    }

    [NativeSqlServerFact]
    public async Task Concurrent_same_idempotency_handoffs_create_one_task_and_one_parent_transition()
    {
        var (factory, claim, request) = await CreateClaimedRequestAsync("handoff:concurrent");
        var firstMiniTaskPersisted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondAtIdempotencyDecision = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstTransaction = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var idempotencyDecisionCount = 0;
        var persistedCount = 0;
        var store = new SqlGpuSchedulerStore(
            factory,
            afterMiniTaskPersisted: async _ =>
            {
                if (Interlocked.Increment(ref persistedCount) == 1)
                {
                    firstMiniTaskPersisted.SetResult();
                    await releaseFirstTransaction.Task;
                }
            },
            beforeIdempotencyRead: _ =>
            {
                if (Interlocked.Increment(ref idempotencyDecisionCount) == 2)
                {
                    secondAtIdempotencyDecision.SetResult();
                }

                return ValueTask.CompletedTask;
            });

        var first = store.GpuTaskHandoffAsync(request, CancellationToken.None).AsTask();
        await firstMiniTaskPersisted.Task;
        var second = store.GpuTaskHandoffAsync(request, CancellationToken.None).AsTask();
        await secondAtIdempotencyDecision.Task;
        releaseFirstTransaction.SetResult();
        var results = await Task.WhenAll(first, second);

        Assert.Single(results.Select(result => result.MiniTaskId).Distinct());
        Assert.Single(results, result => !result.IsIdempotentReplay);
        Assert.Single(results, result => result.IsIdempotentReplay);
        await using var verification = await factory.CreateDbContextAsync();
        Assert.Equal(1, await verification.GpuMiniTasks.CountAsync());
        Assert.Equal(
            (int)PublicJobState.GpuQueued,
            await verification.Jobs.Where(job => job.Id == claim.JobId.Value).Select(job => job.PublicState).SingleAsync());
        Assert.Equal(1, (await verification.GpuSchedulerStates.SingleAsync(state => state.Id == 1)).WakeGeneration);
    }

    [NativeSqlServerFact]
    public async Task Identical_handoff_replays_after_its_mini_task_is_admitted()
    {
        var (factory, claim, request) = await CreateClaimedRequestAsync("handoff:replay-after-admission");
        var handoff = await new SqlGpuSchedulerStore(factory).GpuTaskHandoffAsync(request, CancellationToken.None);
        await using (var arrange = await factory.CreateDbContextAsync())
        {
            arrange.GpuCapacitySlots.Add(new GpuCapacitySlotEntity
            {
                SlotKey = "slot-a",
                State = (int)GpuCapacitySlotState.Available,
                UpdatedAtUtc = DateTimeOffset.Parse("2026-07-29T09:00:00+00:00")
            });
            await arrange.SaveChangesAsync();
        }
        await SqlGpuAdmissionTests.AdmitAsync(
            factory,
            SqlGpuAdmissionTests.Admit("slot-a"),
            new GpuSchedulerOptions(1, 1024, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(10)));

        var replay = await new SqlGpuSchedulerStore(factory).GpuTaskHandoffAsync(request, CancellationToken.None);

        Assert.True(replay.Committed);
        Assert.True(replay.IsIdempotentReplay);
        Assert.Equal(handoff.MiniTaskId, replay.MiniTaskId);
        await using var verification = await factory.CreateDbContextAsync();
        Assert.Equal(1, await verification.GpuMiniTasks.CountAsync());
        Assert.Equal(1, await verification.GpuBatches.CountAsync());
        Assert.Equal(1, await verification.GpuSchedulerStates.Select(state => state.WakeGeneration).SingleAsync());
        Assert.Equal(
            (int)PublicJobState.GpuProcessing,
            await verification.Jobs.Where(job => job.Id == claim.JobId.Value).Select(job => job.PublicState).SingleAsync());
    }

    [NativeSqlServerFact]
    public async Task Invalid_or_conflicting_handoffs_leave_parent_queue_and_wake_state_unchanged()
    {
        var (factory, claim, request) = await CreateClaimedRequestAsync("handoff:invalid");
        var store = new SqlGpuSchedulerStore(factory);

        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await store.GpuTaskHandoffAsync(
                request with { ParentJob = claim with { SourceRevision = claim.SourceRevision + 1 } },
                CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await store.GpuTaskHandoffAsync(
                request with { ParentJob = claim with { LeaseOwner = "wrong-owner" } },
                CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await store.GpuTaskHandoffAsync(
                request with { ParentJob = claim with { LeaseGeneration = claim.LeaseGeneration + 1 } },
                CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            async () => await store.GpuTaskHandoffAsync(
                request with { PriorityLane = (GpuPriorityLane)99 },
                CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(
            async () => await store.GpuTaskHandoffAsync(
                request with { ModelRuntimeKey = "" },
                CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(
            async () => await store.GpuTaskHandoffAsync(
                request with { ParentJob = claim with { PublicState = PublicJobState.GpuQueued } },
                CancellationToken.None));

        await AssertNoSchedulerMutationAsync(factory, claim.JobId.Value);

        var committed = await store.GpuTaskHandoffAsync(request, CancellationToken.None);
        Assert.True(committed.Committed);
        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await store.GpuTaskHandoffAsync(
                request with
                {
                    ParentJob = claim with { PipelineRecordId = new PipelineRecordId(Guid.NewGuid()) }
                },
                CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await store.GpuTaskHandoffAsync(
                request with { ParentJob = claim with { Stage = (PipelineStage)99 } },
                CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await store.GpuTaskHandoffAsync(
                request with { ParentJob = claim with { Operation = "conflicting-operation" } },
                CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await store.GpuTaskHandoffAsync(
                request with { ParentJob = claim with { LeaseOwner = "conflicting-owner" } },
                CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await store.GpuTaskHandoffAsync(
                request with { SettingsFingerprint = "conflicting-settings" },
                CancellationToken.None));
        await using var verification = await factory.CreateDbContextAsync();
        Assert.Equal(1, await verification.GpuMiniTasks.CountAsync());
        Assert.Equal("gpu-handoff-worker", await verification.GpuMiniTasks.Select(task => task.HandoffLeaseOwner).SingleAsync());
        Assert.Equal(1, (await verification.GpuSchedulerStates.SingleAsync(state => state.Id == 1)).WakeGeneration);
    }

    [NativeSqlServerFact]
    public async Task Case_only_handoff_lease_and_idempotency_fences_reject_without_mutation()
    {
        var (factory, claim, request) = await CreateClaimedRequestAsync("handoff:case-fence");
        var store = new SqlGpuSchedulerStore(factory);
        var caseOnlyLeaseOwner = claim.LeaseOwner!.ToUpperInvariant();

        Assert.NotEqual(claim.LeaseOwner, caseOnlyLeaseOwner, StringComparer.Ordinal);
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await store.GpuTaskHandoffAsync(
                request with { ParentJob = claim with { LeaseOwner = caseOnlyLeaseOwner } },
                CancellationToken.None));
        await AssertNoSchedulerMutationAsync(factory, claim.JobId.Value);

        var committed = await store.GpuTaskHandoffAsync(request, CancellationToken.None);
        Assert.True(committed.Committed);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await store.GpuTaskHandoffAsync(
                request with { IdempotencyKey = request.IdempotencyKey.ToUpperInvariant() },
                CancellationToken.None));

        await using var verification = await factory.CreateDbContextAsync();
        Assert.Equal(1, await verification.GpuMiniTasks.CountAsync());
        Assert.Equal(
            (int)PublicJobState.GpuQueued,
            await verification.Jobs
                .Where(job => job.Id == claim.JobId.Value)
                .Select(job => job.PublicState)
                .SingleAsync());
        Assert.Equal(1, await verification.GpuSchedulerStates.Select(state => state.WakeGeneration).SingleAsync());
    }

    [NativeSqlServerFact]
    public async Task Trailing_whitespace_handoff_fence_keys_reject_without_mutation()
    {
        var (factory, claim, request) = await CreateClaimedRequestAsync("handoff:trailing-fence");
        var store = new SqlGpuSchedulerStore(factory);

        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await store.GpuTaskHandoffAsync(
                request with { ParentJob = claim with { Operation = $"{claim.Operation} " } },
                CancellationToken.None));
        await AssertNoSchedulerMutationAsync(factory, claim.JobId.Value);

        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await store.GpuTaskHandoffAsync(
                request with { ParentJob = claim with { LeaseOwner = $"{claim.LeaseOwner} " } },
                CancellationToken.None));
        await AssertNoSchedulerMutationAsync(factory, claim.JobId.Value);

        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await store.GpuTaskHandoffAsync(
                request with { ModelRuntimeKey = $"{request.ModelRuntimeKey} " },
                CancellationToken.None));
        await AssertNoSchedulerMutationAsync(factory, claim.JobId.Value);

        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await store.GpuTaskHandoffAsync(
                request with { SettingsFingerprint = $"{request.SettingsFingerprint} " },
                CancellationToken.None));
        await AssertNoSchedulerMutationAsync(factory, claim.JobId.Value);

        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await store.GpuTaskHandoffAsync(
                request with { IdempotencyKey = $"{request.IdempotencyKey} " },
                CancellationToken.None));
        await AssertNoSchedulerMutationAsync(factory, claim.JobId.Value);

        Assert.True((await store.GpuTaskHandoffAsync(request, CancellationToken.None)).Committed);
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await store.GpuTaskHandoffAsync(
                request with { IdempotencyKey = $"{request.IdempotencyKey} " },
                CancellationToken.None));

        await using var verification = await factory.CreateDbContextAsync();
        Assert.Equal(1, await verification.GpuMiniTasks.CountAsync());
        Assert.Equal(1, await verification.GpuSchedulerStates.Select(state => state.WakeGeneration).SingleAsync());
    }

    private async Task<(IDbContextFactory<FluxKnowledgeDbContext> Factory, ClaimedJob Claim, GpuMiniTaskHandoffRequest Request)>
        CreateClaimedRequestAsync(string idempotencyKey)
    {
        await SqlTestData.ClearPipelineAsync(_fixture);
        var now = DateTimeOffset.Parse("2026-07-29T09:00:00+00:00");
        await SqlTestData.SeedWorkItemAsync(
            _fixture,
            now,
            PublicJobState.WorkerQueued,
            leaseExpiresAtUtc: null);
        var factory = SqlTestData.CreateFactory(_fixture);
        var claim = await new SqlJobClaimStore(factory).ClaimNextDueAsync(
            "gpu-handoff-worker",
            now,
            TimeSpan.FromMinutes(5),
            CancellationToken.None);
        Assert.NotNull(claim);
        return (
            factory,
            claim,
            new GpuMiniTaskHandoffRequest(
                claim,
                GpuPriorityLane.InteractiveRetrieval,
                "test-runtime",
                "test-settings",
                1024,
                idempotencyKey));
    }

    private static async Task AssertNoSchedulerMutationAsync(
        IDbContextFactory<FluxKnowledgeDbContext> factory,
        Guid parentJobId)
    {
        await using var verification = await factory.CreateDbContextAsync();
        Assert.Empty(await verification.GpuMiniTasks.ToListAsync());
        Assert.Equal(
            (int)PublicJobState.WorkerProcessing,
            await verification.Jobs.Where(job => job.Id == parentJobId).Select(job => job.PublicState).SingleAsync());
        var wake = await verification.GpuSchedulerStates.SingleAsync(state => state.Id == 1);
        Assert.Equal(0, wake.WakeGeneration);
        Assert.Equal(0, wake.PendingWakeReasons);
    }
}
