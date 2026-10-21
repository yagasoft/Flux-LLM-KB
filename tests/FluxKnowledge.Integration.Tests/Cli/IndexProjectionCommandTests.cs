using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluxKnowledge.Cli.Commands;
using FluxKnowledge.Application.Indexing;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.Usearch;
using FluxKnowledge.Integrations.Windows;
using FluxKnowledge.Integration.Tests.Indexing;
using FluxKnowledge.Integration.Tests.Support;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Cli;

[Collection("sql-full-text")]
public sealed class IndexProjectionCommandTests(NativeSqlServerFixture fixture) : IClassFixture<NativeSqlServerFixture>
{
    [NativeSqlServerFact]
    public async Task A_recovery_reference_report_reads_no_database_and_does_not_seal_state()
    {
        await using var environment = await SeedAsync();
        var recovery = System.IO.Path.Combine(environment.IndexRoot, "report-recovery"); Directory.CreateDirectory(recovery);
        await using var db = await environment.Factory.CreateDbContextAsync();
        var before = await db.IndexRecoveryAudit.AsNoTracking().SingleAsync();
        await using var child = await StartAsync(environment,
            ["report", "--recovery-root", recovery, "--index-root", environment.IndexRoot], command: "index-recovery", connectionOverride: "not-a-connection");
        var result = await child.FinishAsync(); Assert.Equal(0, result.ExitCode); Assert.Empty(result.Error);
        using var json = JsonDocument.Parse(result.Output);
        Assert.False(json.RootElement.GetProperty("SqlAuditSealed").GetBoolean());
        var after = await db.IndexRecoveryAudit.AsNoTracking().SingleAsync();
        Assert.Equal(before.RowVersion, after.RowVersion); Assert.Equal(before.IsValid, after.IsValid);
    }

    [NativeSqlServerFact]
    public async Task The_privileged_audit_command_seals_bounded_metadata_and_clears_only_the_resolved_current_failure()
    {
        await using var environment = await SeedAsync();
        var recovery = System.IO.Path.Combine(environment.IndexRoot, "operator-recovery"); Directory.CreateDirectory(recovery);
        string[] arguments = ["audit", "--recovery-root", recovery, "--index-root", environment.IndexRoot];
        await using (var child = await StartAsync(environment, arguments, command: "index-recovery"))
        {
            var first = await child.FinishAsync();
            Assert.Equal(0, first.ExitCode); Assert.Empty(first.Error);
            using var json = JsonDocument.Parse(first.Output);
            Assert.True(json.RootElement.GetProperty("SqlAuditSealed").GetBoolean());
            Assert.Equal(0, json.RootElement.GetProperty("PinCount").GetInt32());
        }
        var store = new SqlIndexRecoveryReferences(environment.Factory, TimeProvider.System, new WindowsInteractiveGpuOwnerProbe());
        var unknown = System.IO.Path.Combine(recovery, "unrecognised.json");
        await using (var writer = await store.BeginWriteAsync(recovery, CancellationToken.None))
        { await File.WriteAllTextAsync(unknown, "{\"private\":\"never-echo-packet-content\"}"); await writer.CompleteAsync(CancellationToken.None); }
        await using (var child = await StartAsync(environment, arguments, command: "index-recovery"))
        {
            var refusal = await child.FinishAsync();
            Assert.Equal(1, refusal.ExitCode); Assert.Empty(refusal.Output);
            Assert.DoesNotContain("never-echo-packet-content", refusal.Error);
            using var json = JsonDocument.Parse(refusal.Error);
            Assert.Equal("unrecognised.json", json.RootElement.GetProperty("PacketPath").GetString());
        }
        await using (var writer = await store.BeginWriteAsync(recovery, CancellationToken.None))
        { File.Delete(unknown); await writer.CompleteAsync(CancellationToken.None); }
        await using (var child = await StartAsync(environment, arguments, command: "index-recovery"))
            Assert.Equal(0, (await child.FinishAsync()).ExitCode);
        await using var db = await environment.Factory.CreateDbContextAsync();
        var audit = await db.IndexRecoveryAudit.SingleAsync();
        Assert.True(audit.IsValid); Assert.Null(audit.FailurePath);
        Assert.Empty(await db.IndexRecoveryWriters.ToArrayAsync());
        Assert.True(await db.Vectors.AnyAsync());
    }

