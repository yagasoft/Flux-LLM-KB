using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Diagnostics;
using FluxKnowledge.Application.IntegrationV1;
using FluxKnowledge.Application.IntegrationV1.Corpus;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Sources;
using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.SqlServer.Visibility;
using FluxKnowledge.Integrations.Files;
using FluxKnowledge.Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Sources;

public sealed partial class SourceJournalSealIntegrationTests(NativeSqlServerFixture fixture) : IClassFixture<NativeSqlServerFixture>, IAsyncLifetime
{
    public async Task InitializeAsync()
    {
        await SqlTestData.ClearPhase3SourceDataAsync(fixture);
        await using var db = new SourceJournalStoreIntegrationTests.ContextFactory(fixture.ConnectionString).CreateDbContext();
        await db.SourceVolumeJournalCheckpoints.ExecuteDeleteAsync();
    }
    public Task DisposeAsync() => Task.CompletedTask;

    [NativeSqlServerFact]
    public async Task Self_generated_close_is_consumed_without_leaving_new_work_for_the_capture_it_attests()
    {
        await using var scenario = await Scenario.CreateAsync(fixture.ConnectionString, qualified: true);
        await scenario.ScanAsync("self-close-baseline");
        await using var db = scenario.Factory.CreateDbContext();
        var entry = await db.SourceInventoryEntries.SingleAsync(x => x.RelativePath == "one.txt");
        Assert.True(entry.IsSealed);
        Assert.True((await db.SourceVolumeJournalCheckpoints.SingleAsync()).NextUsn > entry.LatestJournalUsn);
        var pending = await db.SourcePendingChanges.Where(x => x.RelativePath == "one.txt").ToArrayAsync();
        Assert.True(pending.Length == 0, JsonSerializer.Serialize(new { entry.LatestJournalUsn, entry.ObservationJson,
            Pending = pending.Select(x => new { x.RelativePath, x.Generation, x.FirstUsn, x.LastUsn, x.LastContentUsn, x.LastUnjournaledGeneration, x.IsSubtree, Reason = x.ReasonFlags.ToString("X") }) }));
    }

    [NativeSqlServerFact]
    public async Task Failure_after_saving_attestation_rolls_back_receipt_and_keeps_the_consumed_journal_work_durable()
    {
        await using var scenario = await Scenario.CreateAsync(fixture.ConnectionString, qualified: true);
        var claim = await scenario.ClaimAsync("attestation-crash");
        scenario.BeforeAttestationCommit = () => throw new InvalidOperationException("Injected failure after SaveChanges before commit.");
        await Assert.ThrowsAsync<InvalidOperationException>(() => scenario.RunAsync(claim));
        await using (var db = scenario.Factory.CreateDbContext())
        {
            Assert.False(await db.SourceInventoryEntries.AnyAsync(x => !x.IsDirectory));
            var pending = await db.SourcePendingChanges.SingleAsync(x => x.RelativePath == "one.txt");
            Assert.Equal(0x80000000L, pending.ReasonFlags);
            Assert.True((await db.SourceVolumeJournalCheckpoints.SingleAsync()).NextUsn > pending.LastUsn);
            Assert.True(await db.SourceArtifacts.AnyAsync());
        }
        scenario.BeforeAttestationCommit = null;
        await scenario.RunAsync(claim);
        await scenario.DrainAsync();
        await using var verify = scenario.Factory.CreateDbContext();
        Assert.True((await verify.SourceInventoryEntries.SingleAsync(x => x.RelativePath == "one.txt")).IsSealed);
        Assert.False(await verify.SourcePendingChanges.AnyAsync(x => x.RelativePath == "one.txt"));
    }

