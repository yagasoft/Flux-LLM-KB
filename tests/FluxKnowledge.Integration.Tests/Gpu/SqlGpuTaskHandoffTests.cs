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
using Microsoft.Data.SqlClient;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Gpu;

public sealed class SqlGpuTaskHandoffTests(NativeSqlServerFixture fixture)
    : IClassFixture<NativeSqlServerFixture>
{
    private readonly NativeSqlServerFixture _fixture = fixture;

    [NativeSqlServerFact]
    public async Task Recovery_parent_read_arriving_first_can_read_children_and_release_before_handoff_inserts_them()
    {
        var (factory, claim, request) = await CreateClaimedRequestAsync("handoff:recovery-arrives-first");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var inserted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new SqlGpuSchedulerStore(factory, afterMiniTaskPersisted: async ct =>
        {
            inserted.TrySetResult();
            await release.Task.WaitAsync(ct);
        });
        await using var reader = new SqlConnection(_fixture.ConnectionString);
        await reader.OpenAsync(timeout.Token);
        await using var transaction = (SqlTransaction)await reader.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, timeout.Token);
        await using (var parent = new SqlCommand("SELECT Id FROM Jobs WHERE Id=@id", reader, transaction))
        {
            parent.Parameters.AddWithValue("@id", claim.JobId.Value);
            Assert.Equal(claim.JobId.Value, await parent.ExecuteScalarAsync(timeout.Token));
        }
        var handoff = store.GpuTaskHandoffAsync(request, timeout.Token).AsTask();
        try
        {
            // Observe the actual lock wait, rather than relying on a scheduler sleep.
            // Old code reaches the inserted-child callback with only a shared parent
            // lock, establishing the captured S-parent / X-child inversion.
            await using var observer = new SqlConnection(_fixture.ConnectionString);
            await observer.OpenAsync(timeout.Token);
            while (!inserted.Task.IsCompleted)
            {
                await using var waiting = new SqlCommand("SELECT COUNT(*) FROM sys.dm_exec_requests WHERE blocking_session_id=@reader", observer);
                waiting.Parameters.AddWithValue("@reader", reader.ServerProcessId);
                if ((int)(await waiting.ExecuteScalarAsync(timeout.Token))! > 0) break;
                if (handoff.IsCompleted) await handoff;
                await Task.Delay(20, timeout.Token);
            }
            await using var children = new SqlCommand("SELECT COUNT(*) FROM GpuMiniTasks WHERE ParentJobId=@id", reader, transaction);
            children.Parameters.AddWithValue("@id", claim.JobId.Value);
            var childRead = children.ExecuteScalarAsync(timeout.Token);
            if (inserted.Task.IsCompleted)
            {
                // Complete the old cycle deterministically: reader waits for child,
                // then writer attempts the parent S->X conversion.
                while (!childRead.IsCompleted)
                {
                    await using var waiting = new SqlCommand("SELECT COUNT(*) FROM sys.dm_exec_requests WHERE session_id=@reader AND blocking_session_id<>0", observer);
                    waiting.Parameters.AddWithValue("@reader", reader.ServerProcessId);
                    if ((int)(await waiting.ExecuteScalarAsync(timeout.Token))! > 0) break;
                    await Task.Delay(20, timeout.Token);
                }
            }
            release.TrySetResult();
            Assert.Equal(0, await childRead);
            await transaction.CommitAsync(timeout.Token);
            Assert.True((await handoff).Committed);
        }
        finally
        {
            release.TrySetResult();
            await timeout.CancelAsync();
            try { await transaction.RollbackAsync(); } catch (InvalidOperationException) { }
            try { await handoff; } catch (Exception) when (timeout.IsCancellationRequested) { }
        }
        Assert.True((await store.GpuTaskHandoffAsync(request, CancellationToken.None)).IsIdempotentReplay);
        await using var verification = await factory.CreateDbContextAsync();
        Assert.Single(await verification.GpuMiniTasks.ToArrayAsync());
        Assert.Equal((int)PublicJobState.GpuQueued, (await verification.Jobs.SingleAsync(value => value.Id == claim.JobId.Value)).PublicState);
    }

    [NativeSqlServerFact]
    public async Task Handoff_excludes_parent_recovery_reads_before_inserting_a_child_without_changing_replay()
    {
        var (factory, claim, request) = await CreateClaimedRequestAsync("handoff:parent-before-child");
        var inserted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var store = new SqlGpuSchedulerStore(factory, afterMiniTaskPersisted: async ct =>
        {
            inserted.TrySetResult();
            await release.Task.WaitAsync(ct);
        });
        var handoff = store.GpuTaskHandoffAsync(request, timeout.Token).AsTask();
        try
        {
            await inserted.Task.WaitAsync(timeout.Token);
            await using var reader = new SqlConnection(_fixture.ConnectionString);
            await reader.OpenAsync(timeout.Token);
            await using var transaction = await reader.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, timeout.Token);
            await using var command = new SqlCommand("SET LOCK_TIMEOUT 1000; SELECT Id FROM Jobs WHERE Id=@id;", reader, (SqlTransaction)transaction);
            command.Parameters.AddWithValue("@id", claim.JobId.Value);
            var blocked = await Assert.ThrowsAsync<SqlException>(() => command.ExecuteScalarAsync(timeout.Token));
            Assert.Equal(1222, blocked.Number);
        }
        finally { release.TrySetResult(); }
        Assert.True((await handoff).Committed);
        var replay = await store.GpuTaskHandoffAsync(request, timeout.Token);
        Assert.True(replay.IsIdempotentReplay);
        await using var verification = await factory.CreateDbContextAsync(timeout.Token);
        Assert.Single(await verification.GpuMiniTasks.ToArrayAsync(timeout.Token));
        Assert.Equal((int)PublicJobState.GpuQueued,
            await verification.Jobs.Where(job => job.Id == claim.JobId.Value).Select(job => job.PublicState).SingleAsync(timeout.Token));
    }

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
    public async Task Handoff_uses_the_source_bound_mini_task_id_and_replay_cannot_substitute_it()
    {
        var (factory, _, request) = await CreateClaimedRequestAsync("handoff:source-bound-id");
        var expectedMiniTaskId = Guid.NewGuid();
        var store = new SqlGpuSchedulerStore(factory);

        var first = await store.GpuTaskHandoffAsync(
            request with { MiniTaskId = expectedMiniTaskId },
            CancellationToken.None);

        Assert.True(first.Committed);
        Assert.Equal(expectedMiniTaskId, first.MiniTaskId);

        var replay = await store.GpuTaskHandoffAsync(
            request with { MiniTaskId = expectedMiniTaskId },
            CancellationToken.None);

        Assert.True(replay.IsIdempotentReplay);
        Assert.Equal(expectedMiniTaskId, replay.MiniTaskId);
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await store.GpuTaskHandoffAsync(
                request with { MiniTaskId = Guid.NewGuid() },
                CancellationToken.None));
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
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var idempotencyDecisionCount = 0;
        var persistedCount = 0;
        var store = new SqlGpuSchedulerStore(
            factory,
            afterMiniTaskPersisted: async cancellationToken =>
            {
                if (Interlocked.Increment(ref persistedCount) == 1)
                {
                    firstMiniTaskPersisted.SetResult();
                    await releaseFirstTransaction.Task.WaitAsync(cancellationToken);
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

        var first = store.GpuTaskHandoffAsync(request, timeout.Token).AsTask();
        Task<GpuMiniTaskHandoffResult>? second = null;
        GpuMiniTaskHandoffResult[] results;
        Exception? originalFailure = null;
        try
        {
            await firstMiniTaskPersisted.Task.WaitAsync(timeout.Token);
            second = store.GpuTaskHandoffAsync(request, timeout.Token).AsTask();
            await secondAtIdempotencyDecision.Task.WaitAsync(timeout.Token);
            releaseFirstTransaction.TrySetResult();
            results = await Task.WhenAll(first, second).WaitAsync(timeout.Token);
        }
        catch (Exception exception)
        {
            originalFailure = exception;
            throw;
        }
        finally
        {
            releaseFirstTransaction.TrySetResult();
            timeout.Cancel();
            Task pending = second is null ? first : Task.WhenAll(first, second);
            try
            {
                await pending.WaitAsync(TimeSpan.FromSeconds(30));
            }
            catch (Exception) when (pending.IsCompleted)
            {
                // The original result wait reports a fault or cancellation after both handoffs settle.
            }
            catch (TimeoutException cleanupTimeout) when (originalFailure is not null)
            {
                throw new AggregateException(originalFailure, cleanupTimeout);
            }
        }

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
