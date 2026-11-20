using System.Text.Json;
using System.Runtime.InteropServices;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using FluxKnowledge.Integrations.Files;
using FluxKnowledge.Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Sources;

public sealed partial class SourceJournalStoreIntegrationTests
{
    [NativeSqlServerTheory]
    [InlineData("case-only", true)]
    [InlineData("path-changed", false)]
    [InlineData("reparse", false)]
    [InlineData("mixed", false)]
    [InlineData("missing-half", false)]
    [InlineData("late-guard", false)]
    [InlineData("expired-guard", false)]
    public async Task Ancestor_authority_requires_guarded_path_and_journal_proof_and_recovers_once(string scenario, bool keepsBaseline)
    {
        var nativeStage = Environment.GetEnvironmentVariable("FLUXKNOWLEDGE_ANCESTOR_NATIVE_STAGE");
        var useNative = scenario == "case-only" && nativeStage is not null;
        if (useNative && (nativeStage != @"R:\" || new DriveInfo(nativeStage).VolumeLabel != "FKAncReconcile"))
            throw new InvalidOperationException("Expected the isolated NTFS journal stage.");
        var area = Path.Combine(useNative ? nativeStage! : @"I:\Codex Workspaces\Staging\FluxKnowledge", $"AncestorProof-{Guid.NewGuid():N}");
        var ancestorPath = Path.Combine(area, "TrackedParent");
        var rootPath = Path.Combine(ancestorPath, "Workspace");
        Directory.CreateDirectory(rootPath);
        var factory = new ContextFactory(fixture.ConnectionString);
        var rootId = Guid.NewGuid();
        var volume = WindowsSourceChangeJournalReader.CanonicalVolume(rootPath);
        var native = useNative ? new WindowsSourceChangeJournalReader() : null;
        var head = native is null ? null : await native.QueryAsync(volume, default);
        var initialUsn = head?.NextUsn ?? 100;
        var journalId = head?.JournalId ?? 1;
        var clock = new Clock(DateTimeOffset.UtcNow);
        var lastSweep = clock.Now.AddMinutes(-30);
        var id = PhysicalFileIdentity.GetDirectory(rootPath).IdentityFingerprint;
        var root = new SourceRootConfigurationEntity
        {
            Id = rootId, CanonicalPath = rootPath, DisplayName = "Ancestor proof fixture",
            Recursive = true, MaximumFileBytes = 1024, ConfigurationRevision = 1,
            ReconciliationCadenceSeconds = SourcePolicyDefaults.MetadataReconciliationSeconds,
            CreatedAtUtc = lastSweep, UpdatedAtUtc = lastSweep, LastScanCompletedAtUtc = lastSweep,
            HealthEvidenceJson = JsonSerializer.Serialize(new { physicalIdentity = new { IdentityFingerprint = id } })
        };
        var ancestors = new WindowsSourceObservationReader().ReadRootAncestors(SqlSourceRemovalReconciler.Restore(root));
        var changed = ancestors[^2];
        try
        {
            await using (var db = factory.CreateDbContext())
            {
                db.SourceRootConfigurations.Add(root);
                db.SourceRootScanStates.Add(new()
                {
                    SourceRootId = rootId, VolumeIdentity = volume, PolicyFingerprint = SqlRepositoryWorkRecovery.Policy(root),
                    BaselineEpoch = 3, RecoveryGeneration = 7, NamespaceComplete = true, NamespaceGeneration = 10,
                    Mode = "Journal", CoveredUsn = initialUsn, LastReconciliationAtUtc = lastSweep
                });
                db.SourceVolumeJournalCheckpoints.Add(new() { VolumeIdentity = volume, JournalId = journalId.ToString("X16"), NextUsn = initialUsn });
                db.SourceInventoryEntries.AddRange(ancestors.Select(x => new SourceInventoryEntryEntity
                {
                    Id = Guid.NewGuid(), SourceRootId = rootId, RelativePath = x.RelativePath,
                    PathFingerprint = SqlSourceChangeJournalStore.PathKey(x.RelativePath),
                    IsDirectory = true, IsAncestor = x.IsAncestor, FileId = x.FileId, ParentId = x.ParentId, BaselineEpoch = 3
                }));
                await db.SaveChangesAsync();
            }

            if (scenario == "path-changed") Directory.Move(ancestorPath, Path.Combine(area, "OtherParent"));
            JournalChange Old(long usn) => new(usn, changed.FileId, changed.ParentId, "trackedparent", 0x1000, true);
            JournalChange New(long usn) => new(usn, changed.FileId, changed.ParentId, "TrackedParent", 0x2000, true);
            JournalChange Reparse(long usn) => new(usn, changed.FileId, changed.ParentId, "TrackedParent", 0x00100060, true);
            var records = scenario switch
            {
                "reparse" => new[] { Reparse(110), Reparse(120) },
                "mixed" => [Old(110), New(120), Reparse(130)],
                "missing-half" => [Old(110)],
                _ => [Old(110), New(120), New(130) with { Reason = 0x80002000 }]
            };
            var guard = new TestCoverageGuard(scenario == "late-guard", scenario == "expired-guard" ? () => clock.Now = clock.Now.AddSeconds(31) : null);
            var store = new SqlSourceChangeJournalStore(factory, clock, guard);
            var claim = Assert.IsType<JournalReadClaim>(await store.ClaimVolumeAsync(volume, clock.Now, true, default));
            JournalPage page;
            if (native is not null)
            {
                Assert.True(MoveAncestor(ancestorPath, Path.Combine(area, "trackedparent")), $"Case-only rename failed: {Marshal.GetLastPInvokeError()}");
                page = await native.ReadAsync(claim.Cursor, 65536, default);
                Assert.Contains(page.Changes, change => change.FileId == changed.FileId && change.Reason == 0x1000);
                Assert.Contains(page.Changes, change => change.FileId == changed.FileId && change.Reason == 0x2000);
                Assert.Contains(page.Changes, change => change.FileId == changed.FileId && change.Reason == 0x80002000);
            }
            else page = new(claim.Cursor, claim.Cursor with { NextUsn = 200 }, records);
            if (scenario == "expired-guard")
            {
                await Assert.ThrowsAsync<InvalidOperationException>(() => store.AcceptAsync(claim, page, default).AsTask());
                store = new SqlSourceChangeJournalStore(factory, clock, guard);
                var takeover = Assert.IsType<JournalReadClaim>(await store.ClaimVolumeAsync(volume, clock.Now, true, default));
                Assert.Equal(claim.Cursor, takeover.Cursor);
                claim = takeover;
            }
            await store.AcceptAsync(claim, page, default);
            Assert.Equal(scenario is "reparse" or "mixed" or "missing-half" ? 0 : 1, guard.OpenAttempts);
            await using (var db = factory.CreateDbContext())
            {
                var state = await db.SourceRootScanStates.SingleAsync(x => x.SourceRootId == rootId);
                Assert.Equal(page.Next.NextUsn, state.CoveredUsn);
                Assert.Equal(keepsBaseline ? null : clock.Now.AddSeconds(30), state.AncestorReconciliationDueAtUtc);
                Assert.Equal(keepsBaseline, state.NamespaceComplete);
                Assert.Equal(!keepsBaseline, state.RecoveryRequired);
                Assert.Equal(keepsBaseline ? 3 : 4, state.BaselineEpoch);
                Assert.Equal(keepsBaseline ? 7 : 8, state.RecoveryGeneration);
                Assert.Empty(await db.SourceScanRequests.ToArrayAsync());
            }

            var control = new SqlSourceScanStore(factory, clock);
            Assert.Null(await control.ClaimNextReleasedAsync("before-due", clock.Now, TimeSpan.FromMinutes(15), default));
            if (!keepsBaseline)
            {
                clock.Now = clock.Now.AddSeconds(20);
                store = new SqlSourceChangeJournalStore(factory, clock);
                var repeat = Assert.IsType<JournalReadClaim>(await store.ClaimVolumeAsync(volume, clock.Now, true, default));
                await store.AcceptAsync(repeat, new(repeat.Cursor, repeat.Cursor with { NextUsn = 300 }, [Reparse(210)]), default);
                clock.Now = clock.Now.AddSeconds(11);
                var restartedControl = new SqlSourceScanStore(factory, clock);
                var sweep = Assert.IsType<ClaimedSourceScan>(await restartedControl.ClaimNextReleasedAsync("after-restart", clock.Now, TimeSpan.FromMinutes(15), default));
                Assert.Null(await control.ClaimNextReleasedAsync("no-duplicate", clock.Now, TimeSpan.FromMinutes(15), default));
                await using var db = factory.CreateDbContext();
                Assert.Equal(1, (await db.SourceScanRequests.SingleAsync()).RequestKind);
                Assert.Equal(1, await db.SourceScanJobs.CountAsync());
                Assert.Equal(1, await db.SourceScanOutbox.CountAsync());

                if (scenario == "path-changed") Directory.Move(Path.Combine(area, "OtherParent"), ancestorPath);
                using var artifacts = new ContentAddressedSourceArtifactStore(Path.Combine(area, "artifacts"));
                var reader = new AncestorRecoveryReader(volume, journalId, 300);
                var coordinator = new SqlSourceIncrementalCoordinator(factory, clock, reader, new WindowsSourceObservationReader(), artifacts,
                    new() { Enabled = true, ApprovedVolumes = [volume] });
                var plan = Assert.IsType<SourceIncrementalPlan>(await coordinator.PrepareAsync(sweep.SourceRoot, sweep.ScanRequest, default));
                Assert.True(plan.IsBaseline);
                if (scenario == "reparse")
                {
                    var late = Assert.IsType<JournalReadClaim>(await store.ClaimVolumeAsync(volume, clock.Now, true, default));
                    await store.AcceptAsync(late, new(late.Cursor, late.Cursor with { NextUsn = 400 }, [Reparse(310)]), default);
                    await Assert.ThrowsAsync<SourceSnapshotChangedException>(() => coordinator.CompleteAsync(sweep.SourceRoot, sweep.ScanRequest,
                        plan, [], [], new HashSet<SourceRevisionId>(), null, default).AsTask());
                    await restartedControl.CompleteAsync(sweep, new(sweep.SourceRoot.Id, sweep.ScanRequest.Id, 0, 0, 0, 0), "SourceSnapshotChanged", default);
                    Assert.Null(await control.ClaimNextReleasedAsync("before-retry", clock.Now, TimeSpan.FromMinutes(15), default));
                    clock.Now = clock.Now.AddMinutes(1);
                    var retried = Assert.IsType<ClaimedSourceScan>(await control.ClaimNextReleasedAsync("retry", clock.Now, TimeSpan.FromMinutes(15), default));
                    Assert.Equal(sweep.ScanRequest.Id, retried.ScanRequest.Id);
                    reader.NextUsn = 400;
                    sweep = retried;
                    plan = Assert.IsType<SourceIncrementalPlan>(await coordinator.PrepareAsync(sweep.SourceRoot, sweep.ScanRequest, default));
                }
                SourceEnumerationEvidence[] evidence = scenario == "mixed" ? [new("partial", ".", "Fixture unavailable") { NamespaceIncomplete = true }] : [];
                var complete = await coordinator.CompleteAsync(sweep.SourceRoot, sweep.ScanRequest, plan, [], evidence,
                    new HashSet<SourceRevisionId>(), null, default);
                Assert.Equal(evidence.Length == 0, complete);
                await restartedControl.CompleteAsync(sweep, new(sweep.SourceRoot.Id, sweep.ScanRequest.Id, 0, 0, 0, 0)
                    { IsCompleteInventory = complete, CompletesReconciliation = true }, null, default);
                var final = await db.SourceRootScanStates.AsNoTracking().SingleAsync(x => x.SourceRootId == rootId);
                Assert.Equal(!complete, final.RecoveryRequired);
                Assert.Equal(complete ? null : clock.Now.Add(SourcePolicyDefaults.MetadataReconciliationCadence), final.AncestorReconciliationDueAtUtc);
                if (complete)
                {
                    var next = Assert.IsType<JournalReadClaim>(await store.ClaimVolumeAsync(volume, clock.Now, true, default));
                    await store.AcceptAsync(next, new(next.Cursor, next.Cursor with { NextUsn = next.Cursor.NextUsn + 100 },
                        [Reparse(next.Cursor.NextUsn + 10)]), default);
                    var cooled = await db.SourceRootScanStates.AsNoTracking().SingleAsync(x => x.SourceRootId == rootId);
                    Assert.Equal(clock.Now.AddMinutes(15), cooled.AncestorReconciliationDueAtUtc);
                    Assert.Null(await control.ClaimNextReleasedAsync("during-cooldown", clock.Now.AddSeconds(31), TimeSpan.FromMinutes(15), default));
                }
            }
            else
            {
                clock.Now = lastSweep.Add(SourcePolicyDefaults.MetadataReconciliationCadence);
                Assert.NotNull(await control.ClaimNextReleasedAsync("weekly", clock.Now, TimeSpan.FromMinutes(15), default));
                await using var db = factory.CreateDbContext();
                Assert.Equal(1, (await db.SourceScanRequests.SingleAsync()).RequestKind);
            }
        }
        finally
        {
            await using var db = factory.CreateDbContext();
            await db.SourceScanOutbox.Where(x => x.SourceScanRequest.SourceRootId == rootId).ExecuteDeleteAsync();
            await db.SourceScanJobs.Where(x => x.SourceScanRequest.SourceRootId == rootId).ExecuteDeleteAsync();
            await db.SourceScanRequests.Where(x => x.SourceRootId == rootId).ExecuteDeleteAsync();
            await db.SourceRootConfigurations.Where(x => x.Id == rootId).ExecuteDeleteAsync();
            await db.SourceVolumeJournalCheckpoints.Where(x => x.VolumeIdentity == volume && x.JournalId == journalId.ToString("X16")).ExecuteDeleteAsync();
            Directory.Delete(area, true);
        }
    }

    [NativeSqlServerFact]
    public async Task Ancestor_recovery_upgrade_requeues_only_previously_consumed_ancestor_events()
    {
        var factory = new ContextFactory(fixture.ConnectionString);
        var oldSweep = DateTimeOffset.UtcNow.AddDays(-1);
        var ids = new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };
        var reasons = new[] { "ancestor-authority-changed", "journal-continuity-lost", "ancestor-authority-changed" };
        await using var db = factory.CreateDbContext();
        var migrator = db.GetService<IMigrator>();
        for (var i = 0; i < ids.Length; i++)
        {
            db.SourceRootConfigurations.Add(new() { Id = ids[i], CanonicalPath = $@"I:\migration-ancestor-{ids[i]:N}",
                DisplayName = "Migration fixture", ConfigurationRevision = 1, ReconciliationCadenceSeconds = SourcePolicyDefaults.MetadataReconciliationSeconds,
                CreatedAtUtc = oldSweep, UpdatedAtUtc = oldSweep, LastScanCompletedAtUtc = oldSweep });
            db.SourceRootScanStates.Add(new() { SourceRootId = ids[i], Reason = reasons[i], RecoveryRequired = i < 2,
                BaselineEpoch = 3, RecoveryGeneration = 7, CoveredUsn = 784, LastReconciliationAtUtc = oldSweep });
        }
        await db.SaveChangesAsync();
        try
        {
            await migrator.MigrateAsync("20261119201434_AddGpuModelResidencies");
            var migrationAt = DateTimeOffset.UtcNow;
            await migrator.MigrateAsync();
            var states = await db.SourceRootScanStates.AsNoTracking().Where(x => ids.Contains(x.SourceRootId))
                .ToDictionaryAsync(x => x.SourceRootId);
            Assert.InRange(states[ids[0]].AncestorReconciliationDueAtUtc!.Value, migrationAt.AddSeconds(29), DateTimeOffset.UtcNow.AddSeconds(31));
            Assert.Null(states[ids[1]].AncestorReconciliationDueAtUtc);
            Assert.Null(states[ids[2]].AncestorReconciliationDueAtUtc);
            Assert.All(states.Values, state =>
            {
                Assert.Equal(784, state.CoveredUsn);
                Assert.Equal(7, state.RecoveryGeneration);
                Assert.Equal(oldSweep, state.LastReconciliationAtUtc);
            });
        }
        finally
        {
            await db.SourceRootConfigurations.Where(x => ids.Contains(x.Id)).ExecuteDeleteAsync();
        }
    }

    private sealed class TestCoverageGuard(bool loseBeforeCommit, Action? beforeOpen) : ISourceObservationReader
    {
        private readonly WindowsSourceObservationReader _inner = new();
        public int OpenAttempts { get; private set; }
        public ISourceObservationLease Open(SourceRootConfiguration root, string relativePath) => _inner.Open(root, relativePath);
        public IReadOnlyList<SourceNamespaceObservation> ReadRootAncestors(SourceRootConfiguration root) => _inner.ReadRootAncestors(root);
        public ISourceRootObservationLease OpenRoot(SourceRootConfiguration root) => _inner.OpenRoot(root);
        public ISourceAbsenceLease? OpenAbsence(SourceRootConfiguration root, string relativePath) => _inner.OpenAbsence(root, relativePath);
        public ISourceRootObservationLease OpenRootForJournalCoverage(SourceRootConfiguration root)
        {
            OpenAttempts++;
            beforeOpen?.Invoke();
            var lease = _inner.OpenRootForJournalCoverage(root);
            return loseBeforeCommit ? new LostLease(lease) : lease;
        }

        private sealed class LostLease(ISourceRootObservationLease inner) : ISourceRootObservationLease
        {
            public IReadOnlyList<SourceNamespaceObservation> Ancestors => inner.Ancestors;
            public void ValidateCurrent() => throw new SourceSnapshotChangedException("Fixture lost the guarded path before commit.");
            public void Dispose() => inner.Dispose();
        }
    }

    [DllImport("kernel32.dll", EntryPoint = "MoveFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MoveAncestor(string existingName, string newName);

    private sealed class AncestorRecoveryReader(string volume, ulong journalId, long nextUsn) : ISourceChangeJournalReader
    {
        public long NextUsn { get; set; } = nextUsn;
        public ValueTask<JournalCapability> QueryAsync(string volumeIdentity, CancellationToken ct) =>
            ValueTask.FromResult(new JournalCapability(volume, journalId, 0, NextUsn, 0, 1024, 128));
        public ValueTask<JournalPage> ReadAsync(JournalCursor cursor, int maximumBytes, CancellationToken ct) =>
            ValueTask.FromResult(new JournalPage(cursor, cursor, []));
    }
}
