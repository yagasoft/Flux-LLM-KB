using System.Diagnostics;
using System.Runtime.CompilerServices;
using FluxKnowledge.Cli.Commands;
using FluxKnowledge.Integration.Tests.Support;
using FluxKnowledge.Web;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Indexing;

[Collection("sql-full-text")]
public sealed class IndexFormatAdmissionTests(NativeSqlServerFixture fixture) : IClassFixture<NativeSqlServerFixture>
{
    [NativeSqlServerTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Offline_code_update_binds_actual_schema_and_current_projection_without_conversion(bool journalSchema)
    {
        await using var environment = await SqlToUsearchRebuildTests.PipelineEnvironment.CreateAsync(fixture,
            "The existing segmented projection must survive a code-only update.", segmented: true);
        await using var db = await SqlTestData.CreateFactory(fixture).CreateDbContextAsync();
        var migrator = db.GetService<IMigrator>();
        if (!journalSchema) await migrator.MigrateAsync("20261024061418_AddDocumentOcrExecutionLifecycle");
        try
        {
            var root = SourceRoot();
            var start = new ProcessStartInfo("pwsh") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var value in new[] { "-NoProfile", "-File", Path.Combine(root, "tests", "native", "offline-code-sql-binding.ps1"),
                "-SourceRoot", root, "-ExpectedDatabase", fixture.DatabaseName }) start.ArgumentList.Add(value);
            start.Environment["FLUXKNOWLEDGE_OFFLINE_CODE_TEST_SQL"] = fixture.ConnectionString;
            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync(); var errors = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch { if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); } throw; }
            Assert.True(process.ExitCode == 0, (await output) + (await errors));
            Assert.Contains("PASS:", await output);
        }
        finally
        {
            if (!journalSchema) await migrator.MigrateAsync();
        }
    }

    [NativeSqlServerFact]
    public async Task Actual_admission_refuses_incompatible_predecessors_and_excludes_format_changes_until_start_returns()
    {
        await using var environment = await SqlToUsearchRebuildTests.PipelineEnvironment.CreateAsync(fixture,
            "Admission binds the exact converted generation and configuration.", segmented: true);
        var scratch = Path.Combine(Path.GetTempPath(), "FluxKnowledgeIndexAdmission_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        try
        {
            var root = SourceRoot();
            var start = new ProcessStartInfo("pwsh") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var value in new[] { "-NoProfile", "-File", Path.Combine(root, "tests", "native", "index-format-admission.ps1"),
                "-SourceRoot", root, "-WebAssembly", typeof(WebHostComposition).Assembly.Location, "-CliAssembly", typeof(IndexProjectionCommand).Assembly.Location,
                "-ScratchRoot", scratch, "-ExpectedDatabase", fixture.DatabaseName }) start.ArgumentList.Add(value);
            start.Environment["FLUXKNOWLEDGE_INDEX_ADMISSION_TEST_SQL"] = fixture.ConnectionString;
            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync(); var errors = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch { if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); } throw; }
            Assert.True(process.ExitCode == 0, (await output) + (await errors));
            Assert.Contains("PASS:", await output);
        }
        finally { Directory.Delete(scratch, recursive: true); }
    }

    private static string SourceRoot([CallerFilePath] string source = "") =>
        Directory.GetParent(Path.GetDirectoryName(source)!)!.Parent!.Parent!.FullName;
}
