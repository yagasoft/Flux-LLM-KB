using FluxKnowledge.Application.Ports;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using FluxKnowledge.Integration.Tests.Support;
using FluxKnowledge.Integrations.Files;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Sources;

public sealed partial class SourceJournalSealIntegrationTests
{
    [NativeSqlServerTheory]
    [InlineData(false, false, false)]
    [InlineData(true, true, false)]
    [InlineData(false, true, true)]
    public async Task Low_disk_policy_actual_watcher_file_signals_force_capture_without_journal_or_inventory(bool journalEnabled, bool missingInventory, bool incomplete)
    {
        await using var scenario = await Scenario.CreateAsync(fixture.ConnectionString, qualified: false);
        var root = await scenario.ScanAsync("actual-watcher-initial"); await scenario.DrainAsync();
        if (missingInventory)
        {
            await using var db = scenario.Factory.CreateDbContext();
            db.SourceInventoryEntries.Remove(await db.SourceInventoryEntries.SingleAsync(x => !x.IsDirectory));
            await db.SaveChangesAsync();
        }
        scenario.Options.Enabled = journalEnabled; scenario.Restart();
        var signal = Assert.IsType<FluxKnowledge.Domain.Sources.SourceWatchSignal>(LocalSourceRootWatchHostedService.CreateSignal(
            root, FluxKnowledge.Domain.Sources.SourceWatchSignalKind.Changed,
            new FileSystemEventArgs(WatcherChangeTypes.Changed, System.IO.Path.GetDirectoryName(scenario.Path)!, "one.txt"), scenario.Clock.Now));
        Assert.True(Assert.Single(signal.Paths!).IsSubtree); // The actual OS adapter's conservative shape.
        if (incomplete) signal = signal with { PathsIncomplete = true };
        var watch = new SqlSourceRootWatchStore(scenario.Factory, scenario.Clock, scenario.Options);
        await watch.RecordSignalAsync(signal, default);
        scenario.Clock.Now += TimeSpan.FromSeconds(3);
        var batch = Assert.IsType<ClaimedSourceWatchBatch>(await watch.ClaimDueBatchAsync(scenario.Clock.Now, "actual-watcher", TimeSpan.FromMinutes(1), default));
        await watch.ReleaseScanAsync(batch, default);
        await using (var before = scenario.Factory.CreateDbContext())
        {
            Assert.True(await before.SourcePendingChanges.AnyAsync(x => x.RelativePath == "one.txt" && (x.ReasonFlags & SourcePendingReasons.FileEvent) != 0));
            if (incomplete) Assert.True(await before.SourcePendingChanges.AnyAsync(x => x.RelativePath == "." && (x.ReasonFlags & SourcePendingReasons.NamespaceMetadata) != 0));
        }
        scenario.SourceBytes = scenario.ArtifactSourceBytes = scenario.RetainedBytes = 0;
        await scenario.IntakeAsync(); await scenario.DrainAsync();
        Assert.Equal(64, scenario.SourceBytes);
    }

    [NativeSqlServerTheory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Low_disk_policy_adopts_original_ingestion_metadata_when_the_inventory_receipt_is_missing(bool gitTracked, bool removeEntry)
    {
        await using var scenario = await Scenario.CreateAsync(fixture.ConnectionString, qualified: false, gitTracked: gitTracked);
        await scenario.ScanAsync("legacy-metadata-initial"); await scenario.DrainAsync();
        Guid revision; DateTimeOffset checksum;
        await using (var db = scenario.Factory.CreateDbContext())
        {
            revision = (await db.SourceRevisions.SingleAsync()).Id;
            checksum = (await db.SourceArtifacts.SingleAsync()).ChecksumVerifiedAtUtc;
            var entry = await db.SourceInventoryEntries.SingleAsync(x => !x.IsDirectory);
            if (removeEntry) db.SourceInventoryEntries.Remove(entry); else entry.ObservationJson = null;
            await db.SaveChangesAsync();
        }
        scenario.SourceBytes = scenario.ArtifactSourceBytes = scenario.RetainedBytes = 0;
        scenario.Clock.Now += TimeSpan.FromDays(7) + TimeSpan.FromSeconds(1);
        await scenario.IntakeAsync(); await scenario.DrainAsync();
        Assert.Equal(0, scenario.SourceBytes + scenario.ArtifactSourceBytes + scenario.RetainedBytes);
        await using var after = scenario.Factory.CreateDbContext();
        Assert.Equal(revision, (await after.SourceRevisions.SingleAsync()).Id);
        Assert.Equal(checksum, (await after.SourceArtifacts.SingleAsync()).ChecksumVerifiedAtUtc);
        var metadata = System.Text.Json.JsonDocument.Parse((await after.SourceInventoryEntries.SingleAsync(x => !x.IsDirectory)).ObservationJson!);
        Assert.True(metadata.RootElement.GetProperty("MetadataOnly").GetBoolean());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, metadata.RootElement.GetProperty("CaptureGeneration").ValueKind);
        Assert.False(metadata.RootElement.GetProperty("JournalCovered").GetBoolean());
    }

