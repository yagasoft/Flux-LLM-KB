using System.ComponentModel;
using System.Text.Json;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Sources;
using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using FluxKnowledge.Integrations.Files;
using FluxKnowledge.Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Sources;

public sealed partial class SourceJournalRecoveryIntegrationTests
{
    [NativeSqlServerTheory]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    [InlineData(false, true, false)]
    [InlineData(false, false, false)]
    public async Task Newer_degraded_watcher_generations_cannot_immediately_repeat_a_completed_whole_root_scan(bool recoveryRequired, bool fullRecapture, bool deferred)
    {
        var area = NewArea();
        Directory.CreateDirectory(area);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(area, "one.txt"), "retained fixture");
            var factory = new SourceJournalStoreIntegrationTests.ContextFactory(fixture.ConnectionString);
            var clock = new LoopClock(DateTimeOffset.UtcNow);
            var control = new SqlSourceScanStore(factory, clock);
            var claim = await RegisterAndClaimAsync(factory, control, area);
            var volume = WindowsSourceChangeJournalReader.CanonicalVolume(area);
            var options = new SourceJournalOptions { Enabled = true, ApprovedVolumes = [volume] };
            using var artifacts = new ContentAddressedSourceArtifactStore(Path.Combine(area, "Retained"));
            var coordinator = new SqlSourceIncrementalCoordinator(factory, clock, new GapReader(volume) { Unavailable = true },
                new WindowsSourceObservationReader(), artifacts, options);
            await using (var db = factory.CreateDbContext())
            {
                var root = await db.SourceRootConfigurations.SingleAsync();
                db.SourceRootScanStates.Add(new() { SourceRootId = root.Id, VolumeIdentity = volume, BaselineEpoch = 1,
                    RecoveryGeneration = 1, Generation = 1, RecoveryRequired = recoveryRequired, Mode = "Fallback", PolicyFingerprint = SqlRepositoryWorkRecovery.Policy(root) });
                db.SourcePendingChanges.Add(new() { Id = Guid.NewGuid(), SourceRootId = root.Id, RelativePath = ".",
                    PathFingerprint = SqlSourceChangeJournalStore.PathKey("."), Generation = 1, IsSubtree = true,
                    ReasonFlags = (fullRecapture ? SourcePendingReasons.FullRecapture : 0) | SourcePendingReasons.NamespaceMetadata | SourcePendingReasons.RemovalPending | 1,
                    LastContentUsn = 0, LastUnjournaledGeneration = 1, NotBeforeUtc = clock.Now });
                await db.SaveChangesAsync();
            }
            var plan = Assert.IsType<SourceIncrementalPlan>(await coordinator.PrepareAsync(claim.SourceRoot, claim.ScanRequest, default));
            var watcher = new SqlSourceRootWatchStore(factory, clock, options);
            async Task HintAsync()
            {
                await watcher.RecordSignalAsync(new(claim.SourceRoot.Id, SourceWatchSignalKind.Changed, clock.Now, [new(".", true)]), default);
                clock.Now += TimeSpan.FromSeconds(3);
                var batch = Assert.IsType<ClaimedSourceWatchBatch>(await watcher.ClaimDueBatchAsync(clock.Now, "late-watcher", TimeSpan.FromMinutes(1), default));
                await watcher.ReleaseScanAsync(batch, default);
            }
            await HintAsync(); // Later generation than the frozen manifest; it must survive completion.
            var evidence = deferred ? new[] { new SourceEnumerationEvidence("unavailable", "one.txt", "fixture-deferred-content") } : [];
            Assert.False(await coordinator.CompleteAsync(claim.SourceRoot, claim.ScanRequest, plan, [], evidence, new HashSet<SourceRevisionId>(), null, default));
            await control.CompleteAsync(claim, new(claim.SourceRoot.Id, claim.ScanRequest.Id, 0, 0, 0, 0)
                { IsCompleteInventory = false, CompletesReconciliation = true }, null, default);
            var completedAt = clock.Now;
            for (var cycle = 0; cycle < 3; cycle++)
            {
                await HintAsync();
                await using var before = factory.CreateDbContext();
                var pendingBefore = JsonSerializer.Serialize(await before.SourcePendingChanges.SingleAsync());
                Assert.Equal(0, await coordinator.PumpAsync(default));
                Assert.Null(await control.ClaimNextReleasedAsync("must-stay-idle", clock.Now, TimeSpan.FromMinutes(10), default));
                await using var after = factory.CreateDbContext();
                Assert.Equal(pendingBefore, JsonSerializer.Serialize(await after.SourcePendingChanges.SingleAsync()));
                Assert.Equal(recoveryRequired, (await after.SourceRootScanStates.SingleAsync()).RecoveryRequired);
                Assert.True((await after.SourcePendingChanges.SingleAsync()).Generation > plan.Generation);
            }
            clock.Now = completedAt + claim.SourceRoot.ReconciliationCadence;
            Assert.Equal(1, await coordinator.PumpAsync(default));
            Assert.NotNull(await control.ClaimNextReleasedAsync("paced-followup", clock.Now, TimeSpan.FromMinutes(10), default));
        }
        finally { Directory.Delete(area, true); }
    }

    [NativeSqlServerTheory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Rejected_cursor_recovery_preserves_a_paused_participant_and_resets_only_an_enabled_volume(int enabledRoots)
    {
        var factory = new SourceJournalStoreIntegrationTests.ContextFactory(fixture.ConnectionString);
        var volume = $@"\\?\Volume{{{Guid.NewGuid()}}}\";
        var clock = new LoopClock(DateTimeOffset.UtcNow);
        var ids = new[] { Guid.NewGuid(), Guid.NewGuid() };
        await using (var db = factory.CreateDbContext())
        {
            for (var i = 0; i < ids.Length; i++)
            {
                db.SourceRootConfigurations.Add(new() { Id = ids[i], CanonicalPath = $@"I:\synthetic-cursor-{ids[i]:N}", DisplayName = "Cursor recovery fixture",
                    State = i < enabledRoots ? (int)SourceRootState.Enabled : (int)SourceRootState.Paused, ConfigurationRevision = 1,
                    Recursive = true, MaximumFileBytes = 1024, ReconciliationCadenceSeconds = 900, CreatedAtUtc = clock.Now, UpdatedAtUtc = clock.Now });
                db.SourceRootScanStates.Add(new() { SourceRootId = ids[i], VolumeIdentity = volume, BaselineEpoch = 3,
                    RecoveryGeneration = 7, Generation = 11, Mode = "Fallback", CoveredUsn = 100, NamespaceGeneration = 9 });
                db.SourcePendingChanges.Add(new() { Id = Guid.NewGuid(), SourceRootId = ids[i], RelativePath = "later.txt",
                    PathFingerprint = SqlSourceChangeJournalStore.PathKey("later.txt"), ReasonFlags = 1, Generation = 11,
                    RetryGeneration = 11, NotBeforeUtc = clock.Now.AddMinutes(5), FirstObservedAtUtc = clock.Now.AddMinutes(-1),
                    LastContentUsn = 100, LastUnjournaledGeneration = 11 });
            }
            db.SourceVolumeJournalCheckpoints.Add(new() { VolumeIdentity = volume, JournalId = "0000000000000001", NextUsn = 100,
                RegistrationGeneration = 2, UnavailableSinceUtc = clock.Now.AddDays(-2), LastFailureCode = "journal-unavailable" });
            await db.SaveChangesAsync();
        }
        string beforeStates, beforePending;
        await using (var before = factory.CreateDbContext())
        {
            beforeStates = JsonSerializer.Serialize(await before.SourceRootScanStates.OrderBy(x => x.SourceRootId).ToArrayAsync());
            beforePending = JsonSerializer.Serialize(await before.SourcePendingChanges.OrderBy(x => x.SourceRootId).ToArrayAsync());
        }
        using var artifacts = new ContentAddressedSourceArtifactStore(NewArea());
        var reader = new RejectedCursorReader(volume);
        var coordinator = new SqlSourceIncrementalCoordinator(factory, clock, reader, new WindowsSourceObservationReader(), artifacts,
            new() { Enabled = true, ApprovedVolumes = [volume] });
        await coordinator.PumpAsync(default);
        if (enabledRoots == 1)
        {
            var beforeReads = reader.ReadCalls;
            var started = System.Diagnostics.Stopwatch.StartNew();
            var barrier = await reader.QueryAsync(volume, default);
            var error = await Assert.ThrowsAsync<IOException>(() => coordinator.CatchUpStartupAsync(barrier, default));
            Assert.Equal("SourceJournalUnavailable", error.Message);
            Assert.Equal(beforeReads + 1, reader.ReadCalls);
            Assert.True(started.Elapsed < TimeSpan.FromSeconds(5), "A paused participant must yield to fallback, not spin at a foreground barrier.");
        }
        await using var verify = factory.CreateDbContext();
        var checkpoint = await verify.SourceVolumeJournalCheckpoints.SingleAsync();
        var recoveryEvents = await verify.AuditEvents.Where(x => x.EventType == "source.journal.recovery_required").ToArrayAsync();
        if (enabledRoots != 2)
        {
            Assert.Equal(100, checkpoint.NextUsn);
            Assert.Equal("0000000000000001", checkpoint.JournalId);
            Assert.Equal("journal-recovery-paused", checkpoint.LastFailureCode);
            Assert.Equal(beforeStates, JsonSerializer.Serialize(await verify.SourceRootScanStates.OrderBy(x => x.SourceRootId).ToArrayAsync()));
            Assert.Equal(beforePending, JsonSerializer.Serialize(await verify.SourcePendingChanges.OrderBy(x => x.SourceRootId).ToArrayAsync()));
            Assert.Empty(recoveryEvents);
            Assert.Empty(await verify.SourceScanRequests.ToArrayAsync());
        }
        else
        {
            Assert.Equal(1000, checkpoint.NextUsn);
            Assert.All(await verify.SourceRootScanStates.ToArrayAsync(), state => Assert.True(state.RecoveryRequired));
            var receipt = Assert.Single(recoveryEvents);
            Assert.Contains("SourceJournalCursorRejected", receipt.DetailsJson);
            Assert.Contains("1000", receipt.DetailsJson);
            Assert.All(ids, id => Assert.Contains(id.ToString(), receipt.DetailsJson, StringComparison.OrdinalIgnoreCase));
            Assert.Equal(2, await verify.SourcePendingChanges.CountAsync());
        }
    }

    private sealed class LoopClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    [NativeSqlServerTheory]
    [InlineData("Fallback", false)]
    [InlineData("Fallback", true)]
    [InlineData("Journal", false)]
    [InlineData("BaselineRequired", false)]
    public async Task Automatic_whole_root_work_waits_for_its_schedule_while_journal_retries_continue(string mode, bool unavailable)
    {
        var factory = new SourceJournalStoreIntegrationTests.ContextFactory(fixture.ConnectionString);
        var volume = $@"\\?\Volume{{{Guid.NewGuid()}}}\";
        var id = Guid.NewGuid();
        var clock = new LoopClock(DateTimeOffset.UtcNow);
        await using (var db = factory.CreateDbContext())
        {
            db.SourceRootConfigurations.Add(new() { Id = id, CanonicalPath = $@"I:\availability-fixture-{id:N}", DisplayName = "Availability fixture",
                ConfigurationRevision = 1, ReconciliationCadenceSeconds = 900, CreatedAtUtc = clock.Now, UpdatedAtUtc = clock.Now,
                LastScanCompletedAtUtc = clock.Now });
            db.SourceRootScanStates.Add(new() { SourceRootId = id, VolumeIdentity = volume, Mode = mode, RecoveryRequired = true,
                LastReconciliationAtUtc = clock.Now, BaselineEpoch = 3, CoveredUsn = 100 });
            db.SourceVolumeJournalCheckpoints.Add(new() { VolumeIdentity = volume, JournalId = "0000000000000001", NextUsn = 100,
                UnavailableSinceUtc = clock.Now.AddMinutes(-1), LastFailureCode = "journal-unavailable" });
            db.SourcePendingChanges.Add(new() { Id = Guid.NewGuid(), SourceRootId = id, RelativePath = ".", IsSubtree = true,
                PathFingerprint = SqlSourceChangeJournalStore.PathKey("."), Generation = 5, ReasonFlags = SourcePendingReasons.FullRecapture | 1,
                LastContentUsn = 0, LastUnjournaledGeneration = 5, NotBeforeUtc = clock.Now });
            await db.SaveChangesAsync();
        }
        using var artifacts = new ContentAddressedSourceArtifactStore(NewArea());
        var reader = new GapReader(volume) { Unavailable = unavailable };
        var coordinator = new SqlSourceIncrementalCoordinator(factory, clock, reader,
            new WindowsSourceObservationReader(), artifacts, new() { Enabled = true, ApprovedVolumes = [volume] });
        for (var attempt = 0; attempt < 3; attempt++)
        {
            Assert.Equal(0, await coordinator.PumpAsync(default));
            await using var verify = factory.CreateDbContext();
            Assert.Equal(mode, (await verify.SourceRootScanStates.SingleAsync()).Mode);
            Assert.Equal(unavailable, (await verify.SourceVolumeJournalCheckpoints.SingleAsync()).UnavailableSinceUtc is not null);
            Assert.Empty(await verify.SourceScanRequests.ToArrayAsync());
            Assert.Equal(5, (await verify.SourcePendingChanges.SingleAsync()).Generation);
            clock.Now += TimeSpan.FromSeconds(10);
        }
        Assert.Equal(3, reader.ReadCalls);
        clock.Now += TimeSpan.FromMinutes(15);
        Assert.Equal(1, await coordinator.PumpAsync(default));
    }

    [NativeSqlServerTheory]
    [InlineData("owner")]
    [InlineData("generation")]
    [InlineData("expiry")]
    public async Task A_stale_journal_claim_cannot_advance_a_rejected_cursor_or_emit_a_recovery_receipt(string change)
    {
        var factory = new SourceJournalStoreIntegrationTests.ContextFactory(fixture.ConnectionString);
        var volume = $@"\\?\Volume{{{Guid.NewGuid()}}}\";
        var clock = new LoopClock(DateTimeOffset.UtcNow);
        var id = Guid.NewGuid();
        await using (var db = factory.CreateDbContext())
        {
            db.SourceRootConfigurations.Add(new() { Id = id, CanonicalPath = $@"I:\stale-cursor-{id:N}", DisplayName = "Stale cursor fixture",
                ConfigurationRevision = 1, ReconciliationCadenceSeconds = 900, CreatedAtUtc = clock.Now, UpdatedAtUtc = clock.Now });
            db.SourceRootScanStates.Add(new() { SourceRootId = id, VolumeIdentity = volume, Mode = "Fallback", BaselineEpoch = 3, CoveredUsn = 100 });
            db.SourceVolumeJournalCheckpoints.Add(new() { VolumeIdentity = volume, JournalId = "0000000000000001", NextUsn = 100 });
            await db.SaveChangesAsync();
        }
        var reader = new RejectedCursorReader(volume)
        {
            OnQuery = async () =>
            {
                await using var db = factory.CreateDbContext();
                var point = await db.SourceVolumeJournalCheckpoints.SingleAsync();
                if (change == "owner") point.LeaseOwner = "different-owner";
                if (change == "generation") point.LeaseGeneration++;
                if (change == "expiry") point.LeaseExpiresAtUtc = clock.Now.AddSeconds(-1);
                await db.SaveChangesAsync();
            }
        };
        using var artifacts = new ContentAddressedSourceArtifactStore(NewArea());
        await new SqlSourceIncrementalCoordinator(factory, clock, reader, new WindowsSourceObservationReader(), artifacts,
            new() { Enabled = true, ApprovedVolumes = [volume] }).PumpAsync(default);
        await using var verify = factory.CreateDbContext();
        Assert.Equal(100, (await verify.SourceVolumeJournalCheckpoints.SingleAsync()).NextUsn);
        Assert.Equal(100, (await verify.SourceRootScanStates.SingleAsync()).CoveredUsn);
        Assert.Equal(3, (await verify.SourceRootScanStates.SingleAsync()).BaselineEpoch);
        Assert.False(await verify.AuditEvents.AnyAsync(x => x.EventType == "source.journal.recovery_required"));
        Assert.Empty(await verify.SourcePendingChanges.ToArrayAsync());
    }

    private sealed class RejectedCursorReader(string volume) : ISourceChangeJournalReader
    {
        public int ReadCalls { get; private set; }
        public Func<Task>? OnQuery { get; init; }
        public async ValueTask<JournalCapability> QueryAsync(string volumeIdentity, CancellationToken ct)
        {
            if (OnQuery is not null) await OnQuery();
            return new(volume, 1, 0, 1000, 0, 1024, 128);
        }
        public ValueTask<JournalPage> ReadAsync(JournalCursor cursor, int maximumBytes, CancellationToken ct)
        {
            ReadCalls++;
            return cursor.NextUsn == 100
                ? throw new SourceJournalGapException("SourceJournalCursorRejected", new Win32Exception(87))
                : ValueTask.FromResult(new JournalPage(cursor, cursor, []));
        }
    }
}
