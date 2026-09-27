using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Indexing;

public sealed class CoherentPassageMigrationTests(NativeSqlServerFixture fixture) : IClassFixture<NativeSqlServerFixture>
{
    [NativeSqlServerFact]
    public async Task Projection_migration_replays_after_schema_commit_without_changing_epoch_and_can_downgrade()
    {
        var options = new DbContextOptionsBuilder<FluxKnowledgeDbContext>().UseSqlServer(fixture.ConnectionString).Options;
        await using var context = new FluxKnowledgeDbContext(options);
        var epoch = (await context.IndexState.AsNoTracking().SingleAsync()).CorpusEpoch;
        Assert.NotEqual(Guid.Empty, epoch);

        // Simulate interruption after the schema commit, before Full-Text/history.
        await context.Database.ExecuteSqlRawAsync("ALTER FULLTEXT INDEX ON [dbo].[TextChunks] ADD ([Content] LANGUAGE 1033);");
        await context.Database.ExecuteSqlRawAsync("ALTER FULLTEXT INDEX ON [dbo].[TextChunks] DROP ([SearchText]);");
        await context.Database.ExecuteSqlRawAsync(
            "DELETE FROM [__EFMigrationsHistory] WHERE [MigrationId] = N'20260926180256_AddCoherentPassageProjection';");
        await context.Database.MigrateAsync();

        Assert.Equal(epoch, (await context.IndexState.AsNoTracking().SingleAsync()).CorpusEpoch);
        Assert.Equal(new[] { "SearchText" }, await ReadIndexedColumnsAsync(context));
        var migrations = context.Database.GetMigrations().ToArray();
        var projectionIndex = Array.FindIndex(migrations, m => m.EndsWith("_AddCoherentPassageProjection", StringComparison.Ordinal));
        Assert.True(projectionIndex > 0);
        await context.GetService<IMigrator>().MigrateAsync(migrations[projectionIndex - 1]);
        Assert.Equal(new[] { "Content" }, await ReadIndexedColumnsAsync(context));
        await context.Database.MigrateAsync();
        Assert.Equal(new[] { "SearchText" }, await ReadIndexedColumnsAsync(context));
        Assert.NotEqual(epoch, (await context.IndexState.AsNoTracking().SingleAsync()).CorpusEpoch);
    }

    private static Task<string[]> ReadIndexedColumnsAsync(FluxKnowledgeDbContext context) =>
        context.Database.SqlQueryRaw<string>("""
            SELECT [column].[name] AS [Value]
            FROM sys.fulltext_index_columns AS [indexed]
            JOIN sys.columns AS [column] ON [column].[object_id] = [indexed].[object_id]
                AND [column].[column_id] = [indexed].[column_id]
            WHERE [indexed].[object_id] = OBJECT_ID(N'[dbo].[TextChunks]')
            ORDER BY [column].[name]
            """).ToArrayAsync();
}
