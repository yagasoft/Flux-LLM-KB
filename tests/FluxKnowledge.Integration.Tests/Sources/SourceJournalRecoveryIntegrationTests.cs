using System.Text.Json;
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

public sealed class SourceJournalRecoveryIntegrationTests(NativeSqlServerFixture fixture) : IClassFixture<NativeSqlServerFixture>, IAsyncLifetime
{
    public async Task InitializeAsync()
    {
        await SqlTestData.ClearPhase3SourceDataAsync(fixture);
        await using var db = new SourceJournalStoreIntegrationTests.ContextFactory(fixture.ConnectionString).CreateDbContext();
        await db.SourceVolumeJournalCheckpoints.ExecuteDeleteAsync();
    }
    public Task DisposeAsync() => Task.CompletedTask;

    [NativeSqlServerTheory]
    [InlineData("gap")]
    [InlineData("unavailable")]
    [InlineData("epoch")]
    public async Task Capture_barrier_failure_keeps_retention_but_cannot_publish_an_observation_proof(string fault)
    {
        var area = NewArea();
        var files = Path.Combine(area, "Sources");
        Directory.CreateDirectory(files);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(files, "one.txt"), "captured evidence");
            var factory = new SourceJournalStoreIntegrationTests.ContextFactory(fixture.ConnectionString);
            var control = new SqlSourceScanStore(factory, TimeProvider.System);
            var claim = await RegisterAndClaimAsync(factory, control, files);
            var volume = WindowsSourceChangeJournalReader.CanonicalVolume(files);
            using var artifacts = new ContentAddressedSourceArtifactStore(Path.Combine(area, "Retained"));
            var reader = new GapReader(volume) { OnQuery = async (call, ct) =>
            {
                if (call != 2) return; // Prepare succeeded; this is after capture and guarded observation acquisition.
                if (fault == "gap") throw new SourceJournalGapException("Fixture history lost at the capture barrier");
                if (fault == "unavailable") throw new IOException("Fixture helper failed at the capture barrier");
                await using var invalidate = factory.CreateDbContext();
                var state = await invalidate.SourceRootScanStates.SingleAsync(x => x.SourceRootId == claim.SourceRoot.Id.Value, ct);
                SqlSourceIncrementalState.InvalidateCoverage(state, "ancestor-authority-changed", 100);
                await invalidate.SaveChangesAsync(ct);
            } };
            var coordinator = new SqlSourceIncrementalCoordinator(factory, TimeProvider.System, reader,
                new WindowsSourceObservationReader(), artifacts, new() { Enabled = true, ApprovedVolumes = [volume] });
            var worker = new SourceScanWorker(new LocalSourceEnumerator(), control, artifacts,
                new SqlSourceActivityStore(factory, TimeProvider.System), incrementalCoordinator: coordinator);
            var error = await Assert.ThrowsAnyAsync<IOException>(() => worker.ScanAsync(claim.SourceRoot, claim.ScanRequest, default).AsTask());
            if (fault == "gap") Assert.IsType<SourceJournalGapException>(error);
            if (fault == "epoch") Assert.IsType<SourceSnapshotChangedException>(error);
            await using var verify = factory.CreateDbContext();
            Assert.Equal(1, await verify.SourceArtifacts.CountAsync());
            Assert.Equal(1, await verify.SourceRevisions.CountAsync(x => x.SuppressedAtUtc == null));
            Assert.False(await verify.SourceInventoryEntries.AnyAsync(x => x.SourceRootId == claim.SourceRoot.Id.Value && !x.IsDirectory));
            Assert.False(await verify.SourceActivities.AnyAsync());
        }
        finally { Directory.Delete(area, true); }
    }

    [NativeSqlServerFact]
    public async Task Higher_claim_can_replace_a_journal_plan_with_fallback_while_later_pending_edits_survive()
    {
        var area = NewArea();
        var files = Path.Combine(area, "Sources");
        Directory.CreateDirectory(files);
        try
        {
            var factory = new SourceJournalStoreIntegrationTests.ContextFactory(fixture.ConnectionString);
            var control = new SqlSourceScanStore(factory, TimeProvider.System);
            var first = await RegisterAndClaimAsync(factory, control, files);
            var volume = WindowsSourceChangeJournalReader.CanonicalVolume(files);
            var options = new SourceJournalOptions { Enabled = true, ApprovedVolumes = [volume] };
            using var artifacts = new ContentAddressedSourceArtifactStore(Path.Combine(area, "Retained"));
            var reader = new GapReader(volume);
            var coordinator = new SqlSourceIncrementalCoordinator(factory, TimeProvider.System, reader, new WindowsSourceObservationReader(), artifacts, options);
            var original = Assert.IsType<SourceIncrementalPlan>(await coordinator.PrepareAsync(first.SourceRoot, first.ScanRequest, default));
            reader.Unavailable = true;
            await Assert.ThrowsAsync<SourceSnapshotChangedException>(() => coordinator.PrepareAsync(first.SourceRoot, first.ScanRequest, default).AsTask());
            await control.CompleteAsync(first, new(first.SourceRoot.Id, first.ScanRequest.Id, 0, 0, 0, 0) { IsCompleteInventory = false }, "journal-unavailable", default);
            var watch = new SqlSourceRootWatchStore(factory, TimeProvider.System, options);
            async Task SignalAsync()
            {
                var now = DateTimeOffset.UtcNow;
                await watch.RecordSignalAsync(new(first.SourceRoot.Id, SourceWatchSignalKind.Changed, now, [new("later.txt")]), default);
                var batch = Assert.IsType<ClaimedSourceWatchBatch>(await watch.ClaimDueBatchAsync(now.AddSeconds(3), "watch", TimeSpan.FromMinutes(1), default));
                await watch.ReleaseScanAsync(batch, default);
            }
            await SignalAsync();
            var retry = Assert.IsType<ClaimedSourceScan>(await control.ClaimNextReleasedAsync("retry", DateTimeOffset.UtcNow.AddMinutes(2), TimeSpan.FromMinutes(10), default));
            var fallback = Assert.IsType<SourceIncrementalPlan>(await coordinator.PrepareAsync(retry.SourceRoot, retry.ScanRequest, default));
            Assert.Equal(first.ScanRequest.Id, retry.ScanRequest.Id);
            Assert.Equal(original.ManifestId, fallback.SupersedesManifestId);
            Assert.True(fallback.IsBaseline);
            Assert.True(fallback.IsFallback);
            Assert.Null(fallback.Cursor);
            var obligation = Assert.Single(fallback.Targets);
            await File.WriteAllTextAsync(Path.Combine(files, "later.txt"), "later generation evidence");
            await SignalAsync();
            var worker = new SourceScanWorker(new LocalSourceEnumerator(), control, artifacts,
                new SqlSourceActivityStore(factory, TimeProvider.System), incrementalCoordinator: coordinator);
            await control.CompleteAsync(retry, await worker.ScanAsync(retry.SourceRoot, retry.ScanRequest, default), null, default);
            await using var verify = factory.CreateDbContext();
            var pending = Assert.Single(await verify.SourcePendingChanges.ToArrayAsync());
            Assert.Equal("later.txt", pending.RelativePath);
            Assert.True(pending.Generation > obligation.Generation);
            var state = await verify.SourceRootScanStates.SingleAsync();
            Assert.Equal("Fallback", state.Mode);
            Assert.True(state.RecoveryRequired);
        }
        finally { Directory.Delete(area, true); }
    }

    [NativeSqlServerTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task New_uncertainty_during_namespace_completion_survives_without_invalidating_covered_content(bool scopedWork)
    {
        var area = NewArea();
        Directory.CreateDirectory(area);
        try
        {
            var factory = new SourceJournalStoreIntegrationTests.ContextFactory(fixture.ConnectionString);
            var control = new SqlSourceScanStore(factory, TimeProvider.System);
            var claim = await RegisterAndClaimAsync(factory, control, area);
            var volume = WindowsSourceChangeJournalReader.CanonicalVolume(area);
            using var artifacts = new ContentAddressedSourceArtifactStore(Path.Combine(area, "Retained"));
            var reader = new GapReader(volume);
            var coordinator = new SqlSourceIncrementalCoordinator(factory, TimeProvider.System, reader,
                new WindowsSourceObservationReader(), artifacts, new() { Enabled = true, ApprovedVolumes = [volume] });
            var plan = Assert.IsType<SourceIncrementalPlan>(await coordinator.PrepareAsync(claim.SourceRoot, claim.ScanRequest, default));
            var store = new SqlSourceChangeJournalStore(factory, TimeProvider.System);
            var read = Assert.IsType<JournalReadClaim>(await store.ClaimVolumeAsync(volume, DateTimeOffset.UtcNow, true, default));
            await store.AcceptAsync(read, new(read.Cursor, read.Cursor with { NextUsn = 500 },
                [new(200, "000000000000002A", "0000000000009999", "unresolved.txt", 1, false)]), default);
            reader.NextUsn = 500;
            if (scopedWork)
            {
                await using var update = factory.CreateDbContext();
                var current = await update.SourceRootScanStates.SingleAsync(x => x.SourceRootId == claim.SourceRoot.Id.Value);
                var newer = await update.SourcePendingChanges.SingleAsync(x => x.SourceRootId == claim.SourceRoot.Id.Value);
                newer.ReasonFlags |= SourcePendingReasons.ScopedNamespace;
                newer.Generation = ++current.Generation;
                newer.NotBeforeUtc = DateTimeOffset.UtcNow;
                await update.SaveChangesAsync();
            }
            Assert.False(await coordinator.CompleteAsync(claim.SourceRoot, claim.ScanRequest, plan, [], [], new HashSet<SourceRevisionId>(), null, default));
            await using var verify = factory.CreateDbContext();
            var state = await verify.SourceRootScanStates.SingleAsync(x => x.SourceRootId == claim.SourceRoot.Id.Value);
            Assert.Equal(plan.BaselineEpoch, state.BaselineEpoch);
            Assert.Equal(plan.RecoveryGeneration, state.RecoveryGeneration);
            Assert.True(state.NamespaceGeneration > plan.NamespaceGeneration);
            Assert.False(state.NamespaceComplete);
            Assert.Equal("JournalPartial", state.Mode);
            var debt = Assert.Single(await verify.SourcePendingChanges.Where(x => x.SourceRootId == claim.SourceRoot.Id.Value).ToArrayAsync());
            Assert.Equal(SourcePendingReasons.NamespaceMetadata | (scopedWork ? SourcePendingReasons.ScopedNamespace : 0), debt.ReasonFlags);
            if (scopedWork)
            {
                Assert.NotEqual(debt.Generation, debt.RetryGeneration);
                Assert.True(debt.NotBeforeUtc <= DateTimeOffset.UtcNow);
            }
            else
            {
                Assert.Equal(debt.Generation, debt.RetryGeneration);
                Assert.True(debt.NotBeforeUtc > DateTimeOffset.UtcNow.AddMinutes(14));
            }
        }
        finally { Directory.Delete(area, true); }
    }

    [NativeSqlServerFact]
    public async Task Ancestor_invalidation_fences_an_older_completion_and_keeps_recovery_durable()
    {
        var area = NewArea();
        Directory.CreateDirectory(area);
        try
        {
            var factory = new SourceJournalStoreIntegrationTests.ContextFactory(fixture.ConnectionString);
            var control = new SqlSourceScanStore(factory, TimeProvider.System);
            var claim = await RegisterAndClaimAsync(factory, control, area);
            var volume = WindowsSourceChangeJournalReader.CanonicalVolume(area);
            await using (var setup = factory.CreateDbContext())
            {
                var root = await setup.SourceRootConfigurations.SingleAsync(x => x.Id == claim.SourceRoot.Id.Value);
                setup.SourceRootScanStates.Add(new() { SourceRootId = root.Id, VolumeIdentity = volume,
                    PolicyFingerprint = SqlRepositoryWorkRecovery.Policy(root), BaselineEpoch = 1, Mode = "Journal", CoveredUsn = 100 });
                setup.SourceVolumeJournalCheckpoints.Add(new() { VolumeIdentity = volume, JournalId = "0000000000000001", NextUsn = 100 });
                setup.SourceInventoryEntries.Add(new() { Id = Guid.NewGuid(), SourceRootId = root.Id, RelativePath = "!ancestor-0",
                    PathFingerprint = SqlSourceChangeJournalStore.PathKey("!ancestor-0"), FileId = "0000000000000007", IsDirectory = true, IsAncestor = true, BaselineEpoch = 1 });
                await setup.SaveChangesAsync();
            }
            var store = new SqlSourceChangeJournalStore(factory, TimeProvider.System);
            var read = Assert.IsType<JournalReadClaim>(await store.ClaimVolumeAsync(volume, DateTimeOffset.UtcNow, true, default));
            await store.AcceptAsync(read, new(read.Cursor, read.Cursor with { NextUsn = 500 },
                [new(200, "0000000000000007", "0000000000000000", "ancestor", 0x00001000, true)]), default);
            using var artifacts = new ContentAddressedSourceArtifactStore(Path.Combine(area, "Retained"));
            var coordinator = new SqlSourceIncrementalCoordinator(factory, TimeProvider.System, new GapReader(volume),
                new WindowsSourceObservationReader(), artifacts, new() { Enabled = true, ApprovedVolumes = [volume] });
            var older = new SourceIncrementalPlan(Guid.NewGuid(), 1, false, "Journal", null, 0, []);
            await Assert.ThrowsAsync<SourceSnapshotChangedException>(() => coordinator.CompleteAsync(claim.SourceRoot, claim.ScanRequest,
                older, [], [], new HashSet<SourceRevisionId>(), null, default).AsTask());
            await using var verify = factory.CreateDbContext();
            var state = await verify.SourceRootScanStates.SingleAsync(x => x.SourceRootId == claim.SourceRoot.Id.Value);
            Assert.True(state.BaselineEpoch > older.BaselineEpoch);
            Assert.Equal("BaselineRequired", state.Mode);
            Assert.Equal("ancestor-authority-changed", state.Reason);
        }
        finally { Directory.Delete(area, true); }
    }

    [NativeSqlServerTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Lost_history_queues_recovery_or_replans_an_invalidated_retry_and_refreshes_ancestors(bool keepRunning)
    {
        var area = NewArea();
        Directory.CreateDirectory(area);
        try
        {
            var factory = new SourceJournalStoreIntegrationTests.ContextFactory(fixture.ConnectionString);
            var control = new SqlSourceScanStore(factory, TimeProvider.System);
            var first = await RegisterAndClaimAsync(factory, control, area);
            var volume = WindowsSourceChangeJournalReader.CanonicalVolume(area);
            using var artifacts = new ContentAddressedSourceArtifactStore(Path.Combine(area, "Retained"));
            var reader = new GapReader(volume);
            var coordinator = new SqlSourceIncrementalCoordinator(factory, TimeProvider.System, reader,
                new WindowsSourceObservationReader(), artifacts, new() { Enabled = true, ApprovedVolumes = [volume] });
            var original = Assert.IsType<SourceIncrementalPlan>(await coordinator.PrepareAsync(first.SourceRoot, first.ScanRequest, default));
            await using (var setup = factory.CreateDbContext())
            {
                var state = await setup.SourceRootScanStates.SingleAsync(x => x.SourceRootId == first.SourceRoot.Id.Value);
                state.Mode = "Journal";
                await setup.SaveChangesAsync();
            }
            if (!keepRunning)
                await control.CompleteAsync(first, new(first.SourceRoot.Id, first.ScanRequest.Id, 0, 0, 0, 0), null, default);
            reader.LoseHistory = true;
            Assert.Equal(keepRunning ? 0 : 1, await coordinator.PumpAsync(default));
            if (keepRunning)
            {
                await Assert.ThrowsAsync<SourceSnapshotChangedException>(() => coordinator.CompleteAsync(first.SourceRoot, first.ScanRequest,
                    original, [], [], new HashSet<SourceRevisionId>(), null, default).AsTask());
                await control.CompleteAsync(first, new(first.SourceRoot.Id, first.ScanRequest.Id, 0, 0, 0, 0) { IsCompleteInventory = false }, "source-scan-epoch-changed", default);
            }
            var recovery = Assert.IsType<ClaimedSourceScan>(await control.ClaimNextReleasedAsync("recovery", DateTimeOffset.UtcNow.AddMinutes(2), TimeSpan.FromMinutes(10), default));
            var plan = Assert.IsType<SourceIncrementalPlan>(await coordinator.PrepareAsync(recovery.SourceRoot, recovery.ScanRequest, default));
            Assert.True(plan.IsBaseline);
            Assert.Equal(2, plan.BaselineEpoch);
            await using var verify = factory.CreateDbContext();
            var entries = await verify.SourceInventoryEntries.Where(x => x.SourceRootId == recovery.SourceRoot.Id.Value && x.IsDirectory).ToArrayAsync();
            Assert.NotEmpty(entries);
            Assert.All(entries, entry => Assert.Equal(plan.BaselineEpoch, entry.BaselineEpoch));
            Assert.Contains(entries, entry => entry.IsAncestor);
            Assert.Contains(entries, entry => entry.RelativePath == ".");
            Assert.False(await verify.SourcePendingChanges.AnyAsync(x => x.SourceRootId == recovery.SourceRoot.Id.Value));
            Assert.Equal(2ul, plan.Cursor!.JournalId);
            Assert.NotEqual(original.ManifestId, plan.ManifestId);
            if (keepRunning)
            {
                Assert.Equal(first.ScanRequest.Id, recovery.ScanRequest.Id);
                Assert.True(recovery.ScanRequest.Lease!.Generation > first.ScanRequest.Lease!.Generation);
            }
        }
        finally { Directory.Delete(area, true); }
    }

    private static string NewArea() => Path.Combine(@"I:\Codex Workspaces\Staging\FluxKnowledge", "JournalRecovery-" + Guid.NewGuid().ToString("N"));

    private static async Task<ClaimedSourceScan> RegisterAndClaimAsync(SourceJournalStoreIntegrationTests.ContextFactory factory, SqlSourceScanStore control, string area)
    {
        var commands = new NativeCorpusCommandService(new NativeOperationService(new SqlNativeOperationStore(factory, TimeProvider.System), []),
            new SqlNativeCorpusActionStore(factory, new SourceRootPathPolicy(new LocalIngressOptions([area])), new LocalPrivateContentDisclosure()));
        var mutation = new NativeCorpusMutation("root_create", JsonSerializer.SerializeToElement(new { path = area, displayName = "Recovery fixture", recursive = true, maximumFileBytes = 1024 }));
        var preview = await commands.PreviewAsync(mutation, "test", default);
        await commands.CommitAsync(mutation, preview.ConfirmationId, "recovery-" + Guid.NewGuid().ToString("N"), "test", default);
        return Assert.IsType<ClaimedSourceScan>(await control.ClaimNextReleasedAsync("baseline", DateTimeOffset.UtcNow, TimeSpan.FromMinutes(10), default));
    }

    private sealed class GapReader(string volume) : ISourceChangeJournalReader
    {
        private int _queries;
        public bool LoseHistory { get; set; }
        public bool Unavailable { get; set; }
        public long NextUsn { get; set; } = 100;
        public Func<int, CancellationToken, ValueTask>? OnQuery { get; init; }
        public async ValueTask<JournalCapability> QueryAsync(string volumeIdentity, CancellationToken ct)
        {
            if (Unavailable) throw new IOException("Fixture journal unavailable");
            if (OnQuery is not null) await OnQuery(++_queries, ct);
            return new(volume, LoseHistory ? 2ul : 1ul, 0, LoseHistory ? 1000 : NextUsn, 0, 1024, 128);
        }
        public ValueTask<JournalPage> ReadAsync(JournalCursor cursor, int maximumBytes, CancellationToken ct)
        {
            if (Unavailable) throw new IOException("Fixture journal unavailable");
            if (LoseHistory && cursor.JournalId != 2) throw new SourceJournalGapException("Fixture history replaced");
            return ValueTask.FromResult(new JournalPage(cursor, cursor, []));
        }
    }
}