    [NativeSqlServerFact]
    public async Task A_legacy_corpus_reset_cannot_plan_against_a_segmented_projection()
    {
        await using var environment = await SqlToUsearchRebuildTests.PipelineEnvironment.CreateAsync(fixture,
            "Format two remains intact when a legacy reset is requested.", segmented: true);
        var legacy = new SqlCorpusRebuildStore(environment.Factory);
        var failure = await Assert.ThrowsAsync<CorpusRebuildRefusalException>(async () =>
            await legacy.ReadPlanAsync(Guid.NewGuid(), new("another-profile", 1024), new string('b', 64), CancellationToken.None));
        Assert.Equal("corpus-rebuild-requires-index-format-1", failure.Message);
        await using var child = await StartAsync(environment, ["plan", "--operation", Guid.NewGuid().ToString("D")], command: "corpus-rebuild");
        var response = await child.FinishAsync();
        Assert.Equal(1, response.ExitCode);
        Assert.Equal("corpus-rebuild-requires-index-format-1", response.Error.Trim());
        await SqlToUsearchRebuildTests.AssertSegmentedOracleAsync(environment);
    }

    [NativeSqlServerFact]
    public async Task A_legacy_corpus_reset_plan_cannot_clear_a_later_segmented_conversion()
    {
        await using var environment = await SeedAsync();
        var legacy = new SqlCorpusRebuildStore(environment.Factory);
        var reset = await legacy.ReadPlanAsync(Guid.NewGuid(), new("another-profile", 1024), new string('b', 64), CancellationToken.None);
        var conversion = await PlanAsync(environment);
        try
        {
            Assert.Equal(0, (await RunAsync(environment, conversion)).ExitCode);
            await using var db = await environment.Factory.CreateDbContextAsync();
            db.GpuCapacitySlots.Add(new() { SlotKey = "format-test", UpdatedAtUtc = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
            var active = await db.IndexState.AsNoTracking().SingleAsync();
            var failure = await Assert.ThrowsAsync<CorpusRebuildRefusalException>(async () =>
                await legacy.CommitAsync(reset, "format-test", CancellationToken.None));
            Assert.Equal("corpus-rebuild-requires-index-format-1", failure.Message);
            var current = await db.IndexState.AsNoTracking().SingleAsync();
            Assert.Equal(active.ActiveIndexGenerationId, current.ActiveIndexGenerationId);
            Assert.Equal(active.CorpusEpoch, current.CorpusEpoch);
            Assert.Equal(2, current.RequiredIndexFormat);
            Assert.Empty(await db.CorpusRebuildOperations.ToArrayAsync());
            await SqlToUsearchRebuildTests.AssertSegmentedOracleAsync(environment);
        }
        finally { File.Delete(conversion.Path); }
    }

    [NativeSqlServerFact]
    public async Task A_plan_cannot_relocate_the_sql_projection_to_an_unrelated_index_root()
    {
        await using var environment = await SeedAsync();
        var other = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "FluxKnowledgeOperator_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(other);
        try
        {
            await using var child = await StartAsync(environment, ["plan", "--operation-id", Guid.NewGuid().ToString("D"), "--target-format", "2"], other);
            Assert.Equal(1, (await child.FinishAsync()).ExitCode);
            await using var db = await environment.Factory.CreateDbContextAsync();
            Assert.Empty(await db.IndexPublicationCandidates.ToArrayAsync());
            Assert.Empty(Directory.EnumerateFileSystemEntries(other));
        }
        finally { Directory.Delete(other); }
    }

    [NativeSqlServerFact]
    public async Task The_operator_plans_without_mutation_and_runs_and_replays_a_recorded_conversion()
    {
        await using var environment = await SeedAsync();
        var plan = await PlanAsync(environment);
        try
        {
            await using var db = await environment.Factory.CreateDbContextAsync();
            Assert.Empty(await db.IndexPublicationCandidates.ToArrayAsync());
            var first = await RunAsync(environment, plan);
            Assert.Equal(0, first.ExitCode);
            var replay = await RunAsync(environment, plan);
            Assert.Equal(0, replay.ExitCode);
            Assert.Equal(first.Output.Trim(), replay.Output.Trim());
            Assert.Equal(2, await db.IndexState.Select(value => value.RequiredIndexFormat).SingleAsync());
            await SqlToUsearchRebuildTests.AssertSegmentedOracleAsync(environment);
        }
        finally { File.Delete(plan.Path); }
    }

    [NativeSqlServerFact]
    public async Task A_process_killed_after_placement_before_sql_seal_resumes_the_same_immutable_files()
    {
        await using var environment = await SeedAsync();
        var plan = await PlanAsync(environment);
        try
        {
            string placed;
            byte[] bytes;
            DateTime written;
            await using (var db = await environment.Factory.CreateDbContextAsync())
            {
                await db.Database.OpenConnectionAsync();
                await using var transaction = await db.Database.BeginTransactionAsync();
                await db.Database.ExecuteSqlRawAsync("SELECT TOP(1) [Id] FROM [IndexSegments] WITH(TABLOCKX,HOLDLOCK);");
                await using var child = await StartAsync(environment, RunArguments(plan));
                var timer = Stopwatch.StartNew();
                while (true)
                {
                    var directory = System.IO.Path.Combine(environment.IndexRoot, "segments");
                    placed = Directory.Exists(directory)
                        ? Directory.EnumerateFiles(directory, "index.usearch", SearchOption.AllDirectories).FirstOrDefault() ?? ""
                        : "";
                    if (placed.Length != 0) break;
                    Assert.False(child.Process.HasExited, "The operator exited before placing its first segment.");
                    Assert.True(timer.Elapsed < TimeSpan.FromSeconds(15), "The isolated placement checkpoint was not reached.");
                    await Task.Delay(50);
                }
                await child.KillOwnedAsync();
                Assert.Equal(0, await db.IndexSegments.CountAsync());
                var interrupted = await db.IndexPublicationCandidates.SingleAsync();
                Assert.Equal(child.Process.Id, interrupted.OwnerProcessId);
                Assert.True(interrupted.WorklistComplete);
                Assert.Equal(0, interrupted.State);
                bytes = await File.ReadAllBytesAsync(placed);
                written = File.GetLastWriteTimeUtc(placed);
                await transaction.RollbackAsync();
            }
            var resumed = await RunAsync(environment, plan);
            Assert.Equal(0, resumed.ExitCode);
            Assert.Equal(bytes, await File.ReadAllBytesAsync(placed));
            Assert.Equal(written, File.GetLastWriteTimeUtc(placed));
            await SqlToUsearchRebuildTests.AssertSegmentedOracleAsync(environment);
        }
        finally { File.Delete(plan.Path); }
    }

    [NativeSqlServerTheory]
    [InlineData("hash")]
    [InlineData("root")]
    [InlineData("replaced-root")]
    [InlineData("database")]
    [InlineData("budget")]
    [InlineData("plan-link")]
    public async Task A_changed_plan_hash_or_physical_root_is_refused_before_preparation(string change)
    {
        await using var environment = await SeedAsync();
        var plan = await PlanAsync(environment);
        var other = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "FluxKnowledgeOperator_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(other);
        string? relocated = null;
        var link = plan.Path + ".link";
        try
        {
            if (change is "database" or "budget")
            {
                var json = JsonNode.Parse(await File.ReadAllTextAsync(plan.Path))!;
                if (change == "database") json["Database"] = "a-different-database";
                else json["Budgets"]!["MaximumVectors"] = 4096;
                await File.WriteAllTextAsync(plan.Path, json.ToJsonString());
                plan = plan with { Sha256 = Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(plan.Path))) };
            }
            if (change == "replaced-root")
            {
                var saved = System.IO.Path.Combine(other, "original-index");
                Directory.Move(environment.IndexRoot, saved); relocated = saved;
                Directory.CreateDirectory(environment.IndexRoot);
            }
            var attempted = change == "hash" ? plan with { Sha256 = new string('0', 64) } : plan;
            if (change == "plan-link") { File.CreateSymbolicLink(link, plan.Path); attempted = plan with { Path = link }; }
            await using var child = await StartAsync(environment, RunArguments(attempted), change == "root" ? other : null);
            var result = await child.FinishAsync();
            Assert.Equal(1, result.ExitCode);
            await using var db = await environment.Factory.CreateDbContextAsync();
            Assert.Empty(await db.IndexPublicationCandidates.ToArrayAsync());
            Assert.Equal(1, await db.IndexState.Select(value => value.RequiredIndexFormat).SingleAsync());
            Assert.Empty(Directory.EnumerateFileSystemEntries(change == "replaced-root" ? environment.IndexRoot : other));
        }
        finally
        {
            File.Delete(link); File.Delete(plan.Path);
            if (relocated is not null) { Directory.Delete(environment.IndexRoot); Directory.Move(relocated, environment.IndexRoot); }
            Directory.Delete(other);
        }
    }

