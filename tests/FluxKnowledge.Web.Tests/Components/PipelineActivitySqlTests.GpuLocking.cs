using System.Data.Common;
using System.Data;
using FluxKnowledge.Domain.Jobs;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using FluxKnowledge.Integration.Tests.Support;
using FluxKnowledge.Web.Components.Status;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace FluxKnowledge.Web.Tests.Components;

public sealed partial class PipelineActivitySqlTests
{
    [NativeSqlServerTheory]
    [InlineData("overview")]
    [InlineData("jobs")]
    [InlineData("gpu")]
    public async Task GPU_status_commands_read_mutable_GPU_tables_separately(string surface)
    {
        var now = DateTimeOffset.UtcNow;
        _ = await SeedGpuReadLockAsync(now);
        var commands = new SingleGpuTableReadInterceptor();
        await using var context = new TestDbContextFactory(_fixture.ConnectionString, commands).CreateDbContext();
        // Snapshot acceptance is covered separately. This contract inspects the
        // executed production statements even when a concurrent write would
        // correctly make the surrounding status sample unavailable.
        if (surface == "overview")
            _ = await new SqlPipelineActivityReader(null, new FixedTimeProvider(now)).ReadAsync(context, now, CancellationToken.None);
        else
        {
            var evidence = await SqlGpuActivityReadEvidence.ReadAsync(context, CancellationToken.None);
            await using var branches = await SqlSelectedBranchReadScope.CreateAsync(context, [], CancellationToken.None);
            if (surface == "jobs")
                _ = await SqlPipelineActivityQuery.Jobs(context, now, new(false, null), branches, evidence).ToArrayAsync();
            else
                _ = await SqlPipelineActivityQuery.GpuTasks(context, now, new(false, null), branches, evidence).ToArrayAsync();
            await branches.CompleteAsync(CancellationToken.None);
        }
        Assert.Equal(GpuTables.Order(), commands.Tables.Order());
    }

    [NativeSqlServerTheory]
    [InlineData("owner", true)]
    [InlineData("owner", false)]
    [InlineData("generation", true)]
    [InlineData("generation", false)]
    [InlineData("slot", true)]
    [InlineData("slot", false)]
    public async Task A_GPU_write_between_consumed_queries_discards_the_sample_and_preserves_exact_ownership(
        string change, bool commit)
    {
        await WaitForFullTextIdleAsync();
        var now = DateTimeOffset.UtcNow;
        var seed = await SeedGpuReadLockAsync(now);
        var interleaving = new AfterGpuQueryInterceptor(async () =>
        {
            await using var writer = new TestDbContextFactory(_fixture.ConnectionString).CreateDbContext();
            await using var transaction = await writer.Database.BeginTransactionAsync();
            if (change == "owner")
                await writer.GpuExecutorDispatches.Where(d => d.BatchId == seed.Batch)
                    .ExecuteUpdateAsync(update => update.SetProperty(d => d.OwnerKey, "READOWNER"));
            else if (change == "generation")
                await writer.GpuExecutorDispatches.Where(d => d.BatchId == seed.Batch)
                    .ExecuteUpdateAsync(update => update.SetProperty(d => d.AdmissionGeneration, 8));
            else
                await writer.GpuCapacitySlots.Where(s => s.SlotKey == seed.Slot)
                    .ExecuteUpdateAsync(update => update.SetProperty(s => s.ActiveBatchId, (Guid?)null));
            if (commit) await transaction.CommitAsync();
            else await transaction.RollbackAsync();
        });
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var context = new TestDbContextFactory(_fixture.ConnectionString, interleaving).CreateDbContext();
        var before = await SqlPipelineActivityQuery.ReadFenceAsync(context, deadline.Token);
        Assert.True(before.IsStable);
        _ = await new SqlPipelineActivityReader(null, new FixedTimeProvider(now)).ReadAsync(context, now, deadline.Token);
        var after = await SqlPipelineActivityQuery.ReadFenceAsync(context, deadline.Token);
        Assert.True(interleaving.MutatedAfterConsumption);
        Assert.False(before.Accepts(after));
        var trace = new ReadFenceTraceInterceptor();
        var fresh = await ActivityReader(new TestDbContextFactory(_fixture.ConnectionString, trace)).ReadOverviewAsync(deadline.Token);
        output.WriteLine("Fresh fence: " + string.Join("; ", trace.Samples));
        Assert.True(fresh.Activity.IsConsistent, string.Join("; ", trace.Samples));
        var view = PipelineActivityView.From(fresh.Activity, seed.Root);
        Assert.Equal(commit ? 0 : 1, view.ActiveGpuTasks);
        Assert.Equal(commit ? 1 : 0, view.UnconfirmedGpuTasks);
    }

