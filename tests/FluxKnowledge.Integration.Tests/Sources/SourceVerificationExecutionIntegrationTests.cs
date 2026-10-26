using System.Text.Json;
using System.Runtime.Versioning;
using System.Diagnostics;
using FluxKnowledge.Application.IntegrationV1;
using FluxKnowledge.Application.IntegrationV1.Corpus;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Sources;
using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using FluxKnowledge.Infrastructure.SqlServer.Visibility;
using FluxKnowledge.Integrations.Files;
using FluxKnowledge.Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Sources;

[SupportedOSPlatform("windows")]
public sealed partial class SourceVerificationExecutionIntegrationTests(NativeSqlServerFixture fixture)
    : IClassFixture<NativeSqlServerFixture>, IAsyncLifetime
{
    public async Task InitializeAsync()
    {
        await SqlTestData.ClearPhase3SourceDataAsync(fixture);
        await using var db = new SourceJournalStoreIntegrationTests.ContextFactory(fixture.ConnectionString).CreateDbContext();
        await db.SourceVolumeJournalCheckpoints.ExecuteDeleteAsync();
    }
    public Task DisposeAsync() => Task.CompletedTask;

    [NativeSqlServerFact]
    public async Task Concurrent_starts_freeze_one_finite_cycle_and_enqueue_its_request_atomically()
    {
        await using var scenario = await Scenario.CreateAsync(fixture.ConnectionString);
        await scenario.BaselineAsync();
        await using var before = scenario.Factory.CreateDbContext();
        var ordinary = await before.SourceRootConfigurations.AsNoTracking().SingleAsync();
        var revisionIds = await before.SourceInventoryEntries.Where(x => !x.IsDirectory).Select(x => x.SourceRevisionId).ToArrayAsync();
        var starts = await Task.WhenAll(scenario.Verification.StartAsync(scenario.RootId, true, default).AsTask(),
            scenario.Verification.StartAsync(scenario.RootId, true, default).AsTask());
        Assert.NotEqual(Guid.Empty, starts[0]);
        Assert.Equal(starts[0], starts[1]);
        await using var db = scenario.Factory.CreateDbContext();
        var run = await db.SourceVerificationRuns.SingleAsync();
        Assert.True(run.Active);
        Assert.True(run.Manual);
        var items = await db.SourceVerificationItems.Where(x => x.RunId == run.Id).ToArrayAsync();
        Assert.Equal(2, items.Length);
        Assert.Equal(revisionIds.Order(), items.Select(x => x.ExpectedRevisionId).Order());
        Assert.All(items, item => Assert.NotEqual("{}", item.BindingJson));
        var request = await db.SourceScanRequests.SingleAsync(x => x.RequestKind == SqlSourceVerificationStore.RequestKind);
        Assert.Equal(run.Id, request.VerificationRunId);
        Assert.True(request.IsReleased);
        Assert.True(await db.SourceScanJobs.AnyAsync(x => x.SourceScanRequestId == request.Id));
        Assert.True(await db.SourceScanOutbox.AnyAsync(x => x.SourceScanRequestId == request.Id));
        var after = await db.SourceRootConfigurations.SingleAsync();
        Assert.Equal(ordinary.LastScanStartedAtUtc, after.LastScanStartedAtUtc);
        Assert.Equal(ordinary.LastScanCompletedAtUtc, after.LastScanCompletedAtUtc);
        Assert.Equal(ordinary.LastScanEvidenceJson, after.LastScanEvidenceJson);
        var status = Assert.IsType<SourceVerificationStatus>(await scenario.Verification.ReadAsync(scenario.RootId, default));
        Assert.Equal(TimeZoneInfo.Local.Id, status.Policy.TimeZoneId);
        Assert.Equal(2, status.Run!.Total);
        Assert.Equal(0, status.Run.Verified);
    }

    [NativeSqlServerFact]
    public async Task Missing_baseline_cannot_start_an_empty_successful_cycle()
    {
        await using var scenario = await Scenario.CreateAsync(fixture.ConnectionString);
        await Assert.ThrowsAsync<InvalidOperationException>(() => scenario.Verification.StartAsync(scenario.RootId, true, default).AsTask());
        await using var db = scenario.Factory.CreateDbContext();
        Assert.False(await db.SourceVerificationRuns.AnyAsync());
    }

    [NativeSqlServerFact]
    public async Task Feature_off_does_not_start_verification_even_with_an_existing_inventory()
    {
        await using var scenario = await Scenario.CreateAsync(fixture.ConnectionString);
        await scenario.BaselineAsync();
        scenario.Options.Enabled = false;
        await Assert.ThrowsAsync<InvalidOperationException>(() => scenario.Verification.StartAsync(scenario.RootId, true, default).AsTask());
        await using var db = scenario.Factory.CreateDbContext();
        Assert.False(await db.SourceVerificationRuns.AnyAsync());
    }

    private sealed class Scenario : IAsyncDisposable
    {
        private const string Ground = @"I:\Codex Workspaces\Staging\FluxKnowledge";
        private readonly string _area = Path.Combine(Ground, "WeeklyVerification-" + Guid.NewGuid().ToString("N"));
        internal string Sources => Path.Combine(_area, "Sources");
        internal SourceJournalStoreIntegrationTests.ContextFactory Factory { get; }
        internal SourceJournalOptions Options { get; } = new() { Enabled = true };
        internal SourceRootId RootId { get; private set; } = null!;
        internal SqlSourceScanStore Control { get; private set; } = null!;
        internal ContentAddressedSourceArtifactStore Artifacts { get; private set; } = null!;
        internal SqlSourceIncrementalCoordinator Incremental { get; private set; } = null!;
        internal SqlSourceVerificationStore Verification => new(Factory, TimeProvider.System, Options, Incremental);
        internal string Runtime => Path.Combine(_area, "Runtime");
        private Scenario(string connection) => Factory = new(connection);
        internal static async Task<Scenario> CreateAsync(string connection, bool gitTracked = false)
        {
            var scenario = new Scenario(connection);
            try
            {
                Directory.CreateDirectory(scenario.Sources);
                Directory.CreateDirectory(scenario.Runtime);
                File.WriteAllBytes(Path.Combine(scenario.Runtime, WindowsSourceVerificationReaderSlot.FileName), []);
                await File.WriteAllTextAsync(Path.Combine(scenario.Sources, "one.txt"), new string('a', 64));
                await File.WriteAllTextAsync(Path.Combine(scenario.Sources, "two.txt"), new string('b', 96));
                if (gitTracked) { scenario.Git("init", "--quiet"); scenario.Git("add", "."); }
                var commands = new NativeCorpusCommandService(new NativeOperationService(new SqlNativeOperationStore(scenario.Factory, TimeProvider.System), []),
                    new SqlNativeCorpusActionStore(scenario.Factory, new SourceRootPathPolicy(new LocalIngressOptions([scenario.Sources])), new LocalPrivateContentDisclosure()));
                var mutation = new NativeCorpusMutation("root_create", JsonSerializer.SerializeToElement(new
                { path = scenario.Sources, displayName = "Weekly verification fixture", recursive = true, maximumFileBytes = 1024 * 1024,
                    discoveryMode = gitTracked ? "git-tracked" : "filesystem" }));
                var preview = await commands.PreviewAsync(mutation, "test", default);
                await commands.CommitAsync(mutation, preview.ConfirmationId, "verify-" + Guid.NewGuid().ToString("N"), "test", default);
                await using (var db = scenario.Factory.CreateDbContext())
                    scenario.RootId = new((await db.SourceRootConfigurations.SingleAsync()).Id);
                scenario.Options.ApprovedVolumes = [WindowsSourceChangeJournalReader.CanonicalVolume(scenario.Sources)];
                scenario.Artifacts = new(Path.Combine(scenario._area, "Retained"));
                scenario.Control = new(scenario.Factory, TimeProvider.System, sourceJournalOptions: scenario.Options);
                scenario.Incremental = new(scenario.Factory, TimeProvider.System, new WindowsSourceChangeJournalReader(),
                    new WindowsSourceObservationReader(), scenario.Artifacts, scenario.Options);
                return scenario;
            }
            catch { await scenario.DisposeAsync(); throw; }
        }
        internal void Git(params string[] arguments)
        {
            var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Git", "cmd", "git.exe"))
            { WorkingDirectory = Sources, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var key in start.Environment.Keys.Where(key => key.StartsWith("GIT_", StringComparison.OrdinalIgnoreCase)).ToArray()) start.Environment.Remove(key);
            start.Environment["GIT_CONFIG_GLOBAL"] = "NUL"; start.Environment["GIT_CONFIG_SYSTEM"] = "NUL";
            foreach (var argument in new[] { "-c", "core.hooksPath=NUL" }.Concat(arguments)) start.ArgumentList.Add(argument);
            using var process = Process.Start(start)!;
            var errors = process.StandardError.ReadToEnd(); process.WaitForExit();
            Assert.True(process.ExitCode == 0, errors);
        }
        internal async Task BaselineAsync()
        {
            var claim = Assert.IsType<ClaimedSourceScan>(await Control.ClaimNextReleasedAsync("weekly-baseline", DateTimeOffset.UtcNow, TimeSpan.FromMinutes(10), default));
            var worker = new SourceScanWorker(new LocalSourceEnumerator(), Control, Artifacts,
                new SqlSourceActivityStore(Factory, TimeProvider.System), incrementalCoordinator: Incremental);
            await Control.CompleteAsync(claim, await worker.ScanAsync(claim.SourceRoot, claim.ScanRequest, default), null, default);
            await SettleNamespaceAsync(worker);
        }
        internal async Task SettleNamespaceAsync(SourceScanWorker? existingWorker = null)
        {
            var worker = existingWorker ?? new SourceScanWorker(new LocalSourceEnumerator(), Control, Artifacts,
                new SqlSourceActivityStore(Factory, TimeProvider.System), incrementalCoordinator: Incremental);
            // Other native fixtures can create unrelated volume records during this initial baseline.
            // Make its real metadata backstop due and execute it; never manufacture namespace authority.
            for (var attempt = 0; attempt < 10; attempt++)
            {
                await using var db = Factory.CreateDbContext();
                var state = await db.SourceRootScanStates.AsNoTracking().SingleAsync(x => x.SourceRootId == RootId.Value);
                var debt = await db.SourcePendingChanges.Where(x => x.SourceRootId == RootId.Value &&
                    (x.ReasonFlags & SourcePendingReasons.NamespaceMask) != 0).ToArrayAsync();
                if (state.NamespaceComplete && debt.Length == 0) return;
                var now = DateTimeOffset.UtcNow;
                foreach (var pending in debt) pending.NotBeforeUtc = now;
                var id = Guid.NewGuid();
                db.SourceScanRequests.Add(new() { Id = id, SourceRootId = RootId.Value, RequestKind = 1,
                    RequestedBy = "fixture-namespace-backstop", RequestedAtUtc = now, IsReleased = true,
                    ReleasedAtUtc = now, State = (int)SourceScanRequestState.Released });
                db.SourceScanJobs.Add(new() { Id = Guid.NewGuid(), SourceScanRequestId = id, State = (int)SourceScanJobState.Pending,
                    DueAtUtc = now, CreatedAtUtc = now, UpdatedAtUtc = now });
                db.SourceScanOutbox.Add(new() { Id = Guid.NewGuid(), SourceScanRequestId = id, Operation = "source.scan",
                    IdempotencyKey = $"source-scan:{id:N}", DueAtUtc = now, CreatedAtUtc = now });
                await db.SaveChangesAsync();
                var backstop = Assert.IsType<ClaimedSourceScan>(await Control.ClaimNextReleasedAsync("fixture-namespace-backstop", now, TimeSpan.FromMinutes(10), default));
                await Control.CompleteAsync(backstop, await worker.ScanAsync(backstop.SourceRoot, backstop.ScanRequest, default), null, default);
                await Task.Delay(100);
            }
            Assert.Fail("The native fixture could not establish a complete initial namespace through its real backstop.");
        }
        public ValueTask DisposeAsync()
        {
            Artifacts?.Dispose();
            var actual = Path.GetFullPath(_area);
            if (!actual.StartsWith(Ground + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new IOException("Verification fixture escaped its staging ground.");
            if (Directory.Exists(actual))
            {
                foreach (var file in Directory.EnumerateFiles(actual, "*", SearchOption.AllDirectories))
                    File.SetAttributes(file, File.GetAttributes(file) & ~FileAttributes.ReadOnly);
                Directory.Delete(actual, true);
            }
            return ValueTask.CompletedTask;
        }
    }
}
