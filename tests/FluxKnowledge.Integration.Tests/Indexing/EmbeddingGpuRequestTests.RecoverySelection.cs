using System.Data.Common;
using System.Text.Json;
using System.Text.RegularExpressions;
using FluxKnowledge.Application.Gpu;
using FluxKnowledge.Domain.Gpu;
using FluxKnowledge.Domain.Jobs;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using FluxKnowledge.Infrastructure.SqlServer.Workers;
using FluxKnowledge.Integration.Tests.Support;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Indexing;

public sealed partial class EmbeddingGpuRequestTests
{
    [NativeSqlServerFact]
    public async Task Recovery_discovery_bounds_reads_amid_settled_history_and_orders_both_branches_under_one_limit()
    {
        await using var environment = await PrepareRetainedEnvironmentAsync();
        var (requests, handle, work, instance) = await PrepareOwnedAsync(environment);
        await requests.CommitAsync(handle, instance, work, Outputs(work.Batch.Chunks.Count), default);
        await requests.RecordNativeCleanupAsync(handle, instance, work.ClaimOperationId, default);
        await DeliverCleanedAsync(environment, requests, handle);
        await SeedSettledRecoveryHistoryAsync(environment, work.MiniTaskId, 6000);

        await using (var db = environment.Factory.CreateDbContext())
            await db.Jobs.Where(value => value.Id == work.ParentJobId)
                .ExecuteUpdateAsync(set => set.SetProperty(value => value.PublicState, (int)PublicJobState.Completed));

        // Real checkpoint and lifecycle helpers establish the eligible settled attempt.
        await environment.AddRetainedAndPumpAsync("A current legacy checkpoint. " + Text + Text);
        var (_, settledHandle, settledWork, settledInstance) = await PrepareOwnedAsync(environment);
        await requests.CommitAsync(settledHandle, settledInstance, settledWork, Outputs(settledWork.Batch.Chunks.Count), default);
        await requests.RecordNativeCleanupAsync(settledHandle, settledInstance, settledWork.ClaimOperationId, default);
        await DeliverCleanedAsync(environment, requests, settledHandle);
        await using (var db = environment.Factory.CreateDbContext())
            await db.Jobs.Where(value => value.Id == settledWork.ParentJobId)
                .ExecuteUpdateAsync(set => set.SetProperty(value => value.PublicState, (int)PublicJobState.GpuProcessing));

        await environment.AddRetainedAndPumpAsync("An independent current checkpoint remains unfinished. " + Text);
        var (_, pendingHandle, pendingWork, _) = await PrepareOwnedAsync(environment);
        var earlier = DateTimeOffset.UtcNow.AddMinutes(-1);
        await using (var db = environment.Factory.CreateDbContext())
        {
            await db.EmbeddingGpuRequests.Where(value => value.MiniTaskId == settledWork.MiniTaskId)
                .ExecuteUpdateAsync(set => set.SetProperty(value => value.UpdatedAtUtc, earlier));
            await db.EmbeddingGpuRequests.Where(value => value.MiniTaskId == pendingWork.MiniTaskId)
                .ExecuteUpdateAsync(set => set.SetProperty(value => value.UpdatedAtUtc, earlier.AddSeconds(1)));
        }

        await using var connection = new SqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        var messages = new List<string>();
        connection.InfoMessage += (_, message) => messages.Add(message.Message);
        await using (var setup = connection.CreateCommand())
        { setup.CommandText = "SET STATISTICS IO ON;"; await setup.ExecuteNonQueryAsync(); }
        var observer = new RecoverySelectionObserver();
        var factory = new RecoverySelectionFactory(new DbContextOptionsBuilder<FluxKnowledgeDbContext>()
            .UseSqlServer(connection, sql => sql.CommandTimeout(10)).AddInterceptors(observer).Options);
        var admission = new FixtureSourceAdmission(factory);
        var hold = new RecoveryHold { Current = new(false, null) };
        var reader = new SqlEmbeddingGpuRequestStore(factory, new SqlGpuSchedulerStore(factory, sourceAdmission: admission),
            new ChannelGpuSchedulerWakeSignal(), Runtime, TimeProvider.System, hold, admission);
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var found = await reader.ReadRecoveryAsync(budget.Token);
        // Consume every result set of the exact generated selector so SQL IO
        // messages following the row set are included in the work measurement.
        messages.Clear();
        await using (var measured = connection.CreateCommand())
        {
            measured.CommandText = "SET STATISTICS IO ON;\n" + observer.Sql;
            measured.CommandTimeout = 10;
            foreach (var parameter in observer.Parameters) measured.Parameters.Add(parameter);
            var ids = new List<Guid>();
            await using var rows = await measured.ExecuteReaderAsync(budget.Token);
            do { while (await rows.ReadAsync(budget.Token)) ids.Add(rows.GetGuid(0)); }
            while (await rows.NextResultAsync(budget.Token));
            Assert.Equal(found.Select(value => value.MiniTaskId), ids);
        }
        var reads = messages.SelectMany(message => Regex.Matches(message, @"(?<!lob )logical reads (\d+),"))
            .Sum(match => long.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture));
        output.WriteLine("RECOVERY COMMAND:\n" + observer.Command);
        output.WriteLine("RECOVERY IO:\n" + string.Join('\n', messages));
        Assert.Equal(new[] { settledWork.MiniTaskId, pendingWork.MiniTaskId }, found.Select(value => value.MiniTaskId));
        Assert.Equal(new[] { settledHandle, pendingHandle }, found.Select(value => value.Handle));
        Assert.InRange(reads, 1, 8192);
        Assert.Contains("UNION ALL", observer.Sql);
        Assert.Single(Regex.Matches(observer.Sql, @"\bTOP\s*\("));
        Assert.Single(Regex.Matches(observer.Sql, @"\bORDER BY\b"));