    [NativeSqlServerFact]
    public async Task A_held_uncommitted_GPU_owner_change_is_never_reported_as_confirmed()
    {
        await WaitForFullTextIdleAsync();
        var seed = await SeedGpuReadLockAsync(DateTimeOffset.UtcNow);
        await using var writer = new TestDbContextFactory(_fixture.ConnectionString).CreateDbContext();
        await using var transaction = await writer.Database.BeginTransactionAsync();
        await writer.GpuExecutorDispatches.Where(d => d.BatchId == seed.Batch)
            .ExecuteUpdateAsync(update => update.SetProperty(d => d.OwnerKey, "UNCOMMITTEDOWNER"));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        try
        {
            var sample = await ActivityReader().ReadOverviewAsync(deadline.Token);
            Assert.False(sample.Activity.IsConsistent);
            Assert.False(sample.Activity.IsAvailable);
        }
        finally { await transaction.RollbackAsync(CancellationToken.None); }
        var fresh = await ActivityReader().ReadOverviewAsync(deadline.Token);
        Assert.True(fresh.Activity.IsConsistent);
        Assert.Equal(1, PipelineActivityView.From(fresh.Activity, seed.Root).ActiveGpuTasks);
    }

    private async Task WaitForFullTextIdleAsync()
    {
        // Migrations start asynchronous Full-Text populations. Those advance
        // @@DBTS even without application writes, so the single fresh sample
        // needs this fixture precondition as well as the awaited GPU mutation.
        // Keep indexing enabled and retain the production fence unchanged.
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var context = new TestDbContextFactory(_fixture.ConnectionString).CreateDbContext();
        context.Database.SetCommandTimeout(5);
        const string sql = """
            SELECT CASE WHEN COUNT(*) = 2
                AND SUM(CASE WHEN is_enabled = 1 AND change_tracking_state_desc = N'AUTO'
                    AND has_crawl_completed = 1
                    AND CONVERT(int, OBJECTPROPERTYEX(object_id, 'TableFulltextPopulateStatus')) = 0
                    AND CONVERT(int, OBJECTPROPERTYEX(object_id, 'TableFulltextPendingChanges')) = 0
                  THEN 1 ELSE 0 END) = 2
              THEN 1 ELSE 0 END AS [Value]
            FROM sys.fulltext_indexes
            WHERE object_id IN (OBJECT_ID(N'Artifacts'), OBJECT_ID(N'TextChunks'))
            """;
        try
        {
            while (await context.Database.SqlQueryRaw<int>(sql).SingleAsync(deadline.Token) != 1)
                await Task.Delay(100, deadline.Token);
        }
        catch (OperationCanceledException exception) when (deadline.IsCancellationRequested)
        {
            throw new TimeoutException("The owned test Full-Text indexes did not become idle before the GPU read test.", exception);
        }
    }

