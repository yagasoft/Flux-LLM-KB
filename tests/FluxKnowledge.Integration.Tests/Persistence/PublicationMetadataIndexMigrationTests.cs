using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Integration.Tests.Indexing;
using FluxKnowledge.Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using System.Diagnostics;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Persistence;

[Collection("sql-full-text")]
public sealed class PublicationMetadataIndexMigrationTests(NativeSqlServerFixture fixture) : IClassFixture<NativeSqlServerFixture>
{
    [NativeSqlServerFact]
    public async Task Journal_and_segmented_admission_accept_the_verified_metadata_successor()
    {
        await using var environment = await SqlToUsearchRebuildTests.PipelineEnvironment.CreateAsync(fixture,
            "Native admission observes the additive metadata index.", segmented: true);
        var source = FluxKnowledge.Testing.RepositoryFiles.Root();
        var area = Path.Combine(@"I:\Codex Workspaces\Staging\FluxKnowledge", "JournalMigration-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(area);
        try
        {
            var start = new ProcessStartInfo("pwsh") { UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var argument in new[] { "-NoProfile", "-File", Path.Combine(source, "tests/native/source-journal-composed-sql-admission.ps1"),
                "-SourceRoot", source, "-ReceiptDirectory", area,
                "-WebEntryPath", typeof(FluxKnowledge.Web.Endpoints.HealthEndpoints).Assembly.Location }) start.ArgumentList.Add(argument);
            start.Environment["FLUXKNOWLEDGE_JOURNAL_DISPOSABLE_SQL"] = fixture.ConnectionString;
            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync(); var errors = process.StandardError.ReadToEndAsync();
            using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            try { await process.WaitForExitAsync(deadline.Token); }
            catch { process.Kill(true); await process.WaitForExitAsync(); throw; }
            Assert.True(process.ExitCode == 0, await output + await errors);
        }
        finally
        {
            if (!Path.GetFullPath(area).StartsWith(@"I:\Codex Workspaces\Staging\FluxKnowledge\JournalMigration-", StringComparison.OrdinalIgnoreCase))
                throw new IOException("Unsafe native admission fixture cleanup.");
            Directory.Delete(area, true);
        }
    }

    [NativeSqlServerFact]
    public async Task Pinned_deployment_executor_is_atomic_idempotent_and_bound_to_the_database_incarnation()
    {
        await using var environment = await SqlToUsearchRebuildTests.PipelineEnvironment.CreateAsync(fixture,
            "Pinned migration SQL preserves the canonical control.", segmented: true);
        await environment.AddRetainedAndPumpAsync("Retained migration executor control.");
        await using var context = await environment.Factory.CreateDbContextAsync();
        var baseline = await FingerprintsAsync(context);
        var source = FluxKnowledge.Testing.RepositoryFiles.Root();
        var area = Path.Combine(@"I:\Codex Workspaces\Staging\FluxKnowledge", "VectorMetadataSql-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(area);
        try
        {
            var start = new ProcessStartInfo("pwsh") { UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var argument in new[] { "-NoProfile", "-File", Path.Combine(source, "tests/native/vector-publication-metadata-migration-sql.ps1"),
                "-SourceRoot", source, "-ReceiptDirectory", area }) start.ArgumentList.Add(argument);
            start.Environment["FLUXKNOWLEDGE_VECTOR_METADATA_DISPOSABLE_SQL"] = fixture.ConnectionString;
            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync(); var errors = process.StandardError.ReadToEndAsync();
            using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            try { await process.WaitForExitAsync(deadline.Token); }
            catch { process.Kill(true); await process.WaitForExitAsync(); throw; }
            Assert.True(process.ExitCode == 0, await output + await errors);
            Assert.Equal(baseline, await FingerprintsAsync(context));
            await AssertShapeAsync(context);
        }
        finally
        {
            if (!Path.GetFullPath(area).StartsWith(@"I:\Codex Workspaces\Staging\FluxKnowledge\VectorMetadataSql-", StringComparison.OrdinalIgnoreCase))
                throw new IOException("Unsafe metadata migration fixture cleanup.");
            Directory.Delete(area, true);
        }
    }

    [NativeSqlServerFact]
    public async Task Metadata_index_up_down_and_reapply_preserve_canonical_rows_and_publication_ownership()
    {
        await using var environment = await SqlToUsearchRebuildTests.PipelineEnvironment.CreateAsync(fixture,
            "The additive index preserves the original canonical vector bytes.", segmented: true);
        await environment.AddRetainedAndPumpAsync("Retained ownership and publication survive index migration.");
        await using var context = await environment.Factory.CreateDbContextAsync();
        var migrations = context.Database.GetMigrations().ToArray();
        var position = Array.FindIndex(migrations, value => value.EndsWith("_AddVectorPublicationMetadata", StringComparison.Ordinal));
        Assert.True(position > 0);
        var baseline = await FingerprintsAsync(context);
        await AssertShapeAsync(context);
        var migrator = context.GetService<IMigrator>();
        try
        {
            await migrator.MigrateAsync(migrations[position - 1]);
            Assert.Equal(0, await context.Database.SqlQuery<int>($"SELECT COUNT(*) AS [Value] FROM sys.indexes WHERE object_id=OBJECT_ID(N'Vectors') AND name=N'IX_Vectors_PublicationMetadata'").SingleAsync());
            Assert.Equal(baseline, await FingerprintsAsync(context));
            await migrator.MigrateAsync(migrations[position]);
            await AssertShapeAsync(context);
            Assert.Equal(baseline, await FingerprintsAsync(context));
            await migrator.MigrateAsync(migrations[position]);
            await AssertShapeAsync(context);
            Assert.Equal(baseline, await FingerprintsAsync(context));
        }
        finally { await context.Database.MigrateAsync(); }
    }

    private static async Task AssertShapeAsync(FluxKnowledgeDbContext context)
    {
        Assert.Equal(1, await context.Database.SqlQuery<int>($"""
            SELECT COUNT(*) AS [Value] FROM sys.indexes WHERE object_id=OBJECT_ID(N'Vectors')
              AND name=N'IX_Vectors_PublicationMetadata' AND is_unique=0 AND has_filter=0 AND is_disabled=0
            """).SingleAsync());
        var included = await context.Database.SqlQuery<string>($"""
            SELECT column_info.name AS [Value] FROM sys.indexes index_info
            JOIN sys.index_columns membership ON membership.object_id=index_info.object_id AND membership.index_id=index_info.index_id
            JOIN sys.columns column_info ON column_info.object_id=membership.object_id AND column_info.column_id=membership.column_id
            WHERE index_info.object_id=OBJECT_ID(N'Vectors') AND index_info.name=N'IX_Vectors_PublicationMetadata' AND membership.is_included_column=1
            ORDER BY membership.index_column_id
            """).ToArrayAsync();
        Assert.Equal(["TextChunkId", "SourceRevision", "PayloadChecksum", "TextChunkContentHash", "SearchInputHash", "IsDeleted"], included);
        var keys = await context.Database.SqlQuery<string>($"""
            SELECT column_info.name AS [Value] FROM sys.indexes index_info
            JOIN sys.index_columns membership ON membership.object_id=index_info.object_id AND membership.index_id=index_info.index_id
            JOIN sys.columns column_info ON column_info.object_id=membership.object_id AND column_info.column_id=membership.column_id
            WHERE index_info.object_id=OBJECT_ID(N'Vectors') AND index_info.name=N'IX_Vectors_PublicationMetadata' AND membership.key_ordinal>0
            ORDER BY membership.key_ordinal
            """).ToArrayAsync();
        Assert.Equal(["VectorId"], keys);
    }

    private static async Task<string[]> FingerprintsAsync(FluxKnowledgeDbContext context)
    {
        var results = new List<string>();
        foreach (var (table, keys) in new[]
        {
            ("Vectors", "VectorId"), ("PipelineRecords", "Id"), ("SourceRevisions", "Id"), ("SourceIdentities", "Id"),
            ("Artifacts", "Id"), ("TextChunks", "Id"), ("Jobs", "Id"), ("IndexState", "Id"),
            ("CurrentIndexMembers", "CorpusEpoch,VectorId"), ("IndexGenerations", "Id")
        })
        {
            // These identifiers are fixed fixture-owned table/column names.
            var sql = $"""
                SELECT CONVERT(varchar(64),HASHBYTES('SHA2_256',CONVERT(varbinary(max),
                    (SELECT * FROM [{table}] ORDER BY [{keys.Replace(",", "],[", StringComparison.Ordinal)}] FOR JSON PATH,INCLUDE_NULL_VALUES))),2) AS [Value]
                """;
            results.Add(await context.Database.SqlQueryRaw<string>(sql).SingleAsync());
        }
        return results.ToArray();
    }
}
