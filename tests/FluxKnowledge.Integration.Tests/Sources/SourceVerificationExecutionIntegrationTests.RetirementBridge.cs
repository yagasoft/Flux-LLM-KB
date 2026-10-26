using System.Security.Cryptography;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluxKnowledge.Cli.Commands;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Integrations.Files;
using FluxKnowledge.Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Sources;

public sealed partial class SourceVerificationExecutionIntegrationTests
{
    [NativeSqlServerFact]
    public void Retirement_test_entry_refuses_the_exact_production_root_before_reading_bindings()
    {
        foreach (var runtime in new[] { @"I:\FluxKnowledge", @"I:\FluxKnowledge\", @"I:\FluxKnowledge\Runtime" })
            Assert.Equal("source-journal-retirement-test-root-is-production", Assert.Throws<InvalidOperationException>(() =>
                IncrementalSourceJournalRetirementBridge.BeginForIsolatedTests(runtime, @"I:\unused-test-config.json", @"I:\unused-test-hold.json", "{}", () => { },
                    @"Local\FluxKnowledge.SourceJournalRetirementTest.no-live-access")).Message);
    }

    [NativeSqlServerTheory]
    [InlineData("runtime")]
    [InlineData("live-pid")]
    [InlineData("stale")]
    [InlineData("not-stopped")]
    public async Task Retirement_bridge_refuses_mismatched_or_stale_stopped_evidence_before_mutation(string fault)
    {
        await using var scenario = await Scenario.CreateAsync(fixture.ConnectionString);
        await scenario.BaselineAsync();
        await scenario.Verification.StartAsync(scenario.RootId, true, default);
        using var packet = await RetirementPacket.CreateAsync(scenario, fixture.ConnectionString);
        var before = await RetirementSnapshotAsync(scenario);
        Assert.Throws<InvalidOperationException>(() => packet.Begin(() => { }, evidenceFault: fault));
        Assert.Equal(before, await RetirementSnapshotAsync(scenario));
    }

    [NativeSqlServerFact]
    public async Task Retirement_bridge_runs_through_the_real_powershell_operator_loader_without_application_composition()
    {
        await using var scenario = await Scenario.CreateAsync(fixture.ConnectionString);
        await scenario.BaselineAsync();
        await scenario.Verification.StartAsync(scenario.RootId, true, default);
        using var packet = await RetirementPacket.CreateAsync(scenario, fixture.ConnectionString);
        var driver = Path.Combine(scenario.Runtime, "retirement-driver.ps1");
        var bindings = Path.Combine(scenario.Runtime, "retirement-bindings.json");
        await File.WriteAllTextAsync(bindings, packet.PrepareBindings());
        await File.WriteAllTextAsync(driver, """
            param([string]$SourceRoot,[string]$OperatorRoot,[string]$Runtime,[string]$Configuration,[string]$Hold,[string]$Bindings,[string]$MutexName)
            $ErrorActionPreference='Stop'
            Import-Module (Join-Path $SourceRoot 'scripts/deploy/incremental-projection-operator.psm1') -Force
            $calls=[ref]0
            $guard=[Action]({$calls.Value++;if(!(Test-Path -LiteralPath $Hold -PathType Leaf)){throw 'lost-test-hold'}}.GetNewClosure())
            $json=Get-Content -LiteralPath $Bindings -Raw
            $result=& (Get-Module incremental-projection-operator) {
                param($OperatorRoot,$Runtime,$Configuration,$Hold,$Json,$Guard,$MutexName)
                Invoke-ProjectionOperatorBridge -OperatorRoot $OperatorRoot -Operation SourceJournalRetirement -BeginMethod BeginForIsolatedTests `
                    -BeginArguments @($Runtime,$Configuration,$Hold,$Json,$Guard,$MutexName) -TimeoutSeconds 30
            } $OperatorRoot $Runtime $Configuration $Hold $json $guard $MutexName
            if($calls.Value -ne 3){throw 'Stopped callback did not remain on the updater runspace.'}
            $result|ConvertTo-Json -Compress
            """);
        var start = new ProcessStartInfo("pwsh") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-File", driver, "-SourceRoot", RetirementSourceRoot(), "-OperatorRoot",
            Path.GetDirectoryName(typeof(IndexProjectionCommand).Assembly.Location)!, "-Runtime", scenario.Runtime, "-Configuration", packet.Configuration,
            "-Hold", packet.Hold, "-Bindings", bindings, "-MutexName", packet.MutexName }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try { await process.WaitForExitAsync(deadline.Token); }
        catch { if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); } throw; }
        Assert.True(process.ExitCode == 0, await stderr);
        using var result = JsonDocument.Parse(await stdout);
        Assert.Equal(1, result.RootElement.GetProperty("Runs").GetInt32());
        Assert.Equal(1, result.RootElement.GetProperty("Requests").GetInt32());
    }

    private static string RetirementSourceRoot([CallerFilePath] string path = "") => Directory.GetParent(Path.GetDirectoryName(path)!)!.Parent!.Parent!.FullName;

    [NativeSqlServerFact]
    public async Task Retirement_bridge_binds_stopped_evidence_runtime_hold_configuration_database_and_single_use()
    {
        await using var scenario = await Scenario.CreateAsync(fixture.ConnectionString);
        await scenario.BaselineAsync();
        await scenario.Verification.StartAsync(scenario.RootId, true, default);
        using var packet = await RetirementPacket.CreateAsync(scenario, fixture.ConnectionString);
        var before = await RetirementSnapshotAsync(scenario);
        var stopped = true;
        using (var capability = packet.Begin(() => { if (!stopped) throw new InvalidOperationException("fixture-writer-started"); }))
        {
            Assert.Throws<IOException>(() => File.AppendAllText(packet.Hold, " "));
            Assert.Throws<IOException>(() => File.AppendAllText(packet.Evidence, " "));
            stopped = false;
            Assert.Equal("fixture-writer-started", Assert.Throws<InvalidOperationException>(() => IncrementalSourceJournalRetirementBridge.Run(capability, 30)).Message);
        }
        Assert.Equal(before, await RetirementSnapshotAsync(scenario));
        File.AppendAllText(packet.Hold, " ");
        Assert.Throws<InvalidOperationException>(() => packet.Begin(() => { }));
        File.WriteAllText(packet.Hold, JsonSerializer.Serialize(packet.Release));
        using (var wrong = packet.Begin(() => { }, wrongDatabase: true))
            Assert.Throws<InvalidOperationException>(() => IncrementalSourceJournalRetirementBridge.Run(wrong, 30));
        Assert.Equal(before, await RetirementSnapshotAsync(scenario));
        using (var capability = packet.Begin(() => { }))
        {
            using var result = JsonDocument.Parse(IncrementalSourceJournalRetirementBridge.Run(capability, 30));
            Assert.Equal(1, result.RootElement.GetProperty("Runs").GetInt32());
            Assert.Throws<InvalidOperationException>(() => IncrementalSourceJournalRetirementBridge.Run(capability, 30));
        }
        using (var capability = packet.Begin(() => { }))
        {
            using var result = JsonDocument.Parse(IncrementalSourceJournalRetirementBridge.Run(capability, 30));
            Assert.Equal(0, result.RootElement.GetProperty("Requests").GetInt32());
        }
        Assert.Throws<InvalidOperationException>(() => IncrementalSourceJournalRetirementBridge.BeginForIsolatedTests(
            @"I:\FluxKnowledge\Runtime", packet.Configuration, packet.Hold, "{}", () => { }, packet.MutexName));
    }

    private sealed class RetirementPacket(string runtime, string configuration, string hold, string evidence, string runtimeIdentity, Guid database) : IDisposable
    {
        private readonly string _configurationHash = Hash(configuration);
        private readonly string _holdHash = Hash(hold);
        internal string Configuration => configuration;
        internal string Hold => hold;
        internal string Evidence => evidence;
        internal string Release { get; } = "isolated-source-retirement";
        internal string MutexName { get; } = @"Local\FluxKnowledge.SourceJournalRetirementTest." + Guid.NewGuid().ToString("N");
        internal static async Task<RetirementPacket> CreateAsync(Scenario scenario, string connection)
        {
            var configuration = Path.Combine(scenario.Runtime, "retirement-settings.json");
            var hold = Path.Combine(scenario.Runtime, "deployment-validation-hold.json");
            var evidence = Path.Combine(scenario.Runtime, "stopped-writers.json");
            await File.WriteAllTextAsync(configuration, JsonSerializer.Serialize(new { ConnectionStrings = new { FluxKnowledge = connection },
                SourceJournal = new { Enabled = false, SealingEnabled = false } }));
            await File.WriteAllTextAsync(hold, JsonSerializer.Serialize("isolated-source-retirement"));
            await using var db = scenario.Factory.CreateDbContext();
            await db.Database.OpenConnectionAsync();
            await using var command = db.Database.GetDbConnection().CreateCommand();
            command.CommandText = "SELECT database_guid FROM sys.database_recovery_status WHERE database_id=DB_ID();";
            var database = (Guid)(await command.ExecuteScalarAsync())!;
            var identity = PhysicalFileIdentity.GetDirectory(scenario.Runtime).IdentityFingerprint;
            return new(scenario.Runtime, configuration, hold, evidence, identity, database);
        }
        internal IDisposable Begin(Action assertStopped, bool wrongDatabase = false, string? evidenceFault = null) =>
            (IDisposable)IncrementalSourceJournalRetirementBridge.BeginForIsolatedTests(runtime, configuration, hold, PrepareBindings(wrongDatabase, evidenceFault), assertStopped, MutexName);
        internal string PrepareBindings(bool wrongDatabase = false, string? evidenceFault = null)
        {
            var incarnation = wrongDatabase ? Guid.NewGuid() : database;
            var proof = JsonSerializer.SerializeToNode(new { Version = 1, ReleaseId = Release, DatabaseIncarnation = incarnation,
                RuntimeRoot = runtime, RuntimeIdentityFingerprint = runtimeIdentity, ConfigurationSha256 = _configurationHash,
                WritersStopped = true, StoppedProcessIds = Array.Empty<int>(), ObservedAtUtc = DateTimeOffset.UtcNow })!.AsObject();
            switch (evidenceFault)
            {
                case "runtime": proof["RuntimeIdentityFingerprint"] = new string('0', 64); break;
                case "live-pid": proof["StoppedProcessIds"] = new JsonArray(Environment.ProcessId); break;
                case "stale": proof["ObservedAtUtc"] = DateTimeOffset.UtcNow.AddHours(-1); break;
                case "not-stopped": proof["WritersStopped"] = false; break;
            }
            File.WriteAllText(evidence, proof.ToJsonString());
            var bindings = JsonSerializer.Serialize(new { ReleaseId = Release, ConfigurationSha256 = _configurationHash, HoldSha256 = _holdHash,
                RuntimeIdentityFingerprint = runtimeIdentity, DatabaseIncarnation = incarnation,
                OperatorEntrySha256 = Hash(typeof(IndexProjectionCommand).Assembly.Location),
                StoppedWritersEvidencePath = evidence, StoppedWritersEvidenceSha256 = Hash(evidence) });
            return bindings;
        }
        private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
        public void Dispose()
        {
            File.Delete(evidence); File.Delete(hold); File.Delete(configuration);
        }
    }
}