        hold.Current = new(true, null);
        var held = await reader.ReadRecoveryAsync(budget.Token);
        Assert.Equal(pendingWork.MiniTaskId, Assert.Single(held).MiniTaskId);
        Assert.Equal(pendingHandle, held[0].Handle);
        Assert.DoesNotContain("UNION ALL", observer.Sql);
    }

    private static async Task SeedSettledRecoveryHistoryAsync(SqlToUsearchRebuildTests.PipelineEnvironment environment,
        Guid prototypeId, int count)
    {
        await using var db = environment.Factory.CreateDbContext();
        var prototype = await db.EmbeddingGpuRequests.AsNoTracking().SingleAsync(value => value.MiniTaskId == prototypeId);
        var task = await db.GpuMiniTasks.AsNoTracking().SingleAsync(value => value.Id == prototypeId);
        var batch = await db.GpuBatches.AsNoTracking().SingleAsync(value => value.Id == task.BatchId);
        var dispatch = await db.GpuExecutorDispatches.AsNoTracking().SingleAsync(value => value.BatchId == batch.Id);
        Assert.Equal(2, prototype.State);
        Assert.True(prototype.NativeCleanupConfirmed);
        Assert.Equal((int)GpuExecutorDispatchState.Terminal, dispatch.State);
        for (var index = 0; index < count; index++)
        {
            var oldBatch = (GpuBatchEntity)db.Entry(batch).CurrentValues.ToObject(); oldBatch.Id = Guid.NewGuid();
            var oldTask = (GpuMiniTaskEntity)db.Entry(task).CurrentValues.ToObject(); oldTask.Id = Guid.NewGuid();
            oldTask.BatchId = oldBatch.Id; oldTask.CreatedSequence = 0; oldTask.IdempotencyKey = "recovery-history:" + oldTask.Id.ToString("N");
            var oldDispatch = (GpuExecutorDispatchEntity)db.Entry(dispatch).CurrentValues.ToObject();
            oldDispatch.DispatchId = Guid.NewGuid(); oldDispatch.BatchId = oldBatch.Id;
            var oldRequest = (EmbeddingGpuRequestEntity)db.Entry(prototype).CurrentValues.ToObject();
            oldRequest.MiniTaskId = oldTask.Id; oldRequest.DispatchId = oldDispatch.DispatchId; oldRequest.InputDigest = index.ToString("x64");
            db.AddRange(oldBatch, oldTask, oldDispatch, oldRequest);
        }
        await db.SaveChangesAsync();
    }

    private sealed class RecoverySelectionFactory(DbContextOptions<FluxKnowledgeDbContext> options) : IDbContextFactory<FluxKnowledgeDbContext>
    {
        public FluxKnowledgeDbContext CreateDbContext() => new(options);
    }

    private sealed class RecoverySelectionObserver : DbCommandInterceptor
    {
        public string Sql { get; private set; } = "";
        public string Command { get; private set; } = "";
        public SqlParameter[] Parameters { get; private set; } = [];
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Sql = command.CommandText;
            Parameters = command.Parameters.Cast<SqlParameter>().Select(value => (SqlParameter)((ICloneable)value).Clone()).ToArray();
            Command = JsonSerializer.Serialize(new { Sql, Parameters = command.Parameters.Cast<DbParameter>()
                .Select(value => new { value.ParameterName, Type = value.DbType.ToString(), value.Size, value.Value }).ToArray() });
            return ValueTask.FromResult(result);
        }
    }
}
