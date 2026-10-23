using System.Data;
using System.Data.Common;
using System.Text.Json;
using FluxKnowledge.Domain.Jobs;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using FluxKnowledge.Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace FluxKnowledge.Web.Tests.Components;

public sealed partial class PipelineActivitySqlTests
{
    [NativeSqlServerTheory]
    [InlineData("valid")]
    [InlineData("wrong-task")]
    [InlineData("wrong-batch")]
    [InlineData("wrong-generation")]
    [InlineData("empty")]
    public async Task Materialized_GPU_facts_match_all_original_job_fields_and_independent_predicates(string proofKind)
    {
        await EnableOwnedSnapshotAsync();
        var now = DateTimeOffset.UtcNow;
        var seed = await SeedGpuReadLockAsync(now);
        await using var context = new TestDbContextFactory(_fixture.ConnectionString).CreateDbContext();
        var ownedTask = await context.GpuMiniTasks.SingleAsync(t => t.Id == seed.Task);
        var parentJob = ownedTask.ParentJobId!.Value;
        var noTasksRecord = await SeedActivityJobAsync(seed.Root, now, PublicJobState.GpuQueued, null);
        var boundaryRecord = await SeedActivityJobAsync(seed.Root, now, PublicJobState.GpuQueued, null);
        var boundaryJob = await context.Jobs.SingleAsync(j => j.PipelineRecordId == boundaryRecord);
        var futureRecord = await SeedActivityJobAsync(seed.Root, now, PublicJobState.GpuQueued, null);
        var futureJob = await context.Jobs.SingleAsync(j => j.PipelineRecordId == futureRecord);
        context.GpuMiniTasks.AddRange(Task(parentJob, 0), Task(parentJob, 3), Task(parentJob, 0, now.AddTicks(1)),
            Task(boundaryJob.Id, 0, now), Task(futureJob.Id, 0, now.AddTicks(1)), Task(null, 3),
            Task(parentJob, 2), Task(parentJob, 4));
        await context.SaveChangesAsync();
        var proof = new { Id = proofKind == "wrong-task" ? Guid.NewGuid() : seed.Task,
            BatchId = proofKind == "wrong-batch" ? Guid.NewGuid() : seed.Batch,
            AdmissionGeneration = proofKind == "wrong-generation" ? 8L : 7L, HasActive = true, HasPending = true };
        var evidence = new SqlGpuActivityReadEvidence(proofKind == "empty" ? "[]" : JsonSerializer.Serialize(new[] { proof }), 0);
        await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.Snapshot);
        await using var scope = await SqlSelectedBranchReadScope.CreateAsync(context, [], default);
        var original = await SqlPipelineActivityQuery.Jobs(context, now, new(false, null), scope, evidence).OrderBy(j => j.Id).ToArrayAsync();
        await scope.MaterializeGpuFactsAsync(now, evidence, default);
        var materialized = await SqlPipelineActivityQuery.Jobs(context, now, new(false, null), scope, evidence).OrderBy(j => j.Id).ToArrayAsync();
        Assert.Equal(JsonSerializer.Serialize(original), JsonSerializer.Serialize(materialized));
        var parent = Assert.Single(materialized, j => j.Id == parentJob);
        Assert.Equal(proofKind == "valid", parent.HasActiveGpu);
        Assert.True(parent.HasReadyGpu);
        Assert.True(parent.HasUncertainGpu);
        Assert.True(parent.HasWaitingGpu);
        var empty = Assert.Single(materialized, j => j.PipelineRecordId == noTasksRecord);
        Assert.False(empty.HasActiveGpu || empty.HasReadyGpu || empty.HasUncertainGpu || empty.HasWaitingGpu);
        var boundary = Assert.Single(materialized, j => j.Id == boundaryJob.Id);
        Assert.True(boundary.HasReadyGpu);
        Assert.False(boundary.HasWaitingGpu);
        var future = Assert.Single(materialized, j => j.Id == futureJob.Id);
        Assert.False(future.HasReadyGpu);
        Assert.True(future.HasWaitingGpu);
        // A revision mismatch must never attach facts to another job revision.
        await context.Database.ExecuteSqlRawAsync("UPDATE #FluxPipelineGpuFacts SET SourceRevision = SourceRevision + 1;");
        var mismatched = await SqlPipelineActivityQuery.Jobs(context, now, new(false, null), scope, evidence)
            .Where(j => j.Id == parentJob).SingleAsync();
        Assert.False(mismatched.HasActiveGpu || mismatched.HasReadyGpu || mismatched.HasUncertainGpu || mismatched.HasWaitingGpu);
        await scope.CompleteAsync(default);
        Assert.Equal(0, await FactsTableExistsAsync(context));
        await transaction.RollbackAsync();