    [NativeSqlServerTheory]
    [InlineData("file-event")]
    [InlineData("unknown-mtime")]
    [InlineData("physical-identity")]
    [InlineData("withdrawn")]
    public async Task Low_disk_policy_missing_inventory_cannot_hide_a_real_event_or_missing_ingestion_authority(string condition)
    {
        await using var scenario = await Scenario.CreateAsync(fixture.ConnectionString, qualified: false);
        await scenario.ScanAsync("legacy-metadata-refusal-initial"); await scenario.DrainAsync();
        await using (var db = scenario.Factory.CreateDbContext())
        {
            db.SourceInventoryEntries.Remove(await db.SourceInventoryEntries.SingleAsync(x => !x.IsDirectory));
            await db.SaveChangesAsync();
            if (condition == "unknown-mtime") await db.SourceRevisions.ExecuteUpdateAsync(x => x.SetProperty(r => r.FileLastWriteAtUtc, (DateTimeOffset?)null));
            if (condition == "physical-identity") await db.SourceRevisions.ExecuteUpdateAsync(x => x.SetProperty(r => r.StableSourceIdentity, "fixture-nonmatching-identity"));
            if (condition == "withdrawn") await db.SourceRevisions.ExecuteUpdateAsync(x => x.SetProperty(r => r.SuppressedAtUtc, scenario.Clock.Now));
        }
        scenario.SourceBytes = scenario.ArtifactSourceBytes = scenario.RetainedBytes = 0;
        if (condition == "file-event") await scenario.SignalAsync();
        else scenario.Clock.Now += TimeSpan.FromDays(7) + TimeSpan.FromSeconds(1);
        await scenario.IntakeAsync(); await scenario.DrainAsync();
        Assert.Equal(64, scenario.SourceBytes);
    }

