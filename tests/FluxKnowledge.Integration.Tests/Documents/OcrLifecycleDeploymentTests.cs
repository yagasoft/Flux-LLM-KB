using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Documents;

public sealed class OcrLifecycleDeploymentTests(NativeSqlServerFixture fixture) : IClassFixture<NativeSqlServerFixture>
{
    [Fact]
    public Task Physical_verifier_and_release_failure_boundaries_fail_closed_and_replay_safely() => RunScriptAsync("ocr-maintenance-contract.ps1");

    [Fact]
    public Task Actual_updater_preflight_dispatch_and_completed_replay_are_bound_and_fail_closed() => RunScriptAsync("ocr-updater-contract.ps1");

    [NativeSqlServerFact]
    public async Task Pinned_migration_is_additive_atomic_replayable_and_rejects_schema_drift()
    {
        await using var db = SqlTestData.CreateFactory(fixture).CreateDbContext();
        var migrator = db.GetService<IMigrator>();
        const string baseline = "20261021010911_AddIndexRecoveryReferenceAudit";
        const string target = "20261024061418_AddDocumentOcrExecutionLifecycle";
        var root = FluxKnowledge.Testing.RepositoryFiles.Root();
        var expected = migrator.GenerateScript(baseline, target).Replace("\r\n", "\n");
        var actual = await File.ReadAllTextAsync(Path.Combine(root, "scripts/deploy/ocr-lifecycle-schema-up.sql"));
        Assert.Equal(expected, actual);
        Assert.Equal("3A66996FC45853B16E7329B5D51C1E10DDD0F05F9AC842C702591E516115DC20",
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(actual))));
        await migrator.MigrateAsync(baseline);
        try { await RunScriptAsync("ocr-lifecycle-migration-rehearsal.ps1", fixture.ConnectionString); }
        finally { await db.Database.MigrateAsync(); }
    }

    internal static async Task RunScriptAsync(string name, string? connection = null)
    {
        var root = FluxKnowledge.Testing.RepositoryFiles.Root();
        var start = new ProcessStartInfo("pwsh") { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = root };
        foreach (var argument in new[] { "-NoProfile", "-File", Path.Combine(root, "tests/native", name), "-SourceRoot", root }) start.ArgumentList.Add(argument);
        start.Environment["FLUXKNOWLEDGE_OCR_CONTRACT_ASSEMBLY"] = typeof(FluxKnowledge.Application.Documents.DocumentOcrExecutionLease).Assembly.Location;
        if (connection is not null) start.Environment["FLUXKNOWLEDGE_OCR_DISPOSABLE_SQL"] = connection;
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(); var errors = process.StandardError.ReadToEndAsync();
        using var budget = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        try { await process.WaitForExitAsync(budget.Token); }
        catch { if (!process.HasExited) process.Kill(entireProcessTree: true); throw; }
        Assert.True(process.ExitCode == 0, await output + await errors);
    }
}
