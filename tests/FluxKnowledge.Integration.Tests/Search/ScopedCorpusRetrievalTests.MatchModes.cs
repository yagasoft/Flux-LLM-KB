using FluxKnowledge.Application.Ports;
using FluxKnowledge.Infrastructure.SqlServer.Search;
using Microsoft.EntityFrameworkCore;
using FluxKnowledge.Integration.Tests.Support;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Search;

public sealed partial class ScopedCorpusRetrievalTests
{
    [NativeSqlServerFact]
    public async Task Keyword_readiness_is_incomplete_for_idle_but_pending_tracked_changes()
    {
        var factory = SqlTestData.CreateFactory(fixture);
        await using var db = await factory.CreateDbContextAsync();
        AddPublishedText(db, Guid.NewGuid(), @"C:\readiness-seed", "seed.txt", ["readiness seed"]);
        await SaveAndWaitForFullTextAsync(db);
        // Only this disposable fixture's index is changed. Manual tracking holds
        // pending changes deterministically while the native population is idle.
        await db.Database.ExecuteSqlRawAsync("ALTER FULLTEXT INDEX ON [TextChunks] SET CHANGE_TRACKING MANUAL;");
        try
        {
            var root = Guid.NewGuid();
            AddPublishedText(db, root, @"C:\readiness-pending", "pending.txt", ["pendingfixturewordunique evidence"]);
            await db.SaveChangesAsync();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            const string idlePending = """
                SELECT CASE WHEN FULLTEXTCATALOGPROPERTY(N'FluxKnowledge', N'PopulateStatus') = 0
                    AND CONVERT(int, OBJECTPROPERTYEX(OBJECT_ID(N'TextChunks'), 'TableFulltextPopulateStatus')) = 0
                    AND CONVERT(int, OBJECTPROPERTYEX(OBJECT_ID(N'TextChunks'), 'TableFulltextPendingChanges')) > 0
                    THEN 1 ELSE 0 END AS [Value]
                """;
            while (await db.Database.SqlQueryRaw<int>(idlePending).SingleAsync(deadline.Token) != 1)
                await Task.Delay(100, deadline.Token);
            var reader = new SqlCorpusRetrievalReader(factory);
            var readiness = await reader.GetLexicalReadinessAsync(default);
            Assert.True(readiness.IndexPresent);
            Assert.False(readiness.PopulationComplete);
            Assert.Single(await reader.ReadLiteralCandidatesAsync("pendingfixturewordunique", new("root", [root], null), default));
        }
        finally { await db.Database.ExecuteSqlRawAsync("ALTER FULLTEXT INDEX ON [TextChunks] SET CHANGE_TRACKING AUTO;"); }
    }

    [NativeSqlServerFact]
    public async Task Literal_SQL_preserves_edge_spaces_case_and_decomposed_unicode_in_scope()
    {
        var factory = SqlTestData.CreateFactory(fixture); var root = Guid.NewGuid();
        await using (var db = await factory.CreateDbContextAsync())
        {
            AddPublishedText(db, root, @"C:\literal-raw", "unicode.txt",
                ["first e\u0301 last", "first é last", "first E\u0301 last", "firste\u0301last"]);
            AddPublishedText(db, Guid.NewGuid(), @"C:\literal-outside", "unicode.txt", ["first e\u0301 last"]);
            await db.SaveChangesAsync();
        }
        var actual = await new SqlCorpusRetrievalReader(factory).ReadLiteralCandidatesAsync(" e\u0301 ", new("root", [root], null), default);
        var hit = Assert.Single(actual);
        Assert.Equal("first e\u0301 last", hit.Content);
        Assert.Equal(root, hit.RootId);
        Assert.Equal(0, hit.FullTextRank);
        var service = new FluxKnowledge.Application.Search.CorpusRetrievalService(new SqlCorpusRetrievalReader(factory),
            new TestEvidenceCodec(), new FluxKnowledge.Infrastructure.SqlServer.Visibility.LocalPrivateContentDisclosure());
        var response = await service.SearchAsync(new(" e\u0301 ", 10, "root", root, null, "literal"), default);
        var result = Assert.Single(response.Results);
        Assert.Equal("not-requested", response.SemanticStatus);
        Assert.Contains(" e\u0301 ", result.Passage, StringComparison.Ordinal);
        Assert.Equal(result.Passage, (await service.ReadAsync(new(result.EvidenceRef, 0), default)).Text);
    }

    [NativeSqlServerFact]
    public async Task Keyword_candidates_follow_scoped_FullText_rank_without_substring_only_rows()
    {
        var factory = SqlTestData.CreateFactory(fixture); var root = Guid.NewGuid(); Guid artifact;
        // More matches than the candidate budget, both inside and outside the
        // requested root, prove that TOP is applied after scope qualification.
        var bodies = Enumerable.Range(0, 298).Select(i => "needle ordinary evidence " + i)
            .Concat(["prefixneedlesuffix", "NEEDLE NEEDLE NEEDLE", "NEEDLE distant evidence"]).ToArray();
        await using (var db = await factory.CreateDbContextAsync())
        {
            AddPublishedText(db, root, @"C:\keyword-order", "groups.txt", bodies);
            artifact = db.Artifacts.Local.Single().Id;
            AddPublishedText(db, Guid.NewGuid(), @"C:\keyword-elsewhere", "crowding.txt",
                Enumerable.Range(0, 340).Select(i => "NEEDLE NEEDLE NEEDLE high rank " + i).ToArray());
            await db.SaveChangesAsync();
        }
        await using var reference = await factory.CreateDbContextAsync();
        for (var attempt = 0; ; attempt++)
        {
            var count = await reference.Database.SqlQuery<int>($"""
                SELECT COUNT(*) AS [Value] FROM FREETEXTTABLE([TextChunks], [SearchText], N'needle') AS [ft]
                INNER JOIN [TextChunks] AS [chunk] ON [chunk].[Id] = [ft].[KEY] WHERE [chunk].[ArtifactId] = {artifact}
                """).SingleAsync();
            if (count == 300) break;
            if (attempt == 49) throw new InvalidOperationException("Controlled Full-Text fixture did not become ready.");
            await Task.Delay(200);
        }
        var expected = await reference.Database.SqlQuery<LexicalOrderRow>($"""
            SELECT TOP (100) [chunk].[Id] AS [ChunkId], [ft].[RANK] AS [FullTextRank]
            FROM FREETEXTTABLE([TextChunks], [SearchText], N'needle') AS [ft]
            INNER JOIN [TextChunks] AS [chunk] ON [chunk].[Id] = [ft].[KEY]
            WHERE [chunk].[ArtifactId] = {artifact} AND [chunk].[Length] BETWEEN 1 AND 2048
            ORDER BY [ft].[RANK] DESC, [chunk].[Id]
            """).ToArrayAsync();
        var actual = await new SqlCorpusRetrievalReader(factory).ReadLexicalCandidatesAsync("needle", new("root", [root], null), default);
        Assert.Equal(100, actual.Count);
        Assert.Equal(expected.Select(row => (row.ChunkId, row.FullTextRank)), actual.Select(row => (row.ChunkId, row.FullTextRank)));
        Assert.DoesNotContain(actual, row => row.Content == "prefixneedlesuffix");
        Assert.All(actual, row => Assert.Equal(root, row.RootId));
    }
}
