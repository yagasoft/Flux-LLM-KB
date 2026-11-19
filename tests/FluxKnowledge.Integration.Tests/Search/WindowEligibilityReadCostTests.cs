using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Text.RegularExpressions;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Integration.Tests.Indexing;
using FluxKnowledge.Integration.Tests.Support;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;
using Xunit;
using Xunit.Abstractions;

namespace FluxKnowledge.Integration.Tests.Search;

[Collection("sql-full-text")]
public sealed class WindowEligibilityReadCostTests(NativeSqlServerFixture fixture, ITestOutputHelper output) : IClassFixture<NativeSqlServerFixture>
{
    [NativeSqlServerTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Sparse_ID_rechecks_and_single_revisits_do_not_scan_the_excluded_corpus_under_either_compilation_order(bool lookupFirst)
    {
        await using var environment = await SqlToUsearchRebuildTests.PipelineEnvironment.CreateAsync(fixture, "An early keeper.", publish: false, embed: false);
        var removed = await environment.AddRetainedAndPumpAsync("An excluded synthetic source.");
        await using var db = environment.Factory.CreateDbContext();
        var root = await db.PipelineRecords.Where(value => value.Id == removed).Select(value => value.SourceRevision!.SourceRootId).SingleAsync();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT TextChunks(ArtifactId,SourceRevision,Ordinal,StartOffset,Length,Content,ContentHash,PassagePolicyFingerprint,ContextHeader,SearchInputHash)
            SELECT TOP(50000) template.ArtifactId,template.SourceRevision,
                template.Ordinal+CONVERT(int,ROW_NUMBER() OVER(ORDER BY(SELECT NULL))),template.StartOffset,template.Length,
                template.Content,template.ContentHash,template.PassagePolicyFingerprint,template.ContextHeader,template.SearchInputHash
            FROM TextChunks template JOIN Artifacts artifact ON artifact.Id=template.ArtifactId
            CROSS JOIN sys.all_objects a CROSS JOIN sys.all_objects b WHERE artifact.PipelineRecordId={removed};
            """);
        var late = await environment.AddRetainedAndPumpAsync("A late keeper after the excluded span.");
        var lateId = await db.TextChunks.Where(value => value.Artifact.PipelineRecordId == late).Select(value => value.Id).SingleAsync();
        await db.SourceRootConfigurations.Where(value => value.Id == root).ExecuteUpdateAsync(set => set.SetProperty(value => value.State, 2));
        await db.TextChunkWindowEligibilities.ExecuteDeleteAsync();
        var capture = new InputCommands();
        var store = new SqlTextChunkWindowEligibilityStore(new MeasuredFactory(fixture.ConnectionString, capture));
        if (lookupFirst) await store.RequestRevisitAsync(lateId, default);
        var before = capture.Captured.Count;
        var result = await store.RunOneAsync(default, 2);
        Assert.Equal(2, result.Inspected);
        Assert.Equal(2, result.Recomputed);
        Assert.Equal(2, await db.TextChunkWindowEligibilities.CountAsync());
        var removedArtifacts = db.Artifacts.Where(value => value.PipelineRecordId == removed).Select(value => value.Id);
        Assert.False(await db.TextChunkWindowEligibilities.AnyAsync(value => removedArtifacts.Contains(value.ArtifactId)));
        var page = capture.Captured.Skip(before).ToArray();
        var range = Assert.Single(page, value => !value.InTransaction);
        var recheck = Assert.Single(page, value => value.InTransaction);
        before = capture.Captured.Count;
        await store.RequestRevisitAsync(lateId, default);
        var revisit = Assert.Single(capture.Captured.Skip(before));
        await using var replay = environment.Factory.CreateDbContext();
        await replay.Database.OpenConnectionAsync();
        var connection = (SqlConnection)replay.Database.GetDbConnection();
        var messages = new List<string>();
        connection.InfoMessage += (_, item) => messages.Add(item.Message);
        await using var transaction = await replay.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        await replay.Database.ExecuteSqlRawAsync("SET STATISTICS IO ON;");
        async Task<long> Measure(InputCommand command, int expected)
        {
            messages.Clear();
            await using var sql = connection.CreateCommand();
            sql.Transaction = (SqlTransaction)transaction.GetDbTransaction();
            sql.CommandText = command.Sql;
            foreach (var parameter in command.Parameters) sql.Parameters.Add(((ICloneable)parameter).Clone());
            var rows = 0;
            await using (var reader = await sql.ExecuteReaderAsync())
                do { while (await reader.ReadAsync()) rows++; } while (await reader.NextResultAsync());
            Assert.Equal(expected, rows);
            var reads = messages.SelectMany(message => Regex.Matches(message, @"(?<!lob )logical reads (\d+),"))
                .Sum(match => long.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture));
            Assert.True(reads > 0, "SQL did not report logical reads.");
            return reads;
        }
        var rangeReads = await Measure(range, 2);
        var recheckReads = await Measure(recheck, 2);
        var revisitReads = await Measure(revisit, 1);
        output.WriteLine($"window_input_reads lookup_first={lookupFirst} excluded_chunks=50001 range={rangeReads} fenced_recheck={recheckReads} single_revisit={revisitReads}");
        Assert.InRange(recheckReads, 1, 2000);
        Assert.InRange(revisitReads, 1, 2000);
        await replay.Database.ExecuteSqlRawAsync("SET STATISTICS IO OFF;");
        await transaction.CommitAsync();
    }

    private sealed record InputCommand(string Sql, DbParameter[] Parameters, bool InTransaction);
    private sealed class InputCommands : DbCommandInterceptor
    {
        private readonly string _tag = "\n/* window-plan-order-" + Guid.NewGuid().ToString("N") + " */";
        internal List<InputCommand> Captured { get; } = [];
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData data,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("window-eligibility-inputs: metadata only", StringComparison.Ordinal))
            {
                command.CommandText += _tag; // Isolate compilation order without clearing any shared plan cache.
                Captured.Add(new(command.CommandText, command.Parameters.Cast<DbParameter>().Select(value => (DbParameter)((ICloneable)value).Clone()).ToArray(), command.Transaction is not null));
            }
            return ValueTask.FromResult(result);
        }
    }
    private sealed class MeasuredFactory(string connection, InputCommands capture) : IDbContextFactory<FluxKnowledgeDbContext>
    {
        public FluxKnowledgeDbContext CreateDbContext() => new(new DbContextOptionsBuilder<FluxKnowledgeDbContext>()
            .UseSqlServer(connection).AddInterceptors(capture).Options);
    }
}
