using System.Diagnostics;
using FluxKnowledge.Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Visibility;

public sealed class CodeDisclosureMigrationDeploymentTests(NativeSqlServerFixture fixture) : IClassFixture<NativeSqlServerFixture>
{
    [NativeSqlServerFact]
    public async Task Incremental_disclosure_migration_executes_atomic_exact_SQL_and_refuses_a_changed_or_partial_contract()
    {
        var factory = SqlTestData.CreateFactory(fixture);
        await using var db = await factory.CreateDbContextAsync();
        const string baseline = "20260927202655_AddCorpusRebuildSupersession";
        const string target = "20261003220411_AddCanonicalCodeDisclosureProof";
        await db.GetService<IMigrator>().MigrateAsync(baseline);
        var script = db.GetService<IMigrator>().GenerateScript(baseline, target, MigrationsSqlGenerationOptions.Idempotent);
        var source = new DirectoryInfo(AppContext.BaseDirectory);
        const string relative = "tests/native/code-disclosure-migration-sql.ps1";
        while (source is not null && !File.Exists(Path.Combine(source.FullName, relative))) source = source.Parent;
        Assert.NotNull(source);
        var path = Path.Combine(Path.GetTempPath(), $"flux-code-disclosure-{Guid.NewGuid():N}.sql");
        try
        {
            // Match dotnet ef's reviewed output encoding, including its UTF-8 BOM.
            await File.WriteAllTextAsync(path, script, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            var start = new ProcessStartInfo("pwsh") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (var argument in new[] { "-NoProfile", "-File", Path.Combine(source.FullName, relative), "-SourceRoot", source.FullName, "-SqlPath", path })
                start.ArgumentList.Add(argument);
            start.Environment["FLUXKNOWLEDGE_CODE_PROOF_DISPOSABLE_SQL"] = fixture.ConnectionString;
            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            try { await process.WaitForExitAsync(deadline.Token); }
            catch { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); throw; }
            Assert.True(process.ExitCode == 0, await output + await error);
        }
        finally { File.Delete(path); }
    }
}
