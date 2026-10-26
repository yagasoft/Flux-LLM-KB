using System.Text.Json;
using FluxKnowledge.Application.Contracts;
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

public sealed class TargetedSourceScanIntegrationTests(NativeSqlServerFixture fixture) : IClassFixture<NativeSqlServerFixture>, IAsyncLifetime
{
    public async Task InitializeAsync()
    {
        await SqlTestData.ClearPhase3SourceDataAsync(fixture);
        await using var db = new SourceJournalStoreIntegrationTests.ContextFactory(fixture.ConnectionString).CreateDbContext();
        await db.SourceVolumeJournalCheckpoints.ExecuteDeleteAsync();
    }
    public Task DisposeAsync() => Task.CompletedTask;

    [NativeSqlServerFact]
    public async Task Feature_off_fences_incremental_proofs_once_and_reenable_requires_a_new_baseline()
    {
        var area = Path.Combine(@"I:\Codex Workspaces\Staging\FluxKnowledge", "JournalFeatureOff-" + Guid.NewGuid().ToString("N"));
        var files = Path.Combine(area, "Sources");
        Directory.CreateDirectory(files);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(files, "one.txt"), "first evidence");
            var factory = new SourceJournalStoreIntegrationTests.ContextFactory(fixture.ConnectionString);
            var clock = new Clock(DateTimeOffset.UtcNow);
            var commands = new NativeCorpusCommandService(new NativeOperationService(new SqlNativeOperationStore(factory, TimeProvider.System), []),
                new SqlNativeCorpusActionStore(factory, new SourceRootPathPolicy(new LocalIngressOptions([files])), new LocalPrivateContentDisclosure()));
            var mutation = new NativeCorpusMutation("root_create", JsonSerializer.SerializeToElement(new { path = files, displayName = "Feature rollback", recursive = true, maximumFileBytes = 1024 }));
            var preview = await commands.PreviewAsync(mutation, "test", default);
            await commands.CommitAsync(mutation, preview.ConfirmationId, "feature-off-" + Guid.NewGuid().ToString("N"), "test", default);
            clock.Now = DateTimeOffset.UtcNow;
            using var artifacts = new ContentAddressedSourceArtifactStore(Path.Combine(area, "Retained"));
            var options = new SourceJournalOptions { Enabled = true, ApprovedVolumes = [WindowsSourceChangeJournalReader.CanonicalVolume(files)] };
            var control = new SqlSourceScanStore(factory, clock);
            long reads = 0;
            var worker = new SourceScanWorker(new LocalSourceEnumerator { SourceBytesRead = n => reads += n }, control, artifacts,
                new SqlSourceActivityStore(factory, clock), incrementalCoordinator: new SqlSourceIncrementalCoordinator(factory, clock,
                    new WindowsSourceChangeJournalReader(), new WindowsSourceObservationReader(), artifacts, options));
            var root = (await Scan("baseline")).SourceRoot;
            long previousEpoch;
            await using (var inspect = factory.CreateDbContext()) previousEpoch = (await inspect.SourceRootScanStates.SingleAsync()).BaselineEpoch;

            options.Enabled = false;
            clock.Now += root.ReconciliationCadence + TimeSpan.FromSeconds(1);
            await Scan("legacy");
            long disabledEpoch;
            await using (var inspect = factory.CreateDbContext())
            {
                var state = await inspect.SourceRootScanStates.SingleAsync();
                Assert.Equal("Legacy", state.Mode);
                Assert.True(state.RecoveryRequired);
                disabledEpoch = state.BaselineEpoch;
                Assert.True(disabledEpoch > previousEpoch);
            }
            clock.Now += root.ReconciliationCadence + TimeSpan.FromSeconds(1);
            await Scan("legacy-again");
            await using (var inspect = factory.CreateDbContext()) Assert.Equal(disabledEpoch, (await inspect.SourceRootScanStates.SingleAsync()).BaselineEpoch);

