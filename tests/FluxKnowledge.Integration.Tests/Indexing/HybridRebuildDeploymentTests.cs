using System.Diagnostics;
using System.Text;
using FluxKnowledge.Application.Documents;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using FluxKnowledge.Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Indexing;

[Collection("sql-full-text")]
public sealed class HybridRebuildDeploymentTests(NativeSqlServerFixture fixture) : IClassFixture<NativeSqlServerFixture>
{
    [NativeSqlServerFact]
    public Task Actual_updater_replacement_reconciles_sql_receipts_and_retains_deny_all_hold_on_failure() => RunScriptAsync("replacement");

    [NativeSqlServerFact]
    public Task Forward_patch_retains_operation_epoch_and_checkpoint_authority_through_failure_replay() => RunScriptAsync("patch");
    [NativeSqlServerFact]
    public async Task Actual_updater_drain_holds_admission_and_fails_closed_after_session_loss_or_uncertain_capacity()
    {
        await SqlTestData.ClearPhase3SourceDataAsync(fixture);
        await using var context = SqlTestData.CreateFactory(fixture).CreateDbContext();
        context.GpuCapacitySlots.Add(new GpuCapacitySlotEntity { SlotKey=PaddleOcrVlmRuntimeContract.CapacitySlotKey, UpdatedAtUtc=DateTimeOffset.UtcNow });
        await context.SaveChangesAsync();
        await RunScriptAsync("drain");
    }

    [NativeSqlServerFact]
    public async Task Actual_updater_executes_the_reviewed_idempotent_script_and_replays_without_changing_preserved_inputs()
    {
        await SqlTestData.ClearPhase3SourceDataAsync(fixture);
        await using var context = SqlTestData.CreateFactory(fixture).CreateDbContext();
        var migrator = context.GetService<IMigrator>();
        const string baseline = "20260924125920_AddCorpusChunkFullTextIndex";
        const string target = "20260927202655_AddCorpusRebuildSupersession";
        var path = Path.Combine(Path.GetTempPath(), $"hybrid-reviewed-schema-{Guid.NewGuid():N}.sql");
        try
        {
            await File.WriteAllTextAsync(path, migrator.GenerateScript(baseline, target, MigrationsSqlGenerationOptions.Idempotent), new UTF8Encoding(true));
            await migrator.MigrateAsync(baseline);
            await RunScriptAsync("schema", path);
        }
        finally { await context.Database.MigrateAsync(); File.Delete(path); }
    }

    private async Task RunScriptAsync(string mode, string? migrationScript = null)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "scripts", "deploy", "update-native-iis-incremental.ps1"))) root=root.Parent;
        Assert.NotNull(root);
        var start = new ProcessStartInfo("pwsh") { UseShellExecute=false, CreateNoWindow=true, RedirectStandardOutput=true, RedirectStandardError=true };
        var script = mode switch
        {
            "replacement" => "hybrid-rebuild-replacement-contract.ps1",
            "patch" => "hybrid-rebuild-forward-patch-contract.ps1",
            _ => "hybrid-rebuild-sql-integration.ps1"
        };
        foreach (var value in new[] { "-NoProfile", "-File", Path.Combine(root.FullName,"tests","native",script), "-SourceRoot", root.FullName, "-Mode", mode }) start.ArgumentList.Add(value);
        if (migrationScript is not null) { start.ArgumentList.Add("-MigrationScript"); start.ArgumentList.Add(migrationScript); }
        start.Environment["FLUXKNOWLEDGE_HYBRID_DISPOSABLE_SQL"] = fixture.ConnectionString;
        using var process = Process.Start(start)!;
        var output=process.StandardOutput.ReadToEndAsync();
        var errors=process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch { if (!process.HasExited) process.Kill(true); throw; }
        Assert.True(process.ExitCode == 0, (await output) + (await errors));
    }
}
