using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Persistence;

[Collection("sql-full-text")]
public sealed class OutlookWatchedMigrationTests(NativeSqlServerFixture fixture) : IClassFixture<NativeSqlServerFixture>
{
    private const string Baseline = "20261117210606_PersistPassageWindowEligibility";
    private const string Target = "20261118224032_BindOutlookWatchedSources";

    [NativeSqlServerFact]
    public Task Typed_migration_preserves_schema_on_rollback_and_classifies_lost_acknowledgement() =>
        VerifyTypedMigrationAsync(Baseline, Target);

    [NativeSqlServerFact]
    public Task Workspace_notes_migration_preserves_schema_on_rollback_and_classifies_lost_acknowledgement() =>
        VerifyTypedMigrationAsync(Target, "20261118225053_BindCodexNotesToWorkspace", "-CodexWorkspace");

    private async Task VerifyTypedMigrationAsync(string baseline, string target, string? mode = null)
    {
        await using var db = await SqlTestData.CreateFactory(fixture).CreateDbContextAsync();
        await SqlSourceDeletionPendingQueueIndex.EnsureAsync(db, default);
        await db.GetService<IMigrator>().MigrateAsync(baseline);
        try
        {
            var source = FluxKnowledge.Testing.RepositoryFiles.Root();
            var area = Path.Combine(Path.GetTempPath(), "outlook-migration-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(area);
            var start = new ProcessStartInfo("pwsh")
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true
            };
            foreach (var argument in new[] { "-NoProfile", "-File",
                Path.Combine(source, "tests/native/outlook-watched-source-migration-sql.ps1"),
                "-SourceRoot", source, "-ReceiptDirectory", area })
                start.ArgumentList.Add(argument);
            if (mode is not null) start.ArgumentList.Add(mode);
            start.Environment["FLUXKNOWLEDGE_OUTLOOK_DISPOSABLE_SQL"] = fixture.ConnectionString;
            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync();
            var errors = process.StandardError.ReadToEndAsync();
            using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            try { await process.WaitForExitAsync(deadline.Token); }
            catch { process.Kill(true); await process.WaitForExitAsync(); throw; }
            Assert.True(process.ExitCode == 0, await output + await errors);
            Assert.Equal(target, (await db.Database.GetAppliedMigrationsAsync()).Last());
        }
        finally { await db.Database.MigrateAsync(); }
    }

    [NativeSqlServerFact]
    public async Task Ef_outlook_migration_schema_matches_the_reviewed_contract()
    {
        await using var db = await SqlTestData.CreateFactory(fixture).CreateDbContextAsync();
        var query = await File.ReadAllTextAsync(Path.Combine(FluxKnowledge.Testing.RepositoryFiles.Root(),
            "scripts/deploy/outlook-watched-source-schema.sql"));
        var successor = await ReadShapeAsync();
        await db.GetService<IMigrator>().MigrateAsync(Baseline);
        try
        {
            var predecessor = await ReadShapeAsync();
            var history = string.Join('\n', await db.Database.GetAppliedMigrationsAsync());
            Assert.Equal("6DF768C9BC61859A8BAF6E611F0D4D3D7416BEAA76BE8D04E47E5B96D8712740", Hash(history));
            Assert.Equal("267216937E3332CEC817C34F63A04FEF96F88FFE35A3317C923269379BF8ABA5", predecessor);
            Assert.Equal("43EDA5A878152DA099B10A4DC44F5514EFBA7EF8038561CBBC976819BF89E92B", successor);
        }
        finally { await db.Database.MigrateAsync(); }

        async Task<string> ReadShapeAsync()
        {
            await db.Database.OpenConnectionAsync();
            try
            {
                await using var command = db.Database.GetDbConnection().CreateCommand();
                command.CommandText = query;
                var json = new StringBuilder();
                await using var reader = await command.ExecuteReaderAsync();
                while (await reader.ReadAsync()) json.Append(reader.GetString(0));
                return Hash(json.ToString());
            }
            finally { await db.Database.CloseConnectionAsync(); }
        }
    }

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
