using System.Diagnostics;
using FluxKnowledge.Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;
using Xunit.Abstractions;

namespace FluxKnowledge.Integration.Tests.Visibility;

public sealed class SourceDeletionCleanupMigrationDeploymentTests(NativeSqlServerFixture fixture, ITestOutputHelper testOutput) : IClassFixture<NativeSqlServerFixture>
{
    [NativeSqlServerFact]
    public async Task Incremental_cleanup_migration_preserves_attention_and_rejects_schema_or_operation_drift()
    {
        var factory = SqlTestData.CreateFactory(fixture);
        await using var db = await factory.CreateDbContextAsync();
        const string baseline = "20261004175522_AddRepositoryRecoveryAuthority";
        const string target = "20261014134909_AddResumableSourceDeletionCleanup";
        var script = db.GetService<IMigrator>().GenerateScript(baseline, target, MigrationsSqlGenerationOptions.Idempotent);
        var source = new DirectoryInfo(FluxKnowledge.Testing.RepositoryFiles.Root());
        const string relative = "tests/native/source-deletion-cleanup-migration-sql.ps1";
        while (source is not null && !File.Exists(Path.Combine(source.FullName, relative))) source = source.Parent;
        Assert.NotNull(source);
        var path = Path.Combine(Path.GetTempPath(), $"flux-source-cleanup-{Guid.NewGuid():N}.sql");
        try
        {
            await File.WriteAllTextAsync(path, script, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            await db.GetService<IMigrator>().MigrateAsync(baseline);
            var start = new ProcessStartInfo("pwsh") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (var argument in new[] { "-NoProfile", "-File", Path.Combine(source.FullName, relative), "-SourceRoot", source.FullName, "-SqlPath", path })
                start.ArgumentList.Add(argument);
            start.Environment["FLUXKNOWLEDGE_SOURCE_CLEANUP_DISPOSABLE_SQL"] = fixture.ConnectionString;
            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            try { await process.WaitForExitAsync(deadline.Token); }
            catch { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); throw; }
            var transcript = await output + await error;
            testOutput.WriteLine(transcript);
            Assert.True(process.ExitCode == 0, transcript);
        }
        finally { File.Delete(path); }
    }
}