    [NativeSqlServerFact]
    public async Task A_lost_result_acknowledgement_reports_the_durable_operation_and_replays_without_reactivation()
    {
        await using var environment = await SeedAsync();
        var plan = await PlanAsync(environment);
        try
        {
            using var error = new StringWriter();
            var exit = await IndexProjectionCommand.ExecuteAsync(RunArguments(plan), environment.Factory,
                UsearchIndexOptions.FromConfiguredRoot(environment.IndexRoot), new FailedAcknowledgement(), error);
            Assert.Equal(1, exit);
            await using var db = await environment.Factory.CreateDbContextAsync();
            var active = await db.IndexState.Select(value => value.ActiveIndexGenerationId).SingleAsync();
            Assert.NotNull(active);
            Assert.Equal(2, await db.IndexPublicationCandidates.Select(value => value.State).SingleAsync());
            Assert.Contains("index-projection-operation-recorded-result-delivery-failed-resume-same-plan", error.ToString());
            Assert.Equal(0, (await RunAsync(environment, plan)).ExitCode);
            Assert.Equal(active, await db.IndexState.Select(value => value.ActiveIndexGenerationId).SingleAsync());
        }
        finally { File.Delete(plan.Path); }
    }

    private Task<SqlToUsearchRebuildTests.PipelineEnvironment> SeedAsync() =>
        SqlToUsearchRebuildTests.PipelineEnvironment.CreateAsync(fixture,
            "The isolated operator converts this retained source without loading a model.", segmented: true, initialIndexFormat: 1);

