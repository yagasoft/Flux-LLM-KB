using System.Data.Common;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.SqlServer.Search;
using FluxKnowledge.Integration.Tests.Support;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Search;

public sealed partial class ScopedCorpusRetrievalTests
{
    [NativeSqlServerFact]
    public async Task Literal_candidate_order_matches_ordinal_substrings_and_excludes_FullText_only_rows()
    {
        var factory = SqlTestData.CreateFactory(fixture); var root = Guid.NewGuid(); Guid artifact;
        var bodies = Enumerable.Range(0, 98).Select(i => "needle ordinary evidence " + i)
            .Concat(["prefixneedlesuffix", "NEEDLE NEEDLE NEEDLE", "NEEDLE distant evidence"]).ToArray();
        await using (var db = await factory.CreateDbContextAsync())
        {
            AddPublishedText(db, root, @"C:\lexical-order", "groups.txt", bodies);
            artifact = db.Artifacts.Local.Single().Id;
            await db.SaveChangesAsync();
        }
        await using var reference = await factory.CreateDbContextAsync();
        for (var attempt = 0; ; attempt++)
        {
            var count = await reference.Database.SqlQuery<int>($"""
                SELECT COUNT(*) AS [Value] FROM FREETEXTTABLE([TextChunks], [SearchText], N'needle') AS [ft]
                INNER JOIN [TextChunks] AS [chunk] ON [chunk].[Id] = [ft].[KEY] WHERE [chunk].[ArtifactId] = {artifact}
                """).SingleAsync();
            if (count == 100) break;
            if (attempt == 49) throw new InvalidOperationException("Controlled full-text fixture did not become ready.");
            await Task.Delay(200);
        }
        var expected = await reference.Database.SqlQuery<LexicalOrderRow>($"""
            SELECT TOP (100) [chunk].[Id] AS [ChunkId], 0 AS [FullTextRank]
            FROM [TextChunks] AS [chunk]
            WHERE [chunk].[ArtifactId] = {artifact} AND [chunk].[Length] BETWEEN 1 AND 2048
              AND CHARINDEX(N'needle', [chunk].[Content] COLLATE Latin1_General_100_BIN2) > 0
            ORDER BY [chunk].[Id]
            """).ToArrayAsync();
        var actual = await new SqlCorpusRetrievalReader(factory).ReadLiteralCandidatesAsync("needle", new("root", [root], null), default);
        Assert.Equal(99, actual.Count);
        Assert.Equal(expected.Select(row => (row.ChunkId, row.FullTextRank)), actual.Select(row => (row.ChunkId, row.FullTextRank)));
        Assert.Equal("prefixneedlesuffix", actual[98].Content);
        Assert.DoesNotContain(actual, passage => passage.Content.Contains("NEEDLE", StringComparison.Ordinal));
        Assert.Equal(0, actual[98].FullTextRank);
    }

    [NativeSqlServerTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_partial_temporary_batch_is_cleaned_on_the_same_session_after_retry_or_caller_cancellation(bool cancel)
    {
        var setup = SqlTestData.CreateFactory(fixture); var root = Guid.NewGuid();
        long fixtureChunk;
        await using (var db = await setup.CreateDbContextAsync())
        {
            AddPublishedText(db, root, @"C:\lexical-retry\" + root.ToString("N"), "evidence.txt", ["needle evidence"]);
            await db.SaveChangesAsync(); fixtureChunk = db.TextChunks.Local.Single().Id;
        }
        // Keyword retrieval is indexed. Establish this fixture's asynchronous FT
        // readiness before injecting the independent retry/cancellation failure.
        await using (var ready = await setup.CreateDbContextAsync())
        {
            for (var attempt = 0; ; attempt++)
            {
                var count = await ready.Database.SqlQuery<int>($"""
                    SELECT COUNT(*) AS [Value] FROM FREETEXTTABLE([TextChunks], [SearchText], N'needle') AS [ft]
                    WHERE [ft].[KEY] = {fixtureChunk}
                    """).SingleAsync();
                if (count == 1) break;
                if (attempt == 49) throw new InvalidOperationException("Controlled keyword fixture did not become ready.");
                await Task.Delay(200);
            }
        }
        await using var connection = new SqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        var interceptor = new PartialLexicalBatch(cancel);
        var options = new DbContextOptionsBuilder<FluxKnowledgeDbContext>().UseSqlServer(connection,
            sql => sql.EnableRetryOnFailure(1, TimeSpan.Zero, [50025])).AddInterceptors(interceptor).Options;
        var reader = new SqlCorpusRetrievalReader(new LexicalSessionFactory(options));
        if (cancel)
        {
            using var caller = new CancellationTokenSource();
            var interrupted = reader.ReadLexicalCandidatesAsync("needle", new("root", [root], null), caller.Token).AsTask();
            await interceptor.Created.Task.WaitAsync(TimeSpan.FromSeconds(5)); caller.Cancel();
            var error = await Record.ExceptionAsync(() => interrupted);
            Assert.True(caller.IsCancellationRequested);
            // SqlClient's auxiliary non-query may report cancellation as error 0.
            // Accept that specific provider acknowledgement, never an arbitrary failure.
            if (error is not OperationCanceledException)
            {
                var sql = Assert.IsType<SqlException>(error);
                Assert.Equal(0, sql.Number);
                Assert.Contains("Operation cancelled by user.", sql.Message, StringComparison.Ordinal);
            }
            using var exists = new SqlCommand("SELECT OBJECT_ID(N'tempdb..#corpusScope');", connection);
            Assert.NotEqual(DBNull.Value, await exists.ExecuteScalarAsync());
        }
        var result = await reader.ReadLexicalCandidatesAsync("needle", new("root", [root], null), default);
        Assert.Equal("needle evidence", Assert.Single(result).Content);
        Assert.Equal(2, interceptor.Attempts);
        using var cleaned = new SqlCommand("SELECT OBJECT_ID(N'tempdb..#corpusScope');", connection);
        Assert.Equal(DBNull.Value, await cleaned.ExecuteScalarAsync());
    }

    private sealed class LexicalOrderRow
    {
        public long ChunkId { get; init; }
        public int FullTextRank { get; init; }
    }
    private sealed class LexicalSessionFactory(DbContextOptions<FluxKnowledgeDbContext> options) : IDbContextFactory<FluxKnowledgeDbContext>
    { public FluxKnowledgeDbContext CreateDbContext() => new(options); }
    private sealed class PartialLexicalBatch(bool cancel) : DbCommandInterceptor
    {
        public int Attempts { get; private set; }
        public TaskCompletionSource Created { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData data,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("DROP TABLE IF EXISTS #corpusScope", StringComparison.Ordinal))
            {
                Attempts++;
                if (Attempts == 1)
                {
                    // Separate command models an earlier interrupted batch. Defining the
                    // same temporary table twice in one batch would fail SQL compilation.
                    await using var partial = command.Connection!.CreateCommand();
                    partial.CommandText = "CREATE TABLE #corpusScope ([ArtifactId] uniqueidentifier, [SourceRevision] bigint);";
                    await partial.ExecuteNonQueryAsync(cancellationToken);
                    partial.CommandText = cancel ? "WAITFOR DELAY '00:00:10';" : "THROW 50025,N'synthetic transient failure',1;";
                    var interrupted = partial.ExecuteNonQueryAsync(cancellationToken);
                    Created.TrySetResult();
                    await interrupted;
                }
            }
            return result;
        }
    }
}