    [NativeSqlServerTheory]
    [InlineData("null-content")]
    [InlineData("null-unjournalled")]
    [InlineData("content")]
    [InlineData("watcher")]
    [InlineData("metadata")]
    [InlineData("older")]
    [InlineData("later")]
    [InlineData("subtree")]
    public async Task Self_close_acknowledgement_preserves_every_unmatched_or_uncertain_obligation(string kind)
    {
        await using var scenario = await Scenario.CreateAsync(fixture.ConnectionString, qualified: true);
        string? expected = null;
        scenario.BeforeAttestation = () =>
        {
            using var db = scenario.Factory.CreateDbContext();
            var row = db.SourcePendingChanges.Single(x => x.RelativePath == "one.txt");
            var state = db.SourceRootScanStates.Single();
            row.Generation = ++state.Generation;
            switch (kind)
            {
                case "null-content": row.LastContentUsn = null; break;
                case "null-unjournalled": row.LastUnjournaledGeneration = null; break;
                case "content": row.ReasonFlags |= 1; row.LastContentUsn = row.LastUsn; break;
                case "watcher": row.ReasonFlags |= 1; row.LastUnjournaledGeneration = row.Generation; break;
                case "metadata": row.ReasonFlags |= 0x8000; break;
                case "older": row.FirstUsn--; break;
                case "later": row.LastUsn++; break;
                case "subtree": row.IsSubtree = true; break;
            }
            db.SaveChanges(); expected = JsonSerializer.Serialize(row);
        };
        await scenario.ScanAsync("unmatched-self-close");
        await using var inspect = scenario.Factory.CreateDbContext();
        Assert.Equal(expected, JsonSerializer.Serialize(await inspect.SourcePendingChanges.SingleAsync(x => x.RelativePath == "one.txt")));
    }

    [NativeSqlServerFact]
    public async Task Unqualified_capture_cannot_grant_journal_only_future_change_authority()
    {
        await using var scenario = await Scenario.CreateAsync(fixture.ConnectionString);
        await scenario.ScanAsync("baseline");
        await using var db = scenario.Factory.CreateDbContext();
        var proof = JsonDocument.Parse((await db.SourceInventoryEntries.SingleAsync(x => x.RelativePath == "one.txt")).ObservationJson!);
        Assert.False(proof.RootElement.GetProperty("JournalCovered").GetBoolean());
        Assert.Equal(3, proof.RootElement.GetProperty("Version").GetInt32());
    }