        GpuMiniTaskEntity Task(Guid? parent, int state, DateTimeOffset? deferred = null) => new()
        {
            Id = Guid.NewGuid(), ParentJobId = parent, InteractiveExecutorInstanceId = parent is null ? Guid.NewGuid() : null,
            SourceRevision = parent is null ? 0 : 1, ModelRuntimeKey = "synthetic",
            RequiredExecutorKey = parent is null ? "synthetic-executor" : null,
            QueueDeadlineUtc = parent is null ? now.AddMinutes(1) : null,
            ExecutionDeadlineUtc = parent is null ? now.AddMinutes(2) : null,
            InteractiveOwnerProcessId = parent is null ? 123 : null,
            InteractiveOwnerStartedAtUtc = parent is null ? now : null,
            InteractiveOwnerMachineFingerprint = parent is null ? new string('a', 64) : null,
            SettingsFingerprint = "synthetic", IdempotencyKey = $"gpu-facts:{Guid.NewGuid():N}", EstimatedBytes = 1,
            AdmissionGeneration = 7, ExecutionState = state, DeferredUntilUtc = deferred, CreatedAtUtc = now
        };
    }

    [NativeSqlServerFact]
    public async Task Failed_GPU_fact_preparation_cleans_both_tables_and_can_retry_on_the_same_connection()
    {
        var failure = new GpuFactsPreparationFailure();
        await using var context = new TestDbContextFactory(_fixture.ConnectionString, failure).CreateDbContext();
        await context.Database.OpenConnectionAsync();
        await using (var scope = await SqlSelectedBranchReadScope.CreateAsync(context, [], default))
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => scope.MaterializeGpuFactsAsync(
                DateTimeOffset.UtcNow, new("[]", 0), default).AsTask());
        }
        Assert.True(failure.Failed);
        Assert.Equal(0, await FactsTableExistsAsync(context));
        await using (var retry = await SqlSelectedBranchReadScope.CreateAsync(context, [], default))
        {
            await retry.MaterializeGpuFactsAsync(DateTimeOffset.UtcNow, new("[]", 0), default);
            await Assert.ThrowsAsync<InvalidOperationException>(() => retry.MaterializeGpuFactsAsync(
                DateTimeOffset.UtcNow, new("[]", 0), default).AsTask());
            await retry.CompleteAsync(default);
        }
        Assert.Equal(0, await FactsTableExistsAsync(context));
        await context.Database.CloseConnectionAsync();
    }

    private static Task<int> FactsTableExistsAsync(FluxKnowledgeDbContext context) => context.Database.SqlQueryRaw<int>(
        "SELECT CASE WHEN OBJECT_ID('tempdb..#FluxPipelineGpuFacts') IS NULL THEN 0 ELSE 1 END AS [Value]").SingleAsync();

    private sealed class GpuFactsPreparationFailure : DbCommandInterceptor
    {
        public bool Failed { get; private set; }
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (!Failed && command.CommandText.Contains("INSERT INTO #FluxPipelineGpuFacts", StringComparison.Ordinal))
            {
                Failed = true;
                throw new InvalidOperationException("Injected materialisation failure after CREATE.");
            }
            return ValueTask.FromResult(result);
        }
    }
}