            options.Enabled = true;
            clock.Now += root.ReconciliationCadence + TimeSpan.FromSeconds(1);
            reads = 0;
            await Scan("reenabled");
            Assert.Equal("first evidence"u8.Length, reads);
            await using var verify = factory.CreateDbContext();
            Assert.False((await verify.SourceRootScanStates.SingleAsync()).RecoveryRequired);

            async Task<ClaimedSourceScan> Scan(string owner)
            {
                var claim = Assert.IsType<ClaimedSourceScan>(await control.ClaimNextReleasedAsync(owner, clock.Now, TimeSpan.FromMinutes(10), default));
                await control.CompleteAsync(claim, await worker.ScanAsync(claim.SourceRoot, claim.ScanRequest, default), null, default);
                return claim;
            }
        }
        finally { Directory.Delete(area, true); }
    }

    [NativeSqlServerTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Fallback_baseline_finds_a_missed_deletion_and_preserves_an_inaccessible_sibling(bool repeatFailure)
    {
        var area = Path.Combine(@"I:\Codex Workspaces\Staging\FluxKnowledge", "JournalMissedDeletion-" + Guid.NewGuid().ToString("N"));
        var files = Path.Combine(area, "Sources");
        Directory.CreateDirectory(files);
        try
        {
            var removed = Path.Combine(files, "removed.txt");
            var inaccessible = Path.Combine(files, "locked.txt");
            await File.WriteAllTextAsync(removed, "removed evidence");
            await File.WriteAllTextAsync(inaccessible, "preserved evidence");
            var factory = new SourceJournalStoreIntegrationTests.ContextFactory(fixture.ConnectionString);
            var clock = new Clock(DateTimeOffset.UtcNow);
            var commands = new NativeCorpusCommandService(new NativeOperationService(new SqlNativeOperationStore(factory, TimeProvider.System), []),
                new SqlNativeCorpusActionStore(factory, new SourceRootPathPolicy(new LocalIngressOptions([files])), new LocalPrivateContentDisclosure()));
            var mutation = new NativeCorpusMutation("root_create", JsonSerializer.SerializeToElement(new { path = files, displayName = "Missed deletion", recursive = true, maximumFileBytes = 1024 }));
            var preview = await commands.PreviewAsync(mutation, "test", default);
            await commands.CommitAsync(mutation, preview.ConfirmationId, "missed-deletion-" + Guid.NewGuid().ToString("N"), "test", default);
            clock.Now = DateTimeOffset.UtcNow;
            var reader = new SwitchableReader { Unavailable = true };
            using var artifacts = new ContentAddressedSourceArtifactStore(Path.Combine(area, "Retained"));
            var control = new SqlSourceScanStore(factory, clock);
            var coordinator = new SqlSourceIncrementalCoordinator(factory, clock, reader, new WindowsSourceObservationReader(), artifacts,
                new() { Enabled = true, ApprovedVolumes = [WindowsSourceChangeJournalReader.CanonicalVolume(files)] });
            var worker = new SourceScanWorker(new LocalSourceEnumerator(), control, artifacts, new SqlSourceActivityStore(factory, clock), incrementalCoordinator: coordinator);
            var first = Assert.IsType<ClaimedSourceScan>(await control.ClaimNextReleasedAsync("baseline", clock.Now, TimeSpan.FromMinutes(10), default));
            await control.CompleteAsync(first, await worker.ScanAsync(first.SourceRoot, first.ScanRequest, default), null, default);

            File.Delete(removed); // No watcher hint is recorded, as after downtime.
            using var locked = new FileStream(inaccessible, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            clock.Now += first.SourceRoot.ReconciliationCadence + TimeSpan.FromSeconds(1);
            var retry = Assert.IsType<ClaimedSourceScan>(await control.ClaimNextReleasedAsync("backstop", clock.Now, TimeSpan.FromMinutes(10), default));
            await control.CompleteAsync(retry, await worker.ScanAsync(retry.SourceRoot, retry.ScanRequest, default), null, default);

            if (repeatFailure)
            {
                clock.Now += first.SourceRoot.ReconciliationCadence + TimeSpan.FromSeconds(1);
                var repeated = Assert.IsType<ClaimedSourceScan>(await control.ClaimNextReleasedAsync("parked-retry", clock.Now, TimeSpan.FromMinutes(10), default));
                await control.CompleteAsync(repeated, await worker.ScanAsync(repeated.SourceRoot, repeated.ScanRequest, default), null, default);
            }

            await using var verify = factory.CreateDbContext();
            Assert.NotNull((await verify.SourceRevisions.SingleAsync(x => x.CanonicalPath == removed)).SuppressedAtUtc);
            Assert.Null((await verify.SourceRevisions.SingleAsync(x => x.CanonicalPath == inaccessible)).SuppressedAtUtc);
            var state = await verify.SourceRootScanStates.SingleAsync();
            Assert.Equal("Fallback", state.Mode);
            Assert.True(state.RecoveryRequired);
            Assert.False(state.NamespaceComplete);
            Assert.Contains("locked.txt", state.DeferredEvidenceJson!);
            var parked = await verify.SourcePendingChanges.SingleAsync(x => x.RelativePath == "locked.txt");
            Assert.Equal(parked.Generation, parked.RetryGeneration);
            Assert.True(parked.NotBeforeUtc > clock.Now);
        }
        finally { Directory.Delete(area, true); }
    }

    [NativeSqlServerFact]
    public async Task Unavailable_startup_preserves_baseline_debt_and_forced_unsealed_backstops_until_journal_returns()
    {
        var area = Path.Combine(@"I:\Codex Workspaces\Staging\FluxKnowledge", "JournalStartupFallback-" + Guid.NewGuid().ToString("N"));
        var files = Path.Combine(area, "Sources");
        Directory.CreateDirectory(files);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(files, "one.txt"), "startup evidence");
            var factory = new SourceJournalStoreIntegrationTests.ContextFactory(fixture.ConnectionString);
            var clock = new Clock(DateTimeOffset.UtcNow);
            var commands = new NativeCorpusCommandService(new NativeOperationService(new SqlNativeOperationStore(factory, TimeProvider.System), []),
                new SqlNativeCorpusActionStore(factory, new SourceRootPathPolicy(new LocalIngressOptions([files])), new LocalPrivateContentDisclosure()));
            var mutation = new NativeCorpusMutation("root_create", JsonSerializer.SerializeToElement(new { path = files, displayName = "Unavailable startup", recursive = true, maximumFileBytes = 1024 }));
            var preview = await commands.PreviewAsync(mutation, "test", default);
            await commands.CommitAsync(mutation, preview.ConfirmationId, "startup-fallback-" + Guid.NewGuid().ToString("N"), "test", default);
            clock.Now = DateTimeOffset.UtcNow;
            var reader = new SwitchableReader { Unavailable = true };
            long reads = 0;
            using var artifacts = new ContentAddressedSourceArtifactStore(Path.Combine(area, "Retained"));
            var enumeration = new LocalSourceEnumerator { SourceBytesRead = n => reads += n };
            var control = new SqlSourceScanStore(factory, clock);
            var coordinator = new SqlSourceIncrementalCoordinator(factory, clock, reader, new WindowsSourceObservationReader(), artifacts,
                new() { Enabled = true, ApprovedVolumes = [WindowsSourceChangeJournalReader.CanonicalVolume(files)] });
            var worker = new SourceScanWorker(enumeration, control, artifacts, new SqlSourceActivityStore(factory, clock), incrementalCoordinator: coordinator);
            var first = Assert.IsType<ClaimedSourceScan>(await control.ClaimNextReleasedAsync("startup", clock.Now, TimeSpan.FromMinutes(10), default));
            await control.CompleteAsync(first, await worker.ScanAsync(first.SourceRoot, first.ScanRequest, default), null, default);
            Assert.Equal("startup evidence"u8.Length, reads);
            Assert.Equal(0, await coordinator.PumpAsync(default)); // Baseline debt cannot make unavailable mode spin.
            await using (var inspect = factory.CreateDbContext())
            {
                var state = await inspect.SourceRootScanStates.SingleAsync();
                Assert.Equal("Fallback", state.Mode);
                Assert.True(state.RecoveryRequired);
                Assert.False(await inspect.SourceVolumeJournalCheckpoints.AnyAsync());
            }
            clock.Now += first.SourceRoot.ReconciliationCadence + TimeSpan.FromSeconds(1);
            var retry = Assert.IsType<ClaimedSourceScan>(await control.ClaimNextReleasedAsync("backstop", clock.Now, TimeSpan.FromMinutes(10), default));
            reads = 0;
            await control.CompleteAsync(retry, await worker.ScanAsync(retry.SourceRoot, retry.ScanRequest, default), null, default);
            Assert.Equal("startup evidence"u8.Length, reads); // Unsealed entries retain their ordinary forced-check deadline.
            reader.Unavailable = false;
            clock.Now += first.SourceRoot.ReconciliationCadence + TimeSpan.FromSeconds(1);
            var restored = Assert.IsType<ClaimedSourceScan>(await control.ClaimNextReleasedAsync("restored", clock.Now, TimeSpan.FromMinutes(10), default));
            reads = 0;
            await control.CompleteAsync(restored, await worker.ScanAsync(restored.SourceRoot, restored.ScanRequest, default), null, default);
            Assert.Equal("startup evidence"u8.Length, reads); // A first journal baseline cannot inherit heuristic trust.
            await using var verify = factory.CreateDbContext();
            Assert.False((await verify.SourceRootScanStates.SingleAsync()).RecoveryRequired);
            Assert.True(await verify.SourceVolumeJournalCheckpoints.AnyAsync());
        }
        finally { Directory.Delete(area, true); }
    }

    [NativeSqlServerFact]
    public async Task Unavailable_journal_keeps_targeted_force_capture_and_metadata_backstop_without_certifying_coverage()
    {
        var area = Path.Combine(@"I:\Codex Workspaces\Staging\FluxKnowledge", "JournalFallback-" + Guid.NewGuid().ToString("N"));
        var files = Path.Combine(area, "Sources");
        Directory.CreateDirectory(files);
        try
        {
            var edited = Path.Combine(files, "one.txt");
            await File.WriteAllTextAsync(edited, "first evidence");
            await File.WriteAllTextAsync(Path.Combine(files, "other.txt"), "stable sibling");
            var factory = new SourceJournalStoreIntegrationTests.ContextFactory(fixture.ConnectionString);
            var clock = new Clock(DateTimeOffset.UtcNow);
            var commands = new NativeCorpusCommandService(new NativeOperationService(new SqlNativeOperationStore(factory, TimeProvider.System), []),
                new SqlNativeCorpusActionStore(factory, new SourceRootPathPolicy(new LocalIngressOptions([files])), new LocalPrivateContentDisclosure()));
            var mutation = new NativeCorpusMutation("root_create", JsonSerializer.SerializeToElement(new { path = files, displayName = "Fallback pipeline", recursive = true, maximumFileBytes = 1024 }));
            var preview = await commands.PreviewAsync(mutation, "test", default);
            await commands.CommitAsync(mutation, preview.ConfirmationId, "fallback-" + Guid.NewGuid().ToString("N"), "test", default);
            clock.Now = DateTimeOffset.UtcNow;
            long reads = 0;
            var options = new SourceJournalOptions { Enabled = true, ApprovedVolumes = [WindowsSourceChangeJournalReader.CanonicalVolume(files)] };
            var reader = new SwitchableReader();
            using var artifacts = new ContentAddressedSourceArtifactStore(Path.Combine(area, "Retained"));
            var enumeration = new LocalSourceEnumerator { SourceBytesRead = n => reads += n };
            var control = new SqlSourceScanStore(factory, clock);
            var incremental = new SqlSourceIncrementalCoordinator(factory, clock, reader, new WindowsSourceObservationReader(), artifacts, options);
            var worker = new SourceScanWorker(enumeration, control, artifacts, new SqlSourceActivityStore(factory, clock), incrementalCoordinator: incremental);
            var baseline = Assert.IsType<ClaimedSourceScan>(await control.ClaimNextReleasedAsync("baseline", clock.Now, TimeSpan.FromMinutes(10), default));
            QualifyRoot(baseline.SourceRoot, options);
            await control.CompleteAsync(baseline, await worker.ScanAsync(baseline.SourceRoot, baseline.ScanRequest, default), null, default);
            reader.Unavailable = true;
            clock.Now += TimeSpan.FromSeconds(3);
            await incremental.PumpAsync(default);
            long savedCursor;
            await using (var inspect = factory.CreateDbContext())
                savedCursor = (await inspect.SourceVolumeJournalCheckpoints.SingleAsync()).NextUsn;

            var stamp = File.GetLastWriteTimeUtc(edited);
            await File.WriteAllTextAsync(edited, "later evidence"); // Same length; the known hint still forces capture.
            File.SetLastWriteTimeUtc(edited, stamp);
            var watch = new SqlSourceRootWatchStore(factory, clock, options);
            await watch.RecordSignalAsync(new(baseline.SourceRoot.Id, SourceWatchSignalKind.Changed, clock.Now, [new("one.txt")]), default);
            clock.Now += TimeSpan.FromSeconds(3);
            var batch = Assert.IsType<ClaimedSourceWatchBatch>(await watch.ClaimDueBatchAsync(clock.Now, "watch", TimeSpan.FromMinutes(1), default));
            await watch.ReleaseScanAsync(batch, default);
            await incremental.PumpAsync(default);
            var target = Assert.IsType<ClaimedSourceScan>(await control.ClaimNextReleasedAsync("fallback", clock.Now, TimeSpan.FromMinutes(10), default));
            reads = 0;
            var result = await worker.ScanAsync(target.SourceRoot, target.ScanRequest, default);
            await control.CompleteAsync(target, result, null, default);
            Assert.Equal("later evidence"u8.Length, reads);
            Assert.Equal(0, enumeration.LastEnumeratedEntries);
            await using (var inspect = factory.CreateDbContext())
            {
                Assert.Equal("Fallback", (await inspect.SourceRootScanStates.SingleAsync()).Mode);
                Assert.Equal(savedCursor, (await inspect.SourceVolumeJournalCheckpoints.SingleAsync()).NextUsn);
                var remaining = await inspect.SourcePendingChanges.ToArrayAsync();
                Assert.DoesNotContain(remaining, x => x.RelativePath == "one.txt");
                // Native volume traffic can leave conservative, paced namespace debt. It is
                // distinct from the known content obligation just completed.
                Assert.All(remaining, x =>
                {
                    Assert.Equal(".", x.RelativePath);
                    Assert.Equal(SourcePendingReasons.NamespaceMetadata, x.ReasonFlags);
                    Assert.True(x.NotBeforeUtc > clock.Now);
                });
                Assert.Single(await inspect.SourceRevisions.Where(x => x.CanonicalPath.EndsWith("other.txt")).ToArrayAsync());
            }

            clock.Now += baseline.SourceRoot.ReconciliationCadence;
            var backstop = Assert.IsType<ClaimedSourceScan>(await control.ClaimNextReleasedAsync("backstop", clock.Now, TimeSpan.FromMinutes(10), default));
            reads = 0;
            await control.CompleteAsync(backstop, await worker.ScanAsync(backstop.SourceRoot, backstop.ScanRequest, default), null, default);
            Assert.Equal("later evidence"u8.Length, reads); // Only the unsealed fallback capture reaches its forced deadline.
            Assert.Equal(2, enumeration.LastEnumeratedEntries); // The independent authority backstop enumerates metadata.
            await using (var verify = factory.CreateDbContext())
            {
                Assert.Equal("Fallback", (await verify.SourceRootScanStates.SingleAsync()).Mode);
                Assert.Equal(savedCursor, (await verify.SourceVolumeJournalCheckpoints.SingleAsync()).NextUsn);
            }
            reader.Unavailable = false;
            clock.Now += TimeSpan.FromSeconds(3);
            await incremental.PumpAsync(default);
            clock.Now += TimeSpan.FromSeconds(3);
            await incremental.PumpAsync(default);
            var restored = Assert.IsType<ClaimedSourceScan>(await control.ClaimNextReleasedAsync("restored", clock.Now, TimeSpan.FromMinutes(10), default));
            reads = 0;
            await control.CompleteAsync(restored, await worker.ScanAsync(restored.SourceRoot, restored.ScanRequest, default), null, default);
            Assert.Equal("later evidence"u8.Length, reads);
            await using var restoredState = factory.CreateDbContext();
            Assert.True((await restoredState.SourceVolumeJournalCheckpoints.SingleAsync()).NextUsn > savedCursor);
            Assert.NotEqual("Fallback", (await restoredState.SourceRootScanStates.SingleAsync()).Mode);
            var observation = (await restoredState.SourceInventoryEntries.SingleAsync(x => x.RelativePath == "one.txt")).ObservationJson!;
            Assert.True(JsonDocument.Parse(observation).RootElement.GetProperty("JournalCovered").GetBoolean());
        }
        finally { Directory.Delete(area, true); }
    }

    [NativeSqlServerFact]
    public async Task Real_baseline_then_single_edit_and_quiet_scan_preserve_unrelated_content_and_revision()
    {
        var area = Path.Combine(@"I:\Codex Workspaces\Staging\FluxKnowledge", "JournalPipeline-" + Guid.NewGuid().ToString("N"));
        var files = Path.Combine(area, "Sources"); var retained = Path.Combine(area, "Retained");
        Directory.CreateDirectory(files);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(files, "one.txt"), "first evidence");
            await File.WriteAllTextAsync(Path.Combine(files, "other.txt"), "second evidence");
            var clock = new Clock(DateTimeOffset.UtcNow);
            var factory = new SourceJournalStoreIntegrationTests.ContextFactory(fixture.ConnectionString);
            var commands = new NativeCorpusCommandService(new NativeOperationService(new SqlNativeOperationStore(factory, TimeProvider.System), []),
                new SqlNativeCorpusActionStore(factory, new SourceRootPathPolicy(new LocalIngressOptions([files])), new LocalPrivateContentDisclosure()));
            var mutation = new NativeCorpusMutation("root_create", JsonSerializer.SerializeToElement(new { path = files, displayName = "Journal pipeline", recursive = true, maximumFileBytes = 1024 }));
            var preview = await commands.PreviewAsync(mutation, "test", default);
            await commands.CommitAsync(mutation, preview.ConfirmationId, "journal-" + Guid.NewGuid().ToString("N"), "test", default);
            clock.Now = DateTimeOffset.UtcNow;
            long reads = 0, blobReads = 0;
            using var artifacts = new ContentAddressedSourceArtifactStore(retained) { RetainedBytesRead = n => blobReads += n };
            var enumeration = new LocalSourceEnumerator { SourceBytesRead = n => reads += n };
            var control = new SqlSourceScanStore(factory, clock);
            var options = new SourceJournalOptions { Enabled = true, ApprovedVolumes = [WindowsSourceChangeJournalReader.CanonicalVolume(files)] };
            var incremental = new SqlSourceIncrementalCoordinator(factory, clock, new WindowsSourceChangeJournalReader(),
                new WindowsSourceObservationReader(), artifacts, options);
            var worker = new SourceScanWorker(enumeration, control, artifacts, new SqlSourceActivityStore(factory, TimeProvider.System),
                incrementalCoordinator: incremental);
            var first = Assert.IsType<ClaimedSourceScan>(await control.ClaimNextReleasedAsync("test", clock.GetUtcNow(), TimeSpan.FromMinutes(10), default));
            QualifyRoot(first.SourceRoot, options);
            var result = await worker.ScanAsync(first.SourceRoot, first.ScanRequest, default);
            await control.CompleteAsync(first, result, null, default);
            Assert.Equal(2, result.DiscoveredCount);
            Guid unchanged;
            await using (var db = factory.CreateDbContext())
                unchanged = (await db.SourceRevisions.SingleAsync(x => x.SourceRootId == first.SourceRoot.Id.Value && x.CanonicalPath.EndsWith("other.txt"))).Id;

            await File.WriteAllTextAsync(Path.Combine(files, "one.txt"), "new evidence");
            clock.Now += TimeSpan.FromSeconds(3); // The next scheduled poll accepts the edit.
            await incremental.PumpAsync(default);
            clock.Now += TimeSpan.FromSeconds(3);
            await incremental.PumpAsync(default);
            var second = Assert.IsType<ClaimedSourceScan>(await control.ClaimNextReleasedAsync("test", clock.GetUtcNow(), TimeSpan.FromMinutes(10), default));
            reads = blobReads = 0;
            result = await worker.ScanAsync(second.SourceRoot, second.ScanRequest, default);
            await control.CompleteAsync(second, result, null, default);
            Assert.Equal("new evidence"u8.Length, reads);
            Assert.Equal(0, enumeration.LastEnumeratedEntries);
            await using (var db = factory.CreateDbContext())
            {
                Assert.Null((await db.SourceRevisions.SingleAsync(x => x.Id == unchanged)).SuppressedAtUtc);
                Assert.Single(await db.SourceRevisions.Where(x => x.SourceRootId == first.SourceRoot.Id.Value && x.CanonicalPath.EndsWith("other.txt")).ToListAsync());
            }
            clock.Now += first.SourceRoot.ReconciliationCadence + TimeSpan.FromSeconds(1);
            var quiet = Assert.IsType<ClaimedSourceScan>(await control.ClaimNextReleasedAsync("test", clock.GetUtcNow(), TimeSpan.FromMinutes(10), default));
            reads = blobReads = 0;
            result = await worker.ScanAsync(quiet.SourceRoot, quiet.ScanRequest, default);
            await control.CompleteAsync(quiet, result, null, default);
            Assert.Equal(0, reads); Assert.Equal(0, blobReads);
            Assert.Equal(2, enumeration.LastEnumeratedEntries); // Due metadata reconciliation keeps qualified content unread.

        }
        finally { Directory.Delete(area, true); }
    }

    private static void QualifyRoot(SourceRootConfiguration root, SourceJournalOptions options)
    {
        Assert.False(string.IsNullOrEmpty(root.PhysicalIdentityFingerprint));
        var environment = WindowsSourceJournalSealQualification.GetEnvironmentFingerprint();
        Assert.False(string.IsNullOrEmpty(environment));
        options.SealingEnabled = true;
        options.SealQualifications = [new(root.PhysicalIdentityFingerprint!, environment!, "local-ntfs-synthetic", "targeted-flow-fixture")];
    }

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        internal DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class SwitchableReader : ISourceChangeJournalReader
    {
        private readonly WindowsSourceChangeJournalReader _native = new();
        public bool Unavailable { get; set; }
        public ValueTask<JournalCapability> QueryAsync(string volumeIdentity, CancellationToken ct) =>
            Unavailable ? throw new IOException("Fixture journal unavailable") : _native.QueryAsync(volumeIdentity, ct);
        public ValueTask<JournalPage> ReadAsync(JournalCursor cursor, int maximumBytes, CancellationToken ct) =>
            Unavailable ? throw new IOException("Fixture journal unavailable") : _native.ReadAsync(cursor, maximumBytes, ct);
    }
}