    [NativeSqlServerFact]
    public async Task Unsealed_ordinary_backstop_captures_a_missed_repeated_write_with_preserved_timestamps()
    {
        await using var scenario = await Scenario.CreateAsync(fixture.ConnectionString);
        using var persistent = new FileStream(scenario.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        scenario.Overwrite(0, 65);
        var root = await scenario.ScanAsync("baseline");
        scenario.Overwrite(1, 66); // Same reason and timestamp; a different reader keeps aggregation open.
        scenario.Clock.Now += root.ReconciliationCadence + TimeSpan.FromSeconds(1);
        scenario.SourceBytes = 0;
        await scenario.ScanAsync("ordinary-backstop");
        Assert.Equal(64, scenario.SourceBytes);
        await using var db = scenario.Factory.CreateDbContext();
        var current = await db.SourceRevisions.OrderByDescending(x => x.Revision).FirstAsync(x => x.CanonicalPath == scenario.Path);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(scenario.Path))), current.ContentSha256);
    }

    [NativeSqlServerTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Qualified_seals_detect_three_later_edits_across_restart_without_rereading_a_sealed_sibling(bool watcherFirst)
    {
        await using var scenario = await Scenario.CreateAsync(fixture.ConnectionString, qualified: true);
        await File.WriteAllTextAsync(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(scenario.Path)!, "sibling.txt"), new string('s', 64));
        using var persistent = new FileStream(scenario.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        scenario.Overwrite(0, 65);
        await scenario.ScanAsync("baseline");
        await using (var db = scenario.Factory.CreateDbContext())
        {
            var receipt = JsonDocument.Parse((await db.SourceInventoryEntries.SingleAsync(x => x.RelativePath == "one.txt")).ObservationJson!);
            Assert.Equal(3, receipt.RootElement.GetProperty("Version").GetInt32());
            Assert.True(receipt.RootElement.GetProperty("JournalCovered").GetBoolean());
            Assert.True(receipt.RootElement.GetProperty("SealUsn").GetInt64() > 0);
            Assert.False(string.IsNullOrEmpty(receipt.RootElement.GetProperty("SealProfileId").GetString()));
        }
        scenario.SourceBytes = scenario.ArtifactSourceBytes = 0;
        scenario.ReadTrace.Clear();
        await scenario.DrainAsync();
        Assert.True(scenario.SourceBytes + scenario.ArtifactSourceBytes == 0, string.Join(Environment.NewLine, scenario.ReadTrace));
        for (var cycle = 0; cycle < 3; cycle++)
        {
            scenario.Overwrite(cycle + 1, (byte)(66 + cycle));
            if (!watcherFirst) await scenario.IntakeAsync();
            await scenario.SignalAsync();
            scenario.Restart(); // Duplicate hints cannot replace persisted journal work across restart.
            scenario.SourceBytes = scenario.ArtifactSourceBytes = 0;
            scenario.ReadTrace.Clear();
            await scenario.DrainAsync();
            Assert.True(scenario.SourceBytes == 64, $"Cycle{cycle}: source bytes{scenario.SourceBytes}, artifact bytes{scenario.ArtifactSourceBytes}" + Environment.NewLine + string.Join(Environment.NewLine, scenario.ReadTrace));
            await using var db = scenario.Factory.CreateDbContext();
            var current = await db.SourceRevisions.OrderByDescending(x => x.Revision).FirstAsync(x => x.CanonicalPath == scenario.Path);
            Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(scenario.Path))), current.ContentSha256);
        }
        scenario.SourceBytes = scenario.ArtifactSourceBytes = 0;
        await scenario.DrainAsync();
        Assert.Equal(0, scenario.SourceBytes + scenario.ArtifactSourceBytes);
    }

    [NativeSqlServerTheory]
    [InlineData("environment")]
    [InlineData("unavailable-provider")]
    [InlineData("partial-provider")]
    public async Task Revoked_qualification_fences_existing_receipts_and_advisory_hints_but_disabling_only_new_writes_does_not(string change)
    {
        await using var scenario = await Scenario.CreateAsync(fixture.ConnectionString, qualified: true);
        await scenario.ScanAsync("baseline");
        await scenario.DrainAsync();
        scenario.Options.SealingEnabled = false;
        scenario.SourceBytes = scenario.ArtifactSourceBytes = 0;
        await scenario.DrainAsync();
        Assert.Equal(0, scenario.SourceBytes + scenario.ArtifactSourceBytes);

        var original = scenario.Options.SealQualifications[0];
        scenario.Options.SealQualifications = [change switch
        {
            "environment" => original with { EnvironmentFingerprint = "unqualified" },
            "unavailable-provider" => original with { ProviderName = "OneDrive - Business", ProviderVersion = "changed-version" },
            _ => original with { ProviderVersion = "changed-version" }
        }];
        await scenario.SignalAsync();
        await using (var pending = scenario.Factory.CreateDbContext())
            Assert.True(await pending.SourcePendingChanges.AnyAsync(x => x.RelativePath == "one.txt" && x.LastUnjournaledGeneration > 0));
        scenario.Restart();
        await scenario.DrainAsync();
        Assert.Equal(64, scenario.SourceBytes);
        await using var db = scenario.Factory.CreateDbContext();
        var receipt = JsonDocument.Parse((await db.SourceInventoryEntries.SingleAsync(x => x.RelativePath == "one.txt")).ObservationJson!);
        Assert.False(receipt.RootElement.GetProperty("JournalCovered").GetBoolean());
    }

    [NativeSqlServerFact]
    public async Task Qualified_multiply_linked_capture_completes_with_unsealed_debt_instead_of_retrying_forever()
    {
        await using var scenario = await Scenario.CreateAsync(fixture.ConnectionString, qualified: true);
        Assert.True(CreateHardLink(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(scenario.Path)!, "alias.txt"), scenario.Path, IntPtr.Zero));
        await scenario.ScanAsync("hard-link-baseline");
        await using var db = scenario.Factory.CreateDbContext();
        var entries = await db.SourceInventoryEntries.Where(x => !x.IsDirectory).ToArrayAsync();
        Assert.Equal(2, entries.Length);
        Assert.All(entries, entry =>
        {
            Assert.False(entry.IsSealed);
            Assert.Equal("multiple-links", entry.SealFailureCode);
            Assert.NotNull(entry.NextForcedCheckAtUtc);
        });
        Assert.False((await db.SourceRootScanStates.SingleAsync()).RecoveryRequired);
    }

    [NativeSqlServerFact]
    public async Task Refused_seal_preserves_retention_and_an_explicit_ordinary_retry_deadline()
    {
        await using var scenario = await Scenario.CreateAsync(fixture.ConnectionString, qualified: true);
        scenario.SealBehaviour = SealBehaviour.Refuse;
        await scenario.ScanAsync("refused-seal");
        await using var db = scenario.Factory.CreateDbContext();
        var entry = await db.SourceInventoryEntries.SingleAsync(x => x.RelativePath == "one.txt");
        Assert.False(entry.IsSealed);
        Assert.Equal("seal-unavailable", entry.SealFailureCode);
        Assert.NotNull(entry.NextForcedCheckAtUtc);
        Assert.NotNull(entry.SourceRevisionId);
        Assert.True(await db.SourceArtifacts.AnyAsync(x => x.SourceRevisionId == entry.SourceRevisionId));
    }

    [NativeSqlServerFact]
    public async Task Cancellation_after_native_append_cannot_commit_a_receipt_and_the_frozen_claim_can_retry()
    {
        await using var scenario = await Scenario.CreateAsync(fixture.ConnectionString, qualified: true);
        scenario.SealBehaviour = SealBehaviour.CancelAfterAppend;
        var claim = await scenario.ClaimAsync("cancel-after-seal");
        await Assert.ThrowsAsync<OperationCanceledException>(() => scenario.RunAsync(claim));
        await scenario.IntakeAsync();
        await using (var db = scenario.Factory.CreateDbContext())
        {
            Assert.True(await db.SourceArtifacts.AnyAsync());
            Assert.False(await db.SourceInventoryEntries.AnyAsync(x => !x.IsDirectory));
            Assert.True((await db.SourceRootScanStates.SingleAsync()).RecoveryRequired);
            Assert.True(await db.SourcePendingChanges.AnyAsync(x => x.RelativePath == "one.txt"));
        }
        scenario.SealBehaviour = SealBehaviour.Normal;
        await scenario.RunAsync(claim);
        await using var verify = scenario.Factory.CreateDbContext();
        Assert.True((await verify.SourceInventoryEntries.SingleAsync(x => x.RelativePath == "one.txt")).IsSealed);
        Assert.False((await verify.SourceRootScanStates.SingleAsync()).RecoveryRequired);
    }

    [NativeSqlServerFact]
    public async Task An_unjournaled_hint_arriving_during_capture_survives_the_pre_capture_generation_cutoff()
    {
        await using var scenario = await Scenario.CreateAsync(fixture.ConnectionString, qualified: true);
        await scenario.ScanAsync("baseline");
        await scenario.DrainAsync();
        scenario.Overwrite(1, 67);
        await scenario.IntakeAsync(); await scenario.IntakeAsync();
        scenario.AfterRead = () => scenario.SignalAsync().GetAwaiter().GetResult();
        scenario.SourceBytes = 0;
        await scenario.RunAsync(await scenario.ClaimAsync("capture-with-later-hint"));
        Assert.Equal(64, scenario.SourceBytes);
        await using (var db = scenario.Factory.CreateDbContext())
        {
            var proof = JsonDocument.Parse((await db.SourceInventoryEntries.SingleAsync(x => x.RelativePath == "one.txt")).ObservationJson!);
            var pending = await db.SourcePendingChanges.SingleAsync(x => x.RelativePath == "one.txt");
            Assert.True(pending.LastUnjournaledGeneration > proof.RootElement.GetProperty("CaptureGeneration").GetInt64());
        }
        scenario.SourceBytes = 0;
        await scenario.DrainAsync();
        Assert.Equal(64, scenario.SourceBytes); // The later unknown obligation requires a fresh guarded capture.
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(string path, string existing, IntPtr security);

    private enum SealBehaviour { Normal, Refuse, CancelAfterAppend }

    private sealed class Scenario : IAsyncDisposable
    {
        private readonly string _area = System.IO.Path.Combine(@"I:\Codex Workspaces\Staging\FluxKnowledge", "JournalSeal-" + Guid.NewGuid().ToString("N"));
        internal string Path => System.IO.Path.Combine(_area, "Sources", "one.txt");
        internal SourceJournalStoreIntegrationTests.ContextFactory Factory { get; }
        internal Clock Clock { get; } = new();
        internal SourceJournalOptions Options { get; } = new() { Enabled = true };
        internal long SourceBytes;
        internal long ArtifactSourceBytes;
        internal long RetainedBytes;
        internal SealBehaviour SealBehaviour;
        internal Action? AfterRead;
        internal List<string> ReadTrace { get; } = [];
        internal List<string> CapturedPaths { get; } = [];
        internal bool FailNextCompletion;
        internal Func<SourceIncrementalPlan, Task>? BeforeReuse;
        internal Action? BeforeAttestation;
        internal Action? BeforeAttestationCommit;
        internal LocalSourceEnumerator Enumeration = null!;
        internal SourceEnumerationEvidence[] CompletionEvidence = [];
        private Guid _rootId;
        private ContentAddressedSourceArtifactStore _artifacts = null!;
        private SqlSourceScanStore _control = null!;
        private SourceScanWorker _worker = null!;
        private SqlSourceIncrementalCoordinator _coordinator = null!;
        private DateTime _stamp;
        private Scenario(string connection) => Factory = new(connection);

        internal static async Task<Scenario> CreateAsync(string connection, bool qualified = false, bool gitTracked = false)
        {
            var scenario = new Scenario(connection);
            try
            {
                var files = System.IO.Path.GetDirectoryName(scenario.Path)!;
                Directory.CreateDirectory(files);
                await File.WriteAllTextAsync(scenario.Path, new string('x', 64));
                if (gitTracked) { scenario.Git("init", "--quiet"); scenario.Git("add", "."); }
                scenario._stamp = File.GetLastWriteTimeUtc(scenario.Path);
                var commands = new NativeCorpusCommandService(new NativeOperationService(new SqlNativeOperationStore(scenario.Factory, TimeProvider.System), []),
                    new SqlNativeCorpusActionStore(scenario.Factory, new SourceRootPathPolicy(new LocalIngressOptions([files])), new LocalPrivateContentDisclosure()));
                var mutation = new NativeCorpusMutation("root_create", JsonSerializer.SerializeToElement(new { path = files, displayName = "Seal fixture", recursive = true, maximumFileBytes = 1024,
                    discoveryMode = gitTracked ? "git-tracked" : "filesystem" }));
                var preview = await commands.PreviewAsync(mutation, "test", default);
                await commands.CommitAsync(mutation, preview.ConfirmationId, "seal-" + Guid.NewGuid().ToString("N"), "test", default);
                await using (var db = scenario.Factory.CreateDbContext()) scenario._rootId = (await db.SourceRootConfigurations.SingleAsync()).Id;
                scenario.Clock.Now = DateTimeOffset.UtcNow;
                scenario.Options.ApprovedVolumes = [WindowsSourceChangeJournalReader.CanonicalVolume(files)];
                if (qualified)
                {
                    await using var db = scenario.Factory.CreateDbContext();
                    var identity = SqlSourceScanStore.ParseAdmissionIdentityFingerprint((await db.SourceRootConfigurations.SingleAsync()).HealthEvidenceJson);
                    Assert.False(string.IsNullOrEmpty(identity));
                    var environment = WindowsSourceJournalSealQualification.GetEnvironmentFingerprint();
                    Assert.False(string.IsNullOrEmpty(environment));
                    scenario.Options.SealingEnabled = true;
                    scenario.Options.SealQualifications = [new(identity!, environment!, "local-ntfs-synthetic", "native-seal-qualification-fixture")];
                }
                scenario._artifacts = new(System.IO.Path.Combine(scenario._area, "Retained"))
                { SourceBytesRead = n => scenario.ArtifactSourceBytes += n, RetainedBytesRead = n => scenario.RetainedBytes += n };
                scenario._control = new(scenario.Factory, scenario.Clock);
                scenario.Restart();
                return scenario;
            }
            catch { await scenario.DisposeAsync(); throw; }
        }

        internal void Restart()
        {
            _coordinator = new(Factory, Clock, new WindowsSourceChangeJournalReader(), new WindowsSourceObservationReader(), _artifacts, Options)
            {
                ReuseRejected = (path, reason) => ReadTrace.Add(JsonSerializer.Serialize(new { ReusePath = path, reason })),
                BeforeCaptureAttestation = () => BeforeAttestation?.Invoke(), BeforeCaptureAttestationCommit = () => BeforeAttestationCommit?.Invoke()
            };
            Enumeration = new() { SourceBytesRead = n =>
                { SourceBytes += n; TraceRead(); var hook = AfterRead; AfterRead = null; hook?.Invoke(); } };
            _worker = new(Enumeration, _control, _artifacts,
                new SqlSourceActivityStore(Factory, Clock), incrementalCoordinator: new CaptureFaultCoordinator(_coordinator, this));
        }

        private void TraceRead()
        {
            using var db = Factory.CreateDbContext();
            var state = db.SourceRootScanStates.AsNoTracking().Single();
            var pending = db.SourcePendingChanges.AsNoTracking().Select(x => new { x.RelativePath, x.Generation, x.ReasonFlags, x.LastContentUsn, x.LastUnjournaledGeneration }).ToArray();
            var entries = db.SourceInventoryEntries.AsNoTracking().Where(x => !x.IsDirectory).Select(x => new { x.RelativePath, x.IsSealed,
                x.LatestJournalUsn, x.LatestJournalReason, x.ObservationJson }).ToArray();
            ReadTrace.Add(JsonSerializer.Serialize(new { state.Mode, state.Generation, state.RecoveryGeneration, pending, entries }));
        }

        internal async Task IntakeAsync()
        { Clock.Now += TimeSpan.FromSeconds(3); await _coordinator.PumpAsync(default); }

        internal async Task SignalAsync(bool subtree = false, bool membership = false)
        {
            var watcher = new SqlSourceRootWatchStore(Factory, Clock, Options);
            for (var duplicate = 0; duplicate < 3; duplicate++)
                await watcher.RecordSignalAsync(new(new(_rootId), SourceWatchSignalKind.Changed, Clock.Now, [new("one.txt", subtree)],
                    RepositoryControlChanged: membership), default);
            Clock.Now += TimeSpan.FromSeconds(3);
            var batch = Assert.IsType<ClaimedSourceWatchBatch>(await watcher.ClaimDueBatchAsync(Clock.Now, "seal-watch", TimeSpan.FromMinutes(1), default));
            await watcher.ReleaseScanAsync(batch, default);
        }

        internal async Task SignalUnknownNamespaceAsync()
        {
            var watcher = new SqlSourceRootWatchStore(Factory, Clock, Options);
            await watcher.RecordSignalAsync(new(new(_rootId), SourceWatchSignalKind.Changed, Clock.Now, [], true), default);
            Clock.Now += TimeSpan.FromSeconds(3);
            var batch = Assert.IsType<ClaimedSourceWatchBatch>(await watcher.ClaimDueBatchAsync(Clock.Now, "namespace-watch", TimeSpan.FromMinutes(1), default));
            await watcher.ReleaseScanAsync(batch, default);
        }

        internal async Task DrainAsync()
        {
            for (var turn = 0; turn < 5; turn++)
            {
                Clock.Now += TimeSpan.FromSeconds(3);
                await _coordinator.PumpAsync(default);
                var claim = await _control.ClaimNextReleasedAsync("seal-catchup", Clock.Now, TimeSpan.FromMinutes(10), default);
                if (claim is not null)
                    await _control.CompleteAsync(claim, await _worker.ScanAsync(claim.SourceRoot, claim.ScanRequest, default), null, default);
            }
        }

        internal void Overwrite(int offset, byte value)
        {
            using (var writer = new FileStream(Path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
            { writer.Position = offset; writer.WriteByte(value); }
            File.SetLastWriteTimeUtc(Path, _stamp);
        }
        internal async Task<SourceRootConfiguration> ScanAsync(string owner)
        {
            var claim = await ClaimAsync(owner);
            await RunAsync(claim);
            return claim.SourceRoot;
        }
        internal void Git(params string[] arguments)
        {
            var start = new ProcessStartInfo(System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Git", "cmd", "git.exe"))
            { WorkingDirectory = System.IO.Path.GetDirectoryName(Path)!, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var key in start.Environment.Keys.Where(key => key.StartsWith("GIT_", StringComparison.OrdinalIgnoreCase)).ToArray()) start.Environment.Remove(key);
            start.Environment["GIT_CONFIG_GLOBAL"] = "NUL"; start.Environment["GIT_CONFIG_SYSTEM"] = "NUL";
            foreach (var argument in new[] { "-c", "core.hooksPath=NUL" }.Concat(arguments)) start.ArgumentList.Add(argument);
            using var process = Process.Start(start)!;
            var errors = process.StandardError.ReadToEnd(); process.WaitForExit();
            Assert.True(process.ExitCode == 0, errors);
        }
        internal async Task<ClaimedSourceScan> ClaimAsync(string owner) =>
            Assert.IsType<ClaimedSourceScan>(await _control.ClaimNextReleasedAsync(owner, Clock.Now, TimeSpan.FromMinutes(10), default));
        internal ValueTask<SourceIncrementalPlan?> PrepareAsync(ClaimedSourceScan claim) =>
            _coordinator.PrepareAsync(claim.SourceRoot, claim.ScanRequest, default);
        internal async Task RunAsync(ClaimedSourceScan claim) =>
            await _control.CompleteAsync(claim, await _worker.ScanAsync(claim.SourceRoot, claim.ScanRequest, default), null, default);
        public ValueTask DisposeAsync()
        {
            _artifacts?.Dispose();
            var git = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(Path)!, ".git");
            if (Directory.Exists(git)) foreach (var file in Directory.EnumerateFiles(git, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
            if (Directory.Exists(_area)) Directory.Delete(_area, true);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class CaptureFaultCoordinator(ISourceIncrementalCoordinator inner, Scenario scenario) : ISourceIncrementalCoordinator
    {
        public ValueTask<SourceIncrementalPlan?> PrepareAsync(SourceRootConfiguration root, SourceScanRequest request, CancellationToken ct) => inner.PrepareAsync(root, request, ct);
        public async ValueTask<SourceRevisionId?> TryReuseAsync(SourceRootConfiguration root, SourceScanRequest request, SourceIncrementalPlan plan, string path, CancellationToken ct)
        {
            var before = scenario.BeforeReuse; scenario.BeforeReuse = null;
            if (before is not null) await before(plan);
            return await inner.TryReuseAsync(root, request, plan, path, ct);
        }
        public ValueTask RecordCaptureAsync(SourceRootConfiguration root, SourceScanRequest request, SourceIncrementalPlan plan, SourceDiscoveredFile file,
            SourceRevisionId revision, ISourceFileCapture? capture, CancellationToken ct)
        {
            scenario.ReadTrace.Add(JsonSerializer.Serialize(new { CapturePath = file.RelativePath, plan.Generation, plan.Targets }));
            scenario.CapturedPaths.Add(file.RelativePath);
            return inner.RecordCaptureAsync(root, request, plan, file, revision,
                scenario.SealBehaviour == SealBehaviour.Normal || capture is null ? capture : new FaultCapture(capture, scenario.SealBehaviour), ct);
        }
        public ValueTask<bool> CompleteAsync(SourceRootConfiguration root, SourceScanRequest request, SourceIncrementalPlan plan,
            IReadOnlyList<SourceNamespaceObservation> directories, IReadOnlyList<SourceEnumerationEvidence> evidence, IReadOnlySet<SourceRevisionId> seen,
            GitInventoryEvidence? inventory, CancellationToken ct)
        {
            if (scenario.FailNextCompletion)
            {
                scenario.FailNextCompletion = false;
                throw new SourceSnapshotChangedException("Fixture stopped after sealing before scan completion authority.");
            }
            return inner.CompleteAsync(root, request, plan, directories, evidence.Concat(scenario.CompletionEvidence).ToArray(), seen, inventory, ct);
        }
        public ValueTask<int> PumpAsync(CancellationToken ct) => inner.PumpAsync(ct);
    }

    private sealed class FaultCapture(ISourceFileCapture capture, SealBehaviour behaviour) : IJournalSealableSourceCapture
    {
        public SourceDiscoveredFile Snapshot => capture.Snapshot;
        public async ValueTask<SourceCaptureJournalSeal> SealAsync(CancellationToken ct)
        {
            if (behaviour == SealBehaviour.Refuse) throw new IOException("Fixture native seal refusal");
            await Assert.IsAssignableFrom<IJournalSealableSourceCapture>(capture).SealAsync(ct);
            throw new OperationCanceledException("Fixture lost control after native append, before SQL proof");
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask; // The worker still owns the original capture.
    }
    private sealed class Clock : TimeProvider
    {
        internal DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
