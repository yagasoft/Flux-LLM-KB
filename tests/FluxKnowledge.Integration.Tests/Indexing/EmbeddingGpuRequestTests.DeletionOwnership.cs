using System.Data;
using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using FluxKnowledge.Domain.Gpu;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using FluxKnowledge.Integration.Tests.Support;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Indexing;

public sealed partial class EmbeddingGpuRequestTests
{
    [NativeSqlServerFact]
    public async Task Deletion_checks_large_owned_history_without_revisiting_archived_provenance()
    {
        const int count = 4096;
        await using var environment = await SqlToUsearchRebuildTests.PipelineEnvironment.CreateAsync(fixture,
            "Deletion ownership history.", embed: false);
        var work = await ClaimAsync(environment);
        var now = DateTimeOffset.UtcNow;
        var ids = Enumerable.Range(0, count).Select(_ => Guid.NewGuid()).ToArray();
        await using (var setup = environment.Factory.CreateDbContext())
        {
            foreach (var id in ids)
            {
                setup.GpuMiniTasks.Add(new GpuMiniTaskEntity
                {
                    Id = id, ParentJobId = work.Job.JobId.Value, SourceRevision = work.Job.SourceRevision,
                    PriorityLane = (int)GpuPriorityLane.DocumentIndexing, ModelRuntimeKey = Runtime.RuntimeKey,
                    SettingsFingerprint = Runtime.SettingsFingerprint, EstimatedBytes = 1024,
                    IdempotencyKey = "deletion-history:" + id.ToString("N"),
                    ExecutionState = (int)GpuMiniTaskExecutionState.Completed, CreatedAtUtc = now
                });
                setup.EmbeddingGpuRequests.Add(new EmbeddingGpuRequestEntity
                {
                    MiniTaskId = id, ParentJobId = work.Job.JobId.Value, PipelineRecordId = work.Job.PipelineRecordId.Value,
                    SourceRevision = work.Job.SourceRevision, GenerationId = Guid.NewGuid(), CorpusEpoch = Guid.NewGuid(),
                    ModelFingerprint = Runtime.Profile.ModelFingerprint, Dimensions = Runtime.Profile.Dimensions,
                    InputsJson = "[]", InputDigest = new string('a', 64), State = 2,
                    NativeCleanupConfirmed = true, CleanupConfirmedAtUtc = now, CreatedAtUtc = now, UpdatedAtUtc = now
                });
            }
            await setup.SaveChangesAsync();
        }

        var observer = new DeletionOwnershipCommands();
        await using var measured = new FluxKnowledgeDbContext(new DbContextOptionsBuilder<FluxKnowledgeDbContext>()
            .UseSqlServer(fixture.ConnectionString).AddInterceptors(observer).Options);
        await measured.Database.OpenConnectionAsync();
        var messages = new List<string>();
        ((SqlConnection)measured.Database.GetDbConnection()).InfoMessage += (_, item) => messages.Add(item.Message);
        await measured.Database.ExecuteSqlRawAsync("SET STATISTICS IO ON;");
        await using var transaction = await measured.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        var timer = Stopwatch.StartNew();
        Assert.False(await SqlEmbeddingGpuRequestStore.HasContradictoryRequestsAsync(measured,
            [work.Job.PipelineRecordId.Value], ids, CancellationToken.None));
        timer.Stop();
        messages.Clear();
        foreach (var captured in observer.Captured)
        {
            await using var command = measured.Database.GetDbConnection().CreateCommand();
            command.Transaction = transaction.GetDbTransaction();
            command.CommandText = "SET STATISTICS IO ON;\n" + captured.Sql;
            foreach (var parameter in captured.Parameters) command.Parameters.Add(((ICloneable)parameter).Clone());
            await using var rows = await command.ExecuteReaderAsync();
            do { while (await rows.ReadAsync()) { } } while (await rows.NextResultAsync());
        }
        var reads = messages.SelectMany(message => Regex.Matches(message, @"(?<!lob )logical reads (\d+),"))
            .Sum(match => long.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture));
        output.WriteLine($"deletion_owned_history tasks={count} elapsed_ms={timer.Elapsed.TotalMilliseconds:F1} logical_reads={reads}");
        Assert.DoesNotContain(observer.Commands, sql => sql.Contains("CorpusRebuildSupersededJobs", StringComparison.Ordinal));
        Assert.DoesNotContain(observer.Commands, sql => sql.Contains("InputsJson", StringComparison.Ordinal));
        Assert.InRange(reads, 1, count * 5L);

        // Request ownership is checked independently of the task's parent root.
        // Removing just the final task from the supplied proof must still refuse.
        Assert.True(await SqlEmbeddingGpuRequestStore.HasContradictoryRequestsAsync(measured,
            [work.Job.PipelineRecordId.Value], ids[..^1], CancellationToken.None));
        Assert.False(await SqlEmbeddingGpuRequestStore.HasContradictoryRequestsAsync(measured,
            [Guid.NewGuid()], [], CancellationToken.None));
        Assert.False(await SqlEmbeddingGpuRequestStore.HasContradictoryRequestsAsync(measured,
            [], ids, CancellationToken.None));
        await transaction.RollbackAsync();
    }

    private sealed class DeletionOwnershipCommands : DbCommandInterceptor
    {
        public List<string> Commands { get; } = [];
        public List<(string Sql, SqlParameter[] Parameters)> Captured { get; } = [];
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Commands.Add(command.CommandText);
            Captured.Add((command.CommandText, command.Parameters.Cast<SqlParameter>()
                .Select(parameter => (SqlParameter)((ICloneable)parameter).Clone()).ToArray()));
            return ValueTask.FromResult(result);
        }
    }
}
