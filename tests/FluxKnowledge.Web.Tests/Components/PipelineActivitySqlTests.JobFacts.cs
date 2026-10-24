using System.Data;
using System.Data.Common;
using System.Text.Json;
using FluxKnowledge.Application.Sources;
using FluxKnowledge.Application.Workers;
using FluxKnowledge.Domain.Jobs;
using FluxKnowledge.Domain.Pipeline;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using FluxKnowledge.Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace FluxKnowledge.Web.Tests.Components;

public sealed partial class PipelineActivitySqlTests
{
    [NativeSqlServerFact]
    public async Task Snapshot_job_facts_preserve_complete_rows_and_cannot_mix_a_later_committed_job_change()
    {
        await EnableOwnedSnapshotAsync();
        var now = DateTimeOffset.UtcNow;
        var seed = await SeedGpuReadLockAsync(now);
        var queued = await SeedActivityJobAsync(seed.Root, now, PublicJobState.WorkerQueued, null);
        await SeedActivityJobAsync(seed.Root, now, PublicJobState.Failed, null, suppressed: true);
        await SeedActivityJobAsync(seed.Root, now, PublicJobState.Completed, null);
        await using var context = new TestDbContextFactory(_fixture.ConnectionString).CreateDbContext();
        var ingress = new SourceIdentityEntity { Id = Guid.NewGuid(), SourceKind = "synthetic ingress", StableKey = Guid.NewGuid().ToString("N"), CreatedAtUtc = now };
        var ingressRecord = new PipelineRecordEntity { Id = Guid.NewGuid(), SourceIdentityId = ingress.Id, Revision = 1,
            ContentHash = new string('a', 64), CurrentStage = (int)PipelineStage.Extract, RegisteredAtUtc = now };
        ingressRecord.RootLineageRecordId = ingressRecord.Id;
        context.SourceIdentities.Add(ingress); context.PipelineRecords.Add(ingressRecord);
        context.Jobs.Add(new() { Id = Guid.NewGuid(), PipelineRecordId = ingressRecord.Id, SourceRevision = 1,
            Stage = (int)PipelineStage.Extract, Operation = PipelineOperations.ExtractUtf8, PublicState = (int)PublicJobState.WorkerQueued, DueAtUtc = now });
        await context.SaveChangesAsync();
        var jobId = await context.Jobs.Where(j => j.PipelineRecordId == queued).Select(j => j.Id).SingleAsync();
        await context.Jobs.Where(j => j.Id == jobId).ExecuteUpdateAsync(s => s.SetProperty(j => j.Reason, new string('r', 512)));
        await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.Snapshot);
        await using var scope = await SqlSelectedBranchReadScope.CreateAsync(context, [], default);
        var admission = new DeploymentHoldAdmission(false, null);
        var evidence = await SqlGpuActivityReadEvidence.ReadAsync(context, default);
        var original = await SqlPipelineActivityQuery.Jobs(context, now, admission, scope, evidence).OrderBy(j => j.Id).ToArrayAsync();
        var localIngress = Assert.Single(original, j => j.PipelineRecordId == ingressRecord.Id);
        Assert.Null(localIngress.SourceRootId); Assert.Null(localIngress.LogicalSourceRevisionId);
        Assert.Equal("Local ingress (synthetic ingress)", localIngress.FullPath);
        await scope.MaterializeGpuFactsAsync(now, evidence, default);
        await scope.MaterializeJobFactsAsync(now, admission, evidence, default);
        await using (var writer = new TestDbContextFactory(_fixture.ConnectionString).CreateDbContext())
            await writer.Jobs.Where(j => j.Id == jobId).ExecuteUpdateAsync(s => s
                .SetProperty(j => j.PublicState, (int)PublicJobState.Failed).SetProperty(j => j.Reason, "committed-after-facts"));
        var captured = await SqlPipelineActivityQuery.Jobs(context, now, admission, scope, evidence).OrderBy(j => j.Id).ToArrayAsync();
        Assert.Equal(JsonSerializer.Serialize(original), JsonSerializer.Serialize(captured));
        Assert.Equal(new string('r', 512), Assert.Single(captured, j => j.Id == jobId).Reason);
        Assert.Throws<InvalidOperationException>(() => SqlPipelineActivityQuery.Jobs(context, now.AddTicks(1), admission, scope, evidence));
        Assert.Throws<InvalidOperationException>(() => SqlPipelineActivityQuery.Jobs(context, now, new(true, null), scope, evidence));
        Assert.Throws<InvalidOperationException>(() => SqlPipelineActivityQuery.Jobs(context, now, admission, scope, new("[]", 0)));
        await scope.CompleteAsync(default);
        Assert.Equal(0, await JobFactsTableExistsAsync(context));
        await transaction.RollbackAsync();
        Assert.Throws<InvalidOperationException>(() => SqlPipelineActivityQuery.Jobs(context, now, admission, scope, evidence));
        await using var fresh = new TestDbContextFactory(_fixture.ConnectionString).CreateDbContext();
        var changed = await fresh.Jobs.AsNoTracking().SingleAsync(j => j.Id == jobId);
        Assert.Equal((int)PublicJobState.Failed, changed.PublicState);
        Assert.Equal("committed-after-facts", changed.Reason);
    }

    [NativeSqlServerTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Job_facts_reject_GPU_facts_from_outside_the_current_snapshot(bool priorSnapshot)
    {
        await EnableOwnedSnapshotAsync();
        await using var context = new TestDbContextFactory(_fixture.ConnectionString).CreateDbContext();
        await using var scope = await SqlSelectedBranchReadScope.CreateAsync(context, [], default);
        var now = DateTimeOffset.UtcNow;
        var evidence = new SqlGpuActivityReadEvidence("[]", 0);
        if (priorSnapshot)
        {
            await using var previous = await context.Database.BeginTransactionAsync(IsolationLevel.Snapshot);
            await scope.MaterializeGpuFactsAsync(now, evidence, default);
            await previous.CommitAsync();
        }
        else await scope.MaterializeGpuFactsAsync(now, evidence, default);
        await using var current = await context.Database.BeginTransactionAsync(IsolationLevel.Snapshot);
        await Assert.ThrowsAsync<InvalidOperationException>(() => scope.MaterializeJobFactsAsync(now, new(false, null), evidence, default).AsTask());
        Assert.Equal(0, await JobFactsTableExistsAsync(context));
        await scope.CompleteAsync(default);
        await current.CommitAsync();
    }

    [NativeSqlServerTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Job_facts_require_an_actual_snapshot_even_when_GPU_facts_exist(bool readCommitted)
    {
        await using var context = new TestDbContextFactory(_fixture.ConnectionString).CreateDbContext();
        await using var transaction = readCommitted ? await context.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted) : null;
        await using var scope = await SqlSelectedBranchReadScope.CreateAsync(context, [], default);
        var now = DateTimeOffset.UtcNow;
        var evidence = new SqlGpuActivityReadEvidence("[]", 0);
        await scope.MaterializeGpuFactsAsync(now, evidence, default);
        await Assert.ThrowsAsync<InvalidOperationException>(() => scope.MaterializeJobFactsAsync(now, new(false, null), evidence, default).AsTask());
        Assert.Equal(0, await JobFactsTableExistsAsync(context));
        await scope.CompleteAsync(default);
    }

    [NativeSqlServerTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Failed_or_cancelled_job_facts_fail_closed_clean_up_and_allow_a_fresh_scope(bool cancel)
    {
        await EnableOwnedSnapshotAsync();
        using var stopping = new CancellationTokenSource();
        var fault = new JobFactsPreparationFailure(cancel, stopping);
        await using var context = new TestDbContextFactory(_fixture.ConnectionString, fault).CreateDbContext();
        await context.Database.OpenConnectionAsync();
        await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.Snapshot);
        var now = DateTimeOffset.UtcNow;
        var evidence = new SqlGpuActivityReadEvidence("[]", 0);
        await using (var scope = await SqlSelectedBranchReadScope.CreateAsync(context, [], default))
        {
            await scope.MaterializeGpuFactsAsync(now, evidence, default);
            var failure = await Record.ExceptionAsync(() => scope.MaterializeJobFactsAsync(now, new(false, null), evidence, stopping.Token).AsTask());
            if (cancel) Assert.IsAssignableFrom<OperationCanceledException>(failure);
            else Assert.IsType<InvalidOperationException>(failure);
            Assert.True(fault.Failed);
            Assert.Throws<InvalidOperationException>(() => SqlPipelineActivityQuery.Jobs(context, now, new(false, null), scope, evidence));
            await Assert.ThrowsAsync<InvalidOperationException>(() => scope.MaterializeJobFactsAsync(now, new(false, null), evidence, default).AsTask());
        }
        Assert.Equal(0, await JobFactsTableExistsAsync(context));
        Assert.Equal(0, await FactsTableExistsAsync(context));
        Assert.Equal(0, await context.Database.SqlQueryRaw<int>("SELECT CASE WHEN OBJECT_ID('tempdb..#FluxPipelineSelectedBranches') IS NULL THEN 0 ELSE 1 END AS [Value]").SingleAsync());
        await using (var retry = await SqlSelectedBranchReadScope.CreateAsync(context, [], default))
        {
            await retry.MaterializeGpuFactsAsync(now, evidence, default);
            await retry.MaterializeJobFactsAsync(now, new(false, null), evidence, default);
            _ = await SqlPipelineActivityQuery.Jobs(context, now, new(false, null), retry, evidence).ToArrayAsync();
            await retry.CompleteAsync(default);
        }
        Assert.Equal(0, await JobFactsTableExistsAsync(context));
        await transaction.RollbackAsync();
        await context.Database.CloseConnectionAsync();
    }

    private static Task<int> JobFactsTableExistsAsync(FluxKnowledgeDbContext context) => context.Database.SqlQueryRaw<int>(
        "SELECT CASE WHEN OBJECT_ID('tempdb..#FluxPipelineJobFacts') IS NULL THEN 0 ELSE 1 END AS [Value]").SingleAsync();

    private sealed class JobFactsPreparationFailure(bool cancel, CancellationTokenSource stopping) : DbCommandInterceptor
    {
        public bool Failed { get; private set; }
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (!Failed && command.CommandText.Contains("INSERT INTO #FluxPipelineJobFacts", StringComparison.Ordinal))
            {
                Failed = true;
                if (cancel) { stopping.Cancel(); cancellationToken.ThrowIfCancellationRequested(); }
                throw new InvalidOperationException("Injected job-fact preparation failure after CREATE.");
            }
            return ValueTask.FromResult(result);
        }
    }
}