    [NativeSqlServerFact]
    public async Task Low_disk_policy_watcher_capacity_failure_retains_its_batch_and_a_newer_signal_until_retry()
    {
        await using var scenario = await Scenario.CreateAsync(fixture.ConnectionString, qualified: false);
        await scenario.ScanAsync("watch-capacity-initial"); await scenario.DrainAsync();
        FluxKnowledge.Domain.Sources.SourceRootId rootId;
        await using (var db = scenario.Factory.CreateDbContext())
        {
            rootId = new((await db.SourceRootConfigurations.SingleAsync()).Id);
            db.SourcePendingChanges.AddRange(Enumerable.Range(0, 10000).Select(i => new SourcePendingChangeEntity
            { Id = Guid.NewGuid(), SourceRootId = rootId.Value, RelativePath = $"admitted-{i}.txt",
                PathFingerprint = SqlSourceChangeJournalStore.PathKey($"admitted-{i}.txt"), ReasonFlags = 1, Generation = 1,
                LastContentUsn = 0, LastUnjournaledGeneration = 1, FirstObservedAtUtc = scenario.Clock.Now }));
            await db.SaveChangesAsync();
        }
        var watch = new SqlSourceRootWatchStore(scenario.Factory, scenario.Clock, scenario.Options);
        await watch.RecordSignalAsync(new(rootId, FluxKnowledge.Domain.Sources.SourceWatchSignalKind.Changed,
            scenario.Clock.Now, [new("first-event.txt")]), default);
        scenario.Clock.Now += TimeSpan.FromSeconds(3);
        var batch = Assert.IsType<ClaimedSourceWatchBatch>(await watch.ClaimDueBatchAsync(scenario.Clock.Now, "watch-capacity", TimeSpan.FromMinutes(1), default));
        string before;
        await using (var db = scenario.Factory.CreateDbContext())
            before = System.Text.Json.JsonSerializer.Serialize(await db.SourceRootWatchStates.AsNoTracking().SingleAsync());
        await Assert.ThrowsAsync<SourcePendingCapacityException>(() => watch.ReleaseScanAsync(batch, default).AsTask());
        await using (var db = scenario.Factory.CreateDbContext())
        {
            Assert.Equal(before, System.Text.Json.JsonSerializer.Serialize(await db.SourceRootWatchStates.AsNoTracking().SingleAsync()));
            Assert.Equal(10000, await db.SourcePendingChanges.CountAsync());
        }
        await watch.RecordSignalAsync(new(rootId, FluxKnowledge.Domain.Sources.SourceWatchSignalKind.Changed,
            scenario.Clock.Now, [new("newer-event.txt")]), default);
        await using (var db = scenario.Factory.CreateDbContext())
        {
            // Disposable fixture arrangement: two already-admitted paths drain.
            db.SourcePendingChanges.RemoveRange(await db.SourcePendingChanges.OrderBy(x => x.Id).Take(2).ToArrayAsync());
            await db.SaveChangesAsync();
        }
        await watch.ReleaseScanAsync(batch, default);
        await using var after = scenario.Factory.CreateDbContext();
        Assert.True(await after.SourcePendingChanges.AnyAsync(x => x.RelativePath == "first-event.txt"));
        var retained = await after.SourceRootWatchStates.SingleAsync();
        Assert.True(retained.DebounceGeneration > batch.DebounceGeneration);
        Assert.Contains("newer-event.txt", retained.HintsJson);
        Assert.Null(retained.LeaseOwner);
    }