    private async Task<(Guid Root, Guid Batch, Guid Task, string Slot)> SeedGpuReadLockAsync(DateTimeOffset now)
    {
        var root = await AddActivityRootAsync(now);
        var work = await SeedActivityJobAsync(root, now, PublicJobState.GpuProcessing, null);
        await using var setup = new TestDbContextFactory(_fixture.ConnectionString).CreateDbContext();
        var job = await setup.Jobs.SingleAsync(j => j.PipelineRecordId == work);
        var batch = Guid.NewGuid();
        var task = Guid.NewGuid();
        var slot = $"activity-lock-{Guid.NewGuid():N}";
        setup.GpuCapacitySlots.Add(new GpuCapacitySlotEntity
            { SlotKey = slot, State = 1, OwnerKey = "ReadOwner", UpdatedAtUtc = now });
        await setup.SaveChangesAsync();
        setup.GpuBatches.Add(new GpuBatchEntity { Id = batch, CapacitySlotKey = slot, OwnerKey = "ReadOwner",
            State = 0, AdmissionGeneration = 7, ModelRuntimeKey = "synthetic", SettingsFingerprint = "synthetic",
            ItemCount = 1, EstimatedBytes = 1, CreatedAtUtc = now, UpdatedAtUtc = now });
        await setup.SaveChangesAsync();
        (await setup.GpuCapacitySlots.SingleAsync(s => s.SlotKey == slot)).ActiveBatchId = batch;
        setup.GpuExecutorDispatches.Add(new GpuExecutorDispatchEntity { DispatchId = Guid.NewGuid(), BatchId = batch,
            CapacitySlotKey = slot, OwnerKey = "ReadOwner", ExecutorKey = "synthetic-executor", AdmissionGeneration = 7,
            State = 1, AcknowledgedAtUtc = now, UpdatedAtUtc = now });
        setup.GpuMiniTasks.Add(new GpuMiniTaskEntity { Id = task, ParentJobId = job.Id, SourceRevision = 1,
            ModelRuntimeKey = "synthetic", SettingsFingerprint = "synthetic", IdempotencyKey = $"activity-lock:{task:N}",
            EstimatedBytes = 1, AdmissionGeneration = 7, ExecutionState = 1, BatchId = batch, CreatedAtUtc = now });
        await setup.SaveChangesAsync();
        return (root, batch, task, slot);
    }

    private static readonly string[] GpuTables = ["GpuMiniTasks", "GpuBatches", "GpuCapacitySlots", "GpuExecutorDispatches"];

    private sealed class ReadFenceTraceInterceptor : DbCommandInterceptor
    {
        public List<string> Samples { get; } = [];
        public override async ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData,
            DbDataReader result, CancellationToken cancellationToken = default)
        {
            if (!command.CommandText.Contains("MIN_ACTIVE_ROWVERSION", StringComparison.Ordinal)) return result;
            var copy = new DataTable();
            copy.Load(result);
            await result.DisposeAsync();
            // Keep the actual typed fence result for any future failure, without
            // substituting a later observation or issuing another SQL query.
            Samples.Add($"database={command.Connection!.Database}, connection={((Microsoft.Data.SqlClient.SqlConnection)command.Connection).ClientConnectionId}, " +
                $"version={copy.Rows[0]["Version"]}, stable={copy.Rows[0]["IsStable"]}");
            return copy.CreateDataReader();
        }
    }

    // Captured production deadlocks joined these tables in the opposite order
    // from scheduler writes. Inspect executed SQL, including nested EXISTS,
    // without relying on the optimiser choosing a particular blocking plan.
    private sealed class SingleGpuTableReadInterceptor : DbCommandInterceptor
    {
        public HashSet<string> Tables { get; } = [];
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            var tables = GpuTables.Where(t => command.CommandText.Contains(t, StringComparison.OrdinalIgnoreCase)).ToArray();
            Assert.True(tables.Length <= 1, $"Status SELECT acquired multiple mutable GPU tables: {string.Join(", ", tables)}.");
            Tables.UnionWith(tables);
            return ValueTask.FromResult(result);
        }
    }

    private sealed class AfterGpuQueryInterceptor(Func<Task> mutate) : DbCommandInterceptor
    {
        private bool _consumed;
        public bool MutatedAfterConsumption { get; private set; }
        public override InterceptionResult DataReaderDisposing(DbCommand command, DataReaderDisposingEventData eventData,
            InterceptionResult result)
        {
            if (GpuTables.Any(t => command.CommandText.Contains(t, StringComparison.OrdinalIgnoreCase)))
                _consumed = true;
            return result;
        }

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (_consumed && !MutatedAfterConsumption)
            {
                await mutate();
                MutatedAfterConsumption = true;
            }
            return result;
        }
    }
}
