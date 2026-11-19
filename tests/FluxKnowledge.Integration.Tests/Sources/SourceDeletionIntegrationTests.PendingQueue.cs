using System.Data.Common;
using System.Globalization;
using System.Text.RegularExpressions;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Integration.Tests.Support;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Sources;

public sealed partial class SourceDeletionIntegrationTests
{
    [NativeSqlServerFact]
    public async Task Operational_queue_index_is_idempotent_preserves_history_and_refuses_a_wrong_definition()
    {
        await using var context = CreateContext();
        var before = await context.Database.SqlQueryRaw<string>("SELECT MigrationId AS [Value] FROM __EFMigrationsHistory ORDER BY MigrationId").ToArrayAsync();
        try
        {
            await context.Database.ExecuteSqlRawAsync("DROP INDEX IX_SourceDeletionCleanupItems_PendingQueue ON dbo.SourceDeletionCleanupItems;");
            await SqlSourceDeletionPendingQueueIndex.EnsureAsync(context, default);
            await SqlSourceDeletionPendingQueueIndex.EnsureAsync(context, default);
            var after = await context.Database.SqlQueryRaw<string>("SELECT MigrationId AS [Value] FROM __EFMigrationsHistory ORDER BY MigrationId").ToArrayAsync();
            Assert.Equal(before, after);
            await context.Database.ExecuteSqlRawAsync("""
                DROP INDEX IX_SourceDeletionCleanupItems_PendingQueue ON dbo.SourceDeletionCleanupItems;
                CREATE INDEX IX_SourceDeletionCleanupItems_PendingQueue ON dbo.SourceDeletionCleanupItems (State);
                """);
            var failure = await Assert.ThrowsAsync<SqlException>(() => SqlSourceDeletionPendingQueueIndex.EnsureAsync(context, default));
            Assert.Contains("source-deletion-pending-queue-index-definition-invalid", failure.Message);
        }
        finally
        {
            await context.Database.ExecuteSqlRawAsync("DROP INDEX IF EXISTS IX_SourceDeletionCleanupItems_PendingQueue ON dbo.SourceDeletionCleanupItems;");
            await SqlSourceDeletionPendingQueueIndex.EnsureAsync(context, default);
        }
    }

    [NativeSqlServerFact]
    public async Task Pending_cleanup_selection_seeks_past_35000_completed_receipts_without_scanning_them()
    {
        var now = DateTimeOffset.UtcNow;
        var source = await SeedRootAsync("cleanup-queue-tail", SourceRootState.Deleting, now);
        var operationId = await AddDeletionAsync(source, now);
        var pendingId = Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff");
        await using (var seed = CreateContext())
        {
            await seed.SourceDeletionOperations.Where(value => value.Id == operationId)
                .ExecuteUpdateAsync(set => set.SetProperty(value => value.Phase, "cleanup-files"));
            await seed.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT SourceDeletionCleanupItems (Id,SourceDeletionOperationId,StorageKind,RelativePath,ContentSha256,ByteLength,State,CreatedAtUtc,UpdatedAtUtc)
                SELECT TOP(35000) NEWID(),{operationId},1,CONCAT('completed/',ROW_NUMBER() OVER(ORDER BY (SELECT NULL))),
                    {new string('a',64)},4,1,{now},{now} FROM sys.all_objects a CROSS JOIN sys.all_objects b;
                INSERT SourceDeletionCleanupItems (Id,SourceDeletionOperationId,StorageKind,RelativePath,ContentSha256,ByteLength,State,CreatedAtUtc,UpdatedAtUtc)
                VALUES({pendingId},{operationId},1,'zzzz-pending',{new string('b',64)},4,0,{now},{now});
                """);
        }
        var capture = new PendingQueueCommand();
        var factory = new RootDeletionScaleFactory(new DbContextOptionsBuilder<FluxKnowledgeDbContext>()
            .UseSqlServer(_fixture.ConnectionString).AddInterceptors(capture).Options);
        var store = new SqlSourceDeletionStore(factory, TimeProvider.System);
        var work = Assert.IsType<SourceDeletionWorkItem>(await store.ClaimNextAsync(default));
        Assert.Equal(pendingId, Assert.Single(await store.ReadPendingCleanupAsync(work, default)).CleanupItemId);
        Assert.NotNull(capture.Sql);
        await using var connection = new SqlConnection(_fixture.ConnectionString);
        await connection.OpenAsync();
        var messages = new List<string>();
        connection.InfoMessage += (_, item) => messages.Add(item.Message);
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SET STATISTICS IO ON;\n" + capture.Sql + ";\nSET STATISTICS IO OFF;";
            foreach (var parameter in capture.Parameters) command.Parameters.Add(parameter);
            await using var rows = await command.ExecuteReaderAsync();
            do { while (await rows.ReadAsync()) { } } while (await rows.NextResultAsync());
        }
        var reads = messages.SelectMany(message => Regex.Matches(message, @"(?<!lob )logical reads (\d+),"))
            .Sum(match => long.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture));
        output.WriteLine($"cleanup_pending_tail completed=35000 pending=1 logical_reads={reads}");
        Assert.InRange(reads, 1, 64);
        await using var check = CreateContext();
        Assert.Equal(35000, await check.SourceDeletionCleanupItems.CountAsync(value => value.State == 1));
        Assert.Single(await check.SourceDeletionCleanupItems.Where(value => value.State == 0).ToArrayAsync());
    }

    private sealed class PendingQueueCommand : DbCommandInterceptor
    {
        public string? Sql { get; private set; }
        public SqlParameter[] Parameters { get; private set; } = [];
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("FROM [SourceDeletionCleanupItems]", StringComparison.Ordinal))
            {
                Sql = command.CommandText;
                Parameters = command.Parameters.Cast<SqlParameter>().Select(value => (SqlParameter)((ICloneable)value).Clone()).ToArray();
            }
            return ValueTask.FromResult(result);
        }
    }
}