    private async Task<PlanFile> PlanAsync(SqlToUsearchRebuildTests.PipelineEnvironment environment)
    {
        await using var child = await StartAsync(environment, ["plan", "--operation-id", Guid.NewGuid().ToString("D"), "--target-format", "2"]);
        var result = await child.FinishAsync();
        Assert.Equal(0, result.ExitCode);
        using var json = JsonDocument.Parse(result.Output);
        Assert.Equal(2, json.RootElement.GetProperty("Plan").GetProperty("TargetFormat").GetInt32());
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "FluxKnowledgeProjectionPlan_" + Guid.NewGuid().ToString("N") + ".json");
        await File.WriteAllTextAsync(path, result.Output);
        return new(path, Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(path))));
    }

    private async Task<(int ExitCode, string Output, string Error)> RunAsync(SqlToUsearchRebuildTests.PipelineEnvironment environment, PlanFile plan)
    {
        await using var child = await StartAsync(environment, RunArguments(plan));
        return await child.FinishAsync();
    }

    private static string[] RunArguments(PlanFile plan) => ["run", "--plan", plan.Path, "--sha256", plan.Sha256];

    private async Task<OwnedChild> StartAsync(SqlToUsearchRebuildTests.PipelineEnvironment environment, string[] arguments, string? root = null,
        string command = "index-projection", string? connectionOverride = null)
    {
        await using var db = await environment.Factory.CreateDbContextAsync();
        var connection = db.Database.GetConnectionString()!;
        Assert.Equal(fixture.DatabaseName, new SqlConnectionStringBuilder(connection).InitialCatalog);
        var testAssembly = typeof(IndexProjectionCommandTests).Assembly.Location;
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { "exec", "--runtimeconfig", System.IO.Path.ChangeExtension(testAssembly, ".runtimeconfig.json"),
                     "--depsfile", System.IO.Path.ChangeExtension(testAssembly, ".deps.json"), typeof(CorpusRebuildCommand).Assembly.Location, command }.Concat(arguments))
            start.ArgumentList.Add(arg);
        start.Environment["ConnectionStrings__FluxKnowledge"] = connectionOverride ?? connection;
        start.Environment["Usearch__RootPath"] = root ?? environment.IndexRoot;
        start.Environment["DOTNET_ENVIRONMENT"] = "Staging";
        return new(Process.Start(start) ?? throw new InvalidOperationException("The isolated operator did not start."));
    }

    private sealed record PlanFile(string Path, string Sha256);

    private sealed class FailedAcknowledgement : TextWriter
    {
        public override Encoding Encoding => Encoding.UTF8;
        public override Task WriteLineAsync(string? value) => Task.FromException(new IOException("The isolated result channel closed."));
    }

    private sealed class OwnedChild(Process process) : IAsyncDisposable
    {
        public Process Process { get; } = process;
        private readonly int _pid = process.Id;
        private readonly DateTime _started = process.StartTime.ToUniversalTime();
        private readonly Task<string> _output = process.StandardOutput.ReadToEndAsync();
        private readonly Task<string> _error = process.StandardError.ReadToEndAsync();

        public async Task<(int ExitCode, string Output, string Error)> FinishAsync()
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await Process.WaitForExitAsync(timeout.Token);
            var output = await _output; var error = await _error;
            Assert.True(output.Length <= 65536 && error.Length <= 4096, "The operator response exceeded its fixture bounds.");
            return (Process.ExitCode, output, error);
        }

        public async Task KillOwnedAsync()
        {
            if (Process.HasExited) return;
            Assert.Equal(_pid, Process.Id); Assert.Equal(_started, Process.StartTime.ToUniversalTime());
            Process.Kill(entireProcessTree: true);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await Process.WaitForExitAsync(timeout.Token);
            Assert.True(Process.HasExited);
        }

        public async ValueTask DisposeAsync()
        {
            await KillOwnedAsync();
            await _output; await _error;
            Process.Dispose();
        }
    }
}