    [NativeSqlServerFact]
    public async Task Low_disk_policy_weekly_metadata_does_not_reread_unchanged_hard_link_aliases()
    {
        await using var scenario = await Scenario.CreateAsync(fixture.ConnectionString, qualified: false);
        Assert.True(CreateHardLink(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(scenario.Path)!, "alias.txt"), scenario.Path, IntPtr.Zero));
        await scenario.ScanAsync("metadata-hard-links-initial");
        await scenario.DrainAsync();
        scenario.SourceBytes = scenario.ArtifactSourceBytes = scenario.RetainedBytes = 0;
        scenario.Clock.Now += TimeSpan.FromDays(7) + TimeSpan.FromSeconds(1);
        await scenario.IntakeAsync(); await scenario.DrainAsync();
        Assert.Equal(0, scenario.SourceBytes + scenario.ArtifactSourceBytes + scenario.RetainedBytes);
        await using var db = scenario.Factory.CreateDbContext();
        Assert.Equal(2, await db.SourceInventoryEntries.CountAsync(x => !x.IsDirectory));
    }

    [NativeSqlServerFact]
    public async Task Low_disk_policy_disabled_journal_uses_metadata_instead_of_legacy_content_scans()
    {
        await using var scenario = await Scenario.CreateAsync(fixture.ConnectionString, qualified: false);
        scenario.Options.Enabled = false; scenario.Restart();
        await scenario.ScanAsync("disabled-journal-metadata-initial");
        scenario.SourceBytes = scenario.ArtifactSourceBytes = scenario.RetainedBytes = 0;
        scenario.Clock.Now += TimeSpan.FromMinutes(16);
        await scenario.IntakeAsync(); await scenario.DrainAsync();
        scenario.Clock.Now += TimeSpan.FromDays(7);
        await scenario.ScanAsync("disabled-journal-metadata-weekly");
        Assert.Equal(0, scenario.SourceBytes + scenario.ArtifactSourceBytes + scenario.RetainedBytes);
        await using var db = scenario.Factory.CreateDbContext();
        Assert.Equal("Fallback", (await db.SourceRootScanStates.SingleAsync()).Mode);
        Assert.False((await db.SourceRootScanStates.SingleAsync()).RecoveryRequired);
    }

    [NativeSqlServerFact]
    public async Task Low_disk_policy_journal_gap_preserves_real_event_debt_and_waits_for_metadata_without_assurance_reads()
    {
        await using var scenario = await Scenario.CreateAsync(fixture.ConnectionString, qualified: false);
        await scenario.ScanAsync("metadata-gap-initial"); await scenario.DrainAsync();
        scenario.JournalReader = new OneGapJournalReader(); scenario.Restart();
        scenario.SourceBytes = scenario.ArtifactSourceBytes = scenario.RetainedBytes = 0;
        await scenario.IntakeAsync(); await scenario.DrainAsync();
        await using (var before = scenario.Factory.CreateDbContext())
        {
            Assert.True((await before.SourceRootScanStates.SingleAsync()).RecoveryRequired);
            Assert.True(await before.AuditEvents.AnyAsync(x => x.EventType == "source.journal.recovery_required"));
            Assert.False(await before.SourcePendingChanges.AnyAsync(x => (x.ReasonFlags & SourcePendingReasons.FullRecapture) != 0));
        }
        Assert.Equal(0, scenario.SourceBytes + scenario.ArtifactSourceBytes + scenario.RetainedBytes);
        scenario.Clock.Now += TimeSpan.FromDays(7) + TimeSpan.FromSeconds(1);
        await scenario.IntakeAsync(); await scenario.DrainAsync();
        Assert.Equal(0, scenario.SourceBytes + scenario.ArtifactSourceBytes + scenario.RetainedBytes);
        await using var after = scenario.Factory.CreateDbContext();
        Assert.False((await after.SourceRootScanStates.SingleAsync()).RecoveryRequired);
    }

    private sealed class OneGapJournalReader : ISourceChangeJournalReader
    {
        private bool _rejected;
        private readonly WindowsSourceChangeJournalReader _native = new();
        public ValueTask<JournalCapability> QueryAsync(string volume, CancellationToken ct) => _native.QueryAsync(volume, ct);
        public ValueTask<JournalPage> ReadAsync(JournalCursor cursor, int bytes, CancellationToken ct)
        {
            if (!_rejected) { _rejected = true; throw new SourceJournalGapException("fixture-lost-history"); }
            return _native.ReadAsync(cursor, bytes, ct);
        }
    }

    [NativeSqlServerFact]
    public async Task Low_disk_policy_manual_full_content_request_reads_unchanged_files_explicitly()
    {
        await using var scenario = await Scenario.CreateAsync(fixture.ConnectionString, qualified: false);
        await scenario.ScanAsync("manual-full-initial");
        await scenario.DrainAsync();
        await using (var db = scenario.Factory.CreateDbContext())
        {
            var rootId = (await db.SourceRootConfigurations.SingleAsync()).Id;
            var request = Guid.NewGuid();
            db.SourceScanRequests.Add(new() { Id = request, SourceRootId = rootId, RequestKind = 5, RequestedBy = "manual-full-fixture",
                RequestedAtUtc = scenario.Clock.Now, ReleasedAtUtc = scenario.Clock.Now, IsReleased = true, State = 1 });
            db.SourceScanJobs.Add(new() { Id = Guid.NewGuid(), SourceScanRequestId = request, State = 0,
                DueAtUtc = scenario.Clock.Now, CreatedAtUtc = scenario.Clock.Now, UpdatedAtUtc = scenario.Clock.Now });
            await db.SaveChangesAsync();
        }
        scenario.SourceBytes = scenario.ArtifactSourceBytes = scenario.RetainedBytes = 0;
        await scenario.DrainAsync();
        Assert.Equal(64, scenario.SourceBytes);
        await using var after = scenario.Factory.CreateDbContext();
        var manifest = System.Text.Json.JsonSerializer.Deserialize<SourceIncrementalPlan>(
            (await after.SourceScanRequests.SingleAsync(x => x.RequestKind == 5)).IncrementalManifestJson!);
        Assert.True(manifest!.ForceFullContent);
    }

    [NativeSqlServerFact]
    public async Task Low_disk_policy_pending_metadata_recovery_does_not_starve_file_events_or_run_an_early_root_walk()
    {
        await using var scenario = await Scenario.CreateAsync(fixture.ConnectionString, qualified: false);
        await scenario.ScanAsync("event-priority-initial");
        await scenario.DrainAsync();
        await using (var db = scenario.Factory.CreateDbContext())
        {
            var state = await db.SourceRootScanStates.SingleAsync();
            SqlSourceIncrementalState.InvalidateCoverage(state, "journal-continuity-lost", state.CoveredUsn);
            SqlSourcePendingWork.RequireMetadata(db, state, [], scenario.Clock.Now);
            await db.SaveChangesAsync();
        }
        scenario.SourceBytes = scenario.ArtifactSourceBytes = scenario.RetainedBytes = 0;
        await scenario.SignalAsync();
        await scenario.IntakeAsync();
        await scenario.DrainAsync();
        Assert.Equal(64, scenario.SourceBytes);
        await using var after = scenario.Factory.CreateDbContext();
        var latest = await after.SourceScanRequests.OrderByDescending(x => x.RequestedAtUtc).ThenByDescending(x => x.RequestKind).FirstAsync();
        var manifest = System.Text.Json.JsonSerializer.Deserialize<SourceIncrementalPlan>(latest.IncrementalManifestJson!);
        Assert.False(manifest!.IsNamespaceSweep);
        Assert.True((await after.SourceRootScanStates.SingleAsync()).RecoveryRequired);
    }

    [NativeSqlServerFact]
    public async Task Low_disk_policy_refuses_checksum_runs_while_journal_updates_remain_enabled()
    {
        await using var scenario = await Scenario.CreateAsync(fixture.ConnectionString, qualified: false);
        await scenario.ScanAsync("retired-verification-initial");
        await scenario.DrainAsync();
        await using var before = scenario.Factory.CreateDbContext();
        var rootId = new FluxKnowledge.Domain.Sources.SourceRootId((await before.SourceRootConfigurations.SingleAsync()).Id);
        var verifiedAt = (await before.SourceRootScanStates.SingleAsync()).LastVerifiedAtUtc;
        var checksumAt = (await before.SourceArtifacts.SingleAsync()).ChecksumVerifiedAtUtc;
        var verifier = new SqlSourceVerificationStore(scenario.Factory, scenario.Clock, scenario.Options);
        var refusal = await Assert.ThrowsAsync<InvalidOperationException>(() => verifier.StartAsync(rootId, true, default).AsTask());
        Assert.Equal("SourceVerificationPolicyRetired", refusal.Message);
        Assert.True(scenario.Options.Enabled);
        await using var after = scenario.Factory.CreateDbContext();
        Assert.False(await after.SourceVerificationRuns.AnyAsync());
        Assert.Equal(verifiedAt, (await after.SourceRootScanStates.SingleAsync()).LastVerifiedAtUtc);
        Assert.Equal(checksumAt, (await after.SourceArtifacts.SingleAsync()).ChecksumVerifiedAtUtc);
    }

    [NativeSqlServerFact]
    public async Task Low_disk_policy_admitted_capture_finishes_when_intake_capacity_blocks_a_later_barrier()
    {
        await using var scenario = await Scenario.CreateAsync(fixture.ConnectionString, qualified: false);
        await scenario.ScanAsync("capacity-progress-initial");
        await scenario.DrainAsync();
        var journal = new CapacityJournalReader();
        scenario.JournalReader = journal;
        scenario.Restart();
        scenario.Overwrite(0, (byte)'y');
        await scenario.SignalAsync();
        await scenario.IntakeAsync();
        // Explicitly admit an ordinary scan so this case isolates a capacity
        // failure after its file bytes were captured, rather than dispatch timing.
        await using (var admit = scenario.Factory.CreateDbContext())
        {
            var rootId = (await admit.SourceRootConfigurations.SingleAsync()).Id;
            var state = await admit.SourceRootScanStates.SingleAsync();
            var pending = await admit.SourcePendingChanges.SingleOrDefaultAsync(x => x.RelativePath == "one.txt");
            if (pending is null)
            {
                pending = new() { Id = Guid.NewGuid(), SourceRootId = rootId, RelativePath = "one.txt",
                    PathFingerprint = SqlSourceChangeJournalStore.PathKey("one.txt") };
                admit.SourcePendingChanges.Add(pending);
            }
            pending.ReasonFlags |= 1;
            pending.Generation = ++state.Generation;
            pending.LastContentUsn = 0;
            pending.LastUnjournaledGeneration = pending.Generation;
            pending.NotBeforeUtc = scenario.Clock.Now;
            var requestId = Guid.NewGuid();
            admit.SourceScanRequests.Add(new() { Id = requestId, SourceRootId = rootId, RequestKind = 0,
                RequestedBy = "capacity-fixture", RequestedAtUtc = scenario.Clock.Now, ReleasedAtUtc = scenario.Clock.Now,
                IsReleased = true, State = 1 });
            admit.SourceScanJobs.Add(new() { Id = Guid.NewGuid(), SourceScanRequestId = requestId,
                State = 0, DueAtUtc = scenario.Clock.Now, CreatedAtUtc = scenario.Clock.Now, UpdatedAtUtc = scenario.Clock.Now });
            await admit.SaveChangesAsync();
        }
        scenario.SourceBytes = scenario.ArtifactSourceBytes = scenario.RetainedBytes = 0;
        scenario.AfterRead = () => journal.Blocked = true;
        await scenario.ScanAsync("capacity-progress-capture");
        Assert.Equal(64, scenario.SourceBytes);
        await using var db = scenario.Factory.CreateDbContext();
        Assert.False(await db.SourcePendingChanges.AnyAsync(x => x.RelativePath == "one.txt" && (x.ReasonFlags & 1) != 0));
        Assert.Equal(0, await db.SourceScanJobs.CountAsync(x => x.State == 1));
    }

    private sealed class CapacityJournalReader : ISourceChangeJournalReader
    {
        internal bool Blocked;
        private readonly WindowsSourceChangeJournalReader _native = new();
        public ValueTask<JournalCapability> QueryAsync(string volumeIdentity, CancellationToken cancellationToken) =>
            _native.QueryAsync(volumeIdentity, cancellationToken);
        public ValueTask<JournalPage> ReadAsync(JournalCursor cursor, int maximumBytes, CancellationToken cancellationToken) =>
            Blocked ? ValueTask.FromException<JournalPage>(new IOException("SourcePendingCapacityExceeded")) :
                _native.ReadAsync(cursor, maximumBytes, cancellationToken);
    }

    [NativeSqlServerTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Low_disk_policy_reuses_metadata_after_recovery_and_configuration_fences(bool gitTracked)
    {
        await using var scenario = await Scenario.CreateAsync(fixture.ConnectionString, qualified: false, gitTracked: gitTracked);
        await scenario.ScanAsync("metadata-fence-initial");
        await scenario.DrainAsync();
        Guid revision;
        DateTimeOffset checksumTime;
        await using (var setup = scenario.Factory.CreateDbContext())
        {
            var root = await setup.SourceRootConfigurations.SingleAsync();
            var state = await setup.SourceRootScanStates.SingleAsync();
            revision = (await setup.SourceRevisions.SingleAsync()).Id;
            checksumTime = (await setup.SourceArtifacts.SingleAsync()).ChecksumVerifiedAtUtc;
            root.ConfigurationRevision++;
            root.LastScanCompletedAtUtc = scenario.Clock.Now.AddDays(-8);
            state.LastReconciliationAtUtc = root.LastScanCompletedAtUtc;
            SqlSourceIncrementalState.InvalidateCoverage(state, "root-not-enabled", state.CoveredUsn);
            setup.SourcePendingChanges.Add(new SourcePendingChangeEntity
            {
                Id = Guid.NewGuid(), SourceRootId = root.Id, RelativePath = ".",
                PathFingerprint = SqlSourceChangeJournalStore.PathKey("."), Generation = ++state.Generation,
                IsSubtree = true, ReasonFlags = SourcePendingReasons.NamespaceMetadata | SourcePendingReasons.RemovalPending,
                FirstObservedAtUtc = scenario.Clock.Now, NotBeforeUtc = scenario.Clock.Now
            });
            await setup.SaveChangesAsync();
        }
        scenario.SourceBytes = scenario.ArtifactSourceBytes = scenario.RetainedBytes = 0;
        await scenario.IntakeAsync();
        await scenario.DrainAsync();

        Assert.Equal(0, scenario.SourceBytes + scenario.ArtifactSourceBytes + scenario.RetainedBytes);
        await using var after = scenario.Factory.CreateDbContext();
        Assert.Equal(revision, (await after.SourceRevisions.SingleAsync()).Id);
        Assert.Equal(checksumTime, (await after.SourceArtifacts.SingleAsync()).ChecksumVerifiedAtUtc);
        Assert.False((await after.SourceRootScanStates.SingleAsync()).RecoveryRequired);
        if (gitTracked)
            Assert.True(SqlRepositoryWorkRecovery.CanReuseCurrentDiscovery(await after.SourceRootConfigurations.SingleAsync(),
                await after.SourceRevisions.SingleAsync(), await after.SourceArtifacts.SingleAsync()));
    }

    [NativeSqlServerFact]
    public async Task Low_disk_policy_watcher_file_event_forces_capture_despite_matching_metadata_and_a_prior_seal()
    {
        await using var scenario = await Scenario.CreateAsync(fixture.ConnectionString, qualified: true);
        await scenario.ScanAsync("metadata-event-initial");
        await scenario.DrainAsync();
        scenario.SourceBytes = scenario.ArtifactSourceBytes = scenario.RetainedBytes = 0;
        await scenario.SignalAsync();
        await scenario.IntakeAsync();
        await scenario.DrainAsync();
        Assert.Equal(64, scenario.SourceBytes);
    }

    [NativeSqlServerTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Low_disk_policy_does_not_schedule_unsealed_assurance_after_the_former_deadline(bool gitTracked)
    {
        await using var scenario = await Scenario.CreateAsync(fixture.ConnectionString, qualified: false, gitTracked: gitTracked);
        await scenario.ScanAsync("metadata-policy-initial");
        await scenario.DrainAsync();
        scenario.SourceBytes = scenario.ArtifactSourceBytes = scenario.RetainedBytes = 0;
        scenario.Clock.Now += TimeSpan.FromMinutes(16);
        await scenario.IntakeAsync();
        await scenario.DrainAsync();

        Assert.Equal(0, scenario.SourceBytes + scenario.ArtifactSourceBytes + scenario.RetainedBytes);
        await using var db = scenario.Factory.CreateDbContext();
        Assert.Null((await db.SourceInventoryEntries.SingleAsync(x => x.RelativePath == "one.txt")).NextForcedCheckAtUtc);
        Assert.False(await db.SourcePendingChanges.AnyAsync(x => x.RelativePath == "one.txt" && (x.ReasonFlags & 1) != 0));
        Assert.Equal(604800, (await db.SourceRootConfigurations.SingleAsync()).ReconciliationCadenceSeconds);
    }

    [NativeSqlServerTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Low_disk_policy_weekly_metadata_walk_reuses_unchanged_unqualified_content(bool gitTracked)
    {
        await using var scenario = await Scenario.CreateAsync(fixture.ConnectionString, qualified: false, gitTracked: gitTracked);
        await scenario.ScanAsync("metadata-policy-initial");
        await scenario.DrainAsync();
        Guid revision;
        DateTimeOffset checksumTime;
        await using (var before = scenario.Factory.CreateDbContext())
        {
            revision = (await before.SourceRevisions.SingleAsync()).Id;
            checksumTime = (await before.SourceArtifacts.SingleAsync()).ChecksumVerifiedAtUtc;
        }
        scenario.SourceBytes = scenario.ArtifactSourceBytes = scenario.RetainedBytes = 0;
        scenario.Clock.Now += TimeSpan.FromDays(7) + TimeSpan.FromSeconds(1);
        await scenario.IntakeAsync();
        await scenario.DrainAsync();

        Assert.Equal(0, scenario.SourceBytes + scenario.ArtifactSourceBytes + scenario.RetainedBytes);
        await using var after = scenario.Factory.CreateDbContext();
        Assert.Equal(revision, (await after.SourceRevisions.SingleAsync()).Id);
        Assert.Equal(checksumTime, (await after.SourceArtifacts.SingleAsync()).ChecksumVerifiedAtUtc);
        Assert.False((await after.SourceRootScanStates.SingleAsync()).RecoveryRequired);
    }
}
