using System.Diagnostics;
using System.Security.Cryptography;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using FluxKnowledge.Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Sources;

public sealed class SourceJournalMigrationDeploymentTests(NativeSqlServerFixture fixture) : IClassFixture<NativeSqlServerFixture>
{
    [NativeSqlServerTheory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Additive_source_migration_preserves_legacy_rows_and_refuses_predecessor_start_after_intent(bool committedFailure, bool deploymentExecutor)
    {
        var factory = SqlTestData.CreateFactory(fixture);
        await using var db = await factory.CreateDbContextAsync();
        const string baseline = "20261024061418_AddDocumentOcrExecutionLifecycle";
        const string target = "20261025124025_PreserveSourceVerificationReadCounts";
        // Only this generated disposable catalogue is reset to prepare the old-schema fixture.
        await db.GetService<IMigrator>().MigrateAsync(baseline);
        var area = Path.Combine(@"I:\Codex Workspaces\Staging\FluxKnowledge", "JournalMigration-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(area);
        var blob = Path.Combine(area, "retained-seed.txt");
        try
        {
            await File.WriteAllTextAsync(blob, "immutable synthetic retained content");
            var hash = Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(blob)));
            var root = Guid.NewGuid(); var revision = Guid.NewGuid(); var now = DateTimeOffset.UtcNow;
            db.SourceRootConfigurations.Add(new() { Id = root, CanonicalPath = area, DisplayName = "Legacy migration fixture",
                Recursive = true, MaximumFileBytes = 1024, ReconciliationCadenceSeconds = 900, ConfigurationRevision = 1,
                CreatedAtUtc = now, UpdatedAtUtc = now });
            db.SourceRevisions.Add(new() { Id = revision, SourceRootId = root, StableSourceIdentity = "legacy-" + root.ToString("N"),
                Revision = 1, ContentSha256 = hash, CanonicalPath = blob, Classification = "text", Extension = ".txt",
                ByteLength = new FileInfo(blob).Length, DiscoveredAtUtc = now });
            db.SourceArtifacts.Add(new() { Id = Guid.NewGuid(), SourceRevisionId = revision, ContentSha256 = hash,
                StoreRelativePath = "retained-seed.txt", ByteLength = new FileInfo(blob).Length, ChecksumVerifiedAtUtc = now, ReferenceCount = 1 });
            await db.SaveChangesAsync();
            var sql = db.GetService<IMigrator>().GenerateScript(baseline, target, MigrationsSqlGenerationOptions.Idempotent);
            var sqlPath = Path.Combine(area, "reviewed-up.sql");
            await File.WriteAllTextAsync(sqlPath, sql, new System.Text.UTF8Encoding(true));
            var receipt = Path.Combine(area, "Receipts"); Directory.CreateDirectory(receipt);
            var source = FluxKnowledge.Testing.RepositoryFiles.Root();
            var start = new ProcessStartInfo("pwsh") { UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var argument in new[] { "-NoProfile", "-File", Path.Combine(source, "tests/native/source-journal-migration-sql.ps1"),
                "-SourceRoot", source, "-SqlPath", sqlPath, "-ReceiptDirectory", receipt,
                "-WebEntryPath", typeof(FluxKnowledge.Web.Endpoints.HealthEndpoints).Assembly.Location }) start.ArgumentList.Add(argument);
            if (committedFailure) start.ArgumentList.Add("-FailAfterFirstMigration");
            if (deploymentExecutor) start.ArgumentList.Add("-UseDeploymentExecutor");
            start.Environment["FLUXKNOWLEDGE_JOURNAL_DISPOSABLE_SQL"] = fixture.ConnectionString;
            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync(); var errors = process.StandardError.ReadToEndAsync();
            using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            try { await process.WaitForExitAsync(deadline.Token); }
            catch { process.Kill(true); await process.WaitForExitAsync(); throw; }
            Assert.True(process.ExitCode == 0, await output + await errors);
            Assert.Equal(hash, Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(blob))));
        }
        finally
        {
            var resolved = Path.GetFullPath(area);
            if (!resolved.StartsWith(@"I:\Codex Workspaces\Staging\FluxKnowledge\JournalMigration-", StringComparison.OrdinalIgnoreCase))
                throw new IOException("Unsafe migration fixture cleanup.");
            Directory.Delete(resolved, true);
        }
    }
}
