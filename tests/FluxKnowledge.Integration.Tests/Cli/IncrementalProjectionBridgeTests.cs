using System.Reflection;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using FluxKnowledge.Cli.Commands;
using FluxKnowledge.Infrastructure.Usearch;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Integration.Tests.Indexing;
using FluxKnowledge.Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Cli;

[Collection("sql-full-text")]
public sealed class IncrementalProjectionBridgeTests(NativeSqlServerFixture fixture) : IClassFixture<NativeSqlServerFixture>
{
    [NativeSqlServerFact]
    public async Task Actual_updater_callbacks_prepare_offline_then_resume_only_the_matching_result()
    {
        var root = Path.Combine(Path.GetTempPath(), "FluxProjectionDeployment_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await using var environment = await SqlToUsearchRebuildTests.PipelineEnvironment.CreateAsync(fixture,
                "A controlled deployment converts current canonical membership without source ingestion.", segmented: true, initialIndexFormat: 1,
                indexRootOverride: Path.Combine(root, "live", "Data", "Index"));
            await using var db = await environment.Factory.CreateDbContextAsync();
            var migrator = db.GetService<IMigrator>();
            await migrator.MigrateAsync("20261014134909_AddResumableSourceDeletionCleanup");
            try
            {
                var converterOwner = await RunStageAsync(false);
                Assert.Empty(await db.IndexRecoveryWriters.ToArrayAsync());
                Assert.Equal(converterOwner, (await db.IndexPublicationCandidates.SingleAsync(value => value.BuildPurpose == 1)).OwnerProcessId);
                await SqlToUsearchRebuildTests.AssertSegmentedOracleAsync(environment);
                var nativeFiles = Directory.GetFiles(Path.Combine(environment.IndexRoot, "segments"), "*", SearchOption.AllDirectories)
                    .ToDictionary(path => path, path => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));
                await new SqlIndexRecoveryReferences(environment.Factory, TimeProvider.System,
                    new FluxKnowledge.Integrations.Windows.WindowsInteractiveGpuOwnerProbe()).AuditAsync(
                    Path.Combine(root, "live", "Recovery"), environment.IndexRoot, CancellationToken.None);
                Assert.True((await db.IndexRecoveryAudit.SingleAsync()).IsValid);
                await RunStageAsync(true);
                foreach (var file in nativeFiles) Assert.Equal(file.Value, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file.Key))));
                await environment.AddAndPumpAsync("A normal publication follows the controlled conversion.");
                await SqlToUsearchRebuildTests.AssertSegmentedOracleAsync(environment);

                async Task<int> RunStageAsync(bool resume)
                {
                var start = new ProcessStartInfo("pwsh") { UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardOutput = true, RedirectStandardError = true };
                foreach (var value in new[] { "-NoProfile", "-File", Path.Combine(SourceRoot(), "tests", "native", "segmented-index-iis-staging.ps1"),
                    "-SourceRoot", SourceRoot(), "-OperatorRoot", Path.GetDirectoryName(typeof(IndexProjectionCommand).Assembly.Location)!,
                    "-ScratchRoot", root, "-ExpectedDatabase", fixture.DatabaseName }) start.ArgumentList.Add(value);
                if (resume) start.ArgumentList.Add("-ResumeOfflineMaintenance");
                start.Environment["FLUXKNOWLEDGE_PROJECTION_DEPLOYMENT_TEST_SQL"] = fixture.ConnectionString;
                using var child = Process.Start(start)!;
                var output = child.StandardOutput.ReadToEndAsync(); var error = child.StandardError.ReadToEndAsync();
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
                try { await child.WaitForExitAsync(deadline.Token); }
                catch { if (!child.HasExited) { child.Kill(entireProcessTree: true); await child.WaitForExitAsync(); } throw; }
                Assert.True(child.ExitCode == 0, (await output) + (await error));
                Assert.Contains("PASS:", await output);
                return child.Id;
                }
            }
            finally { await migrator.MigrateAsync(); }
        }
        finally
        {
            if (Directory.Exists(root))
            {
                // Git marks its own disposable object files read-only on Windows.
                foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                    File.SetAttributes(file, File.GetAttributes(file) & ~FileAttributes.ReadOnly);
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [NativeSqlServerFact]
    public async Task Actual_PowerShell_loads_the_operator_and_owns_the_native_conversion_in_its_own_process()
    {
        await using var environment = await SqlToUsearchRebuildTests.PipelineEnvironment.CreateAsync(fixture,
            "The native converter must belong to the actual PowerShell lifecycle process.", segmented: true, initialIndexFormat: 1);
        using var packet = await Packet.CreateAsync(environment);
        var source = SourceRoot();
        var start = new ProcessStartInfo("pwsh") { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var value in new[] { "-NoProfile", "-File", Path.Combine(source, "tests", "native", "incremental-projection-operator.ps1"),
            "-SourceRoot", source, "-OperatorRoot", Path.GetDirectoryName(typeof(IndexProjectionCommand).Assembly.Location)!,
            "-IndexRoot", environment.IndexRoot, "-Configuration", packet.Configuration, "-Hold", packet.Hold,
            "-Bindings", packet.WriteBindings(), "-MutexName", packet.MutexName, "-ExpectedDatabase", fixture.DatabaseName }) start.ArgumentList.Add(value);
        using var child = Process.Start(start)!;
        var output = child.StandardOutput.ReadToEndAsync(); var error = child.StandardError.ReadToEndAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        try { await child.WaitForExitAsync(deadline.Token); }
        catch { if (!child.HasExited) { child.Kill(entireProcessTree: true); await child.WaitForExitAsync(); } throw; }
        Assert.True(child.ExitCode == 0, (await output) + (await error));
        Assert.Contains("PASS:", await output);
        await using var db = await environment.Factory.CreateDbContextAsync();
        Assert.Equal(child.Id, (await db.IndexPublicationCandidates.SingleAsync(value => value.BuildPurpose == 1)).OwnerProcessId);
        await SqlToUsearchRebuildTests.AssertSegmentedOracleAsync(environment);
    }

    [NativeSqlServerFact]
    public async Task In_process_conversion_holds_the_updater_lease_and_refuses_replay_over_a_later_publication()
    {
        await using var environment = await SqlToUsearchRebuildTests.PipelineEnvironment.CreateAsync(fixture,
            "A model-free conversion must retain the actual lifecycle owner.", segmented: true, initialIndexFormat: 1);
        using var packet = await Packet.CreateAsync(environment);
        var observations = 0;
        using (var capability = packet.Begin(() => observations++))
        {
            Assert.False(Task.Run(() => TryAcquire(packet.MutexName)).GetAwaiter().GetResult());
            Assert.Throws<IOException>(() => File.WriteAllText(packet.Configuration, "changed"));
            Assert.Throws<IOException>(() => File.WriteAllText(packet.Hold, "changed"));
            var result = packet.Run(capability);
            using var json = JsonDocument.Parse(result);
            Assert.Equal(2, json.RootElement.GetProperty("RecordedGeneration").GetProperty("FormatVersion").GetInt32());
            Assert.True(observations >= 3);
            Assert.Equal("index-projection-capability-consumed", Assert.Throws<InvalidOperationException>(() => packet.Run(capability)).Message);
        }
        Assert.True(await Task.Run(() => TryAcquire(packet.MutexName)));
        await SqlToUsearchRebuildTests.AssertSegmentedOracleAsync(environment);
        await environment.AddAndPumpAsync("A newer valid publication must survive replay of the old conversion.");
        var later = await environment.ActiveGenerationAsync();
        using (var replay = packet.Begin(() => { }))
            Assert.Equal("index-projection-recorded-result-is-not-current", Assert.Throws<InvalidOperationException>(() => packet.Run(replay)).Message);
        Assert.Equal(later.Id, (await environment.ActiveGenerationAsync()).Id);
    }

    [NativeSqlServerFact]
    public async Task Changed_bindings_or_quiescence_refuse_before_creating_a_conversion_candidate()
    {
        await using var environment = await SqlToUsearchRebuildTests.PipelineEnvironment.CreateAsync(fixture,
            "Do not create a candidate after lifecycle or file binding drift.", segmented: true, initialIndexFormat: 1);
        using var packet = await Packet.CreateAsync(environment);
        File.AppendAllText(packet.Hold, " ");
        Assert.Equal("index-projection-hold-binding-changed", Assert.Throws<InvalidOperationException>(() => packet.Begin(() => { })).Message);
        File.WriteAllText(packet.Hold, "\"isolated-index-update\"");
        var stopped = true;
        using (var capability = packet.Begin(() => { if (!stopped) throw new InvalidOperationException("test-owner-no-longer-stopped"); }))
        {
            stopped = false;
            Assert.Equal("test-owner-no-longer-stopped", Assert.Throws<InvalidOperationException>(() => packet.Run(capability)).Message);
        }
        await using var db = await environment.Factory.CreateDbContextAsync();
        Assert.Empty(await db.IndexPublicationCandidates.Where(value => value.BuildPurpose != 0).ToArrayAsync());
        Assert.Equal(1, (await db.IndexState.SingleAsync()).RequiredIndexFormat);
        Assert.Equal("index-projection-capability-unrecognised", Assert.Throws<InvalidOperationException>(() => packet.Run(new object())).Message);
    }

    private static bool TryAcquire(string name)
    {
        using var mutex = new Mutex(false, name);
        if (!mutex.WaitOne(0)) return false;
        mutex.ReleaseMutex(); return true;
    }

    private static string SourceRoot([CallerFilePath] string path = "") => Directory.GetParent(Path.GetDirectoryName(path)!)!.Parent!.Parent!.FullName;

    private sealed class Packet : IDisposable
    {
        private readonly string _directory;
        private readonly string _index;
        private readonly string _plan;
        private readonly string _bindings;
        public string Configuration { get; }
        public string Hold { get; }
        public string MutexName { get; } = "Local\\FluxKnowledge.ProjectionBridgeTest." + Guid.NewGuid().ToString("N");
        private Packet(string directory, string index, Guid database)
        {
            _directory = directory; _index = index;
            Configuration = Path.Combine(directory, "configuration.json"); Hold = Path.Combine(directory, "hold.json");
            _plan = Path.Combine(directory, "projection-plan.json");
            _bindings = JsonSerializer.Serialize(new { PlanPath = _plan, PlanSha256 = Hash(_plan), ConfigurationSha256 = Hash(Configuration),
                HoldSha256 = Hash(Hold), DatabaseIncarnation = database, OperatorEntrySha256 = Hash(typeof(IndexProjectionCommand).Assembly.Location) });
        }
        public static async Task<Packet> CreateAsync(SqlToUsearchRebuildTests.PipelineEnvironment environment)
        {
            var directory = Path.Combine(Path.GetTempPath(), "FluxProjectionBridge_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            await using var db = await environment.Factory.CreateDbContextAsync();
            await File.WriteAllTextAsync(Path.Combine(directory, "configuration.json"), JsonSerializer.Serialize(new
                { ConnectionStrings = new { FluxKnowledge = db.Database.GetConnectionString() } }));
            await File.WriteAllTextAsync(Path.Combine(directory, "hold.json"), "\"isolated-index-update\"");
            using var output = new StringWriter(); using var error = new StringWriter();
            Assert.Equal(0, await IndexProjectionCommand.ExecuteAsync(["plan", "--operation-id", Guid.NewGuid().ToString("D"), "--target-format", "2"],
                environment.Factory, new UsearchIndexOptions(environment.IndexRoot), output, error));
            await File.WriteAllTextAsync(Path.Combine(directory, "projection-plan.json"), output.ToString());
            await db.Database.OpenConnectionAsync();
            await using var command = db.Database.GetDbConnection().CreateCommand();
            command.CommandText = "SELECT database_guid FROM sys.database_recovery_status WHERE database_id=DB_ID();";
            var database = Assert.IsType<Guid>(await command.ExecuteScalarAsync());
            return new(directory, environment.IndexRoot, database);
        }
        public IDisposable Begin(Action assertQuiescent) => (IDisposable)Invoke("BeginForIsolatedTests",
            [_index, Configuration, Hold, _bindings, assertQuiescent, MutexName]);
        public string Run(object capability) => (string)Invoke("Run", [capability, 30]);
        public string WriteBindings()
        {
            var path = Path.Combine(_directory, "bindings.json"); File.WriteAllText(path, _bindings); return path;
        }
        private static object Invoke(string method, object[] args)
        {
            var bridge = typeof(IndexProjectionCommand).Assembly.GetType("FluxKnowledge.Cli.Commands.IncrementalProjectionBridge", throwOnError: true)!;
            try { return bridge.GetMethod(method, BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, args)!; }
            catch (TargetInvocationException exception) when (exception.InnerException is { } cause)
            { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(cause).Throw(); throw; }
        }
        private static string Hash(string path) { using var file = File.OpenRead(path); return Convert.ToHexStringLower(SHA256.HashData(file)); }
        public void Dispose()
        {
            var full = Path.GetFullPath(_directory);
            if (!full.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(full).StartsWith("FluxProjectionBridge_", StringComparison.Ordinal)) throw new InvalidOperationException("Unsafe fixture cleanup.");
            Directory.Delete(full, recursive: true);
        }
    }
}
