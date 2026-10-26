using FluxKnowledge.Application.Ports;
using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using FluxKnowledge.Integrations.Files;
using FluxKnowledge.Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Sources;

public sealed class SourceWatcherJournalConvergenceTests(NativeSqlServerFixture fixture) : IClassFixture<NativeSqlServerFixture>
{
    [NativeSqlServerTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Unqualified_entries_merge_both_signal_orders_and_duplicate_hints_without_losing_provenance(bool watcherFirst)
    {
        var factory = new SourceJournalStoreIntegrationTests.ContextFactory(fixture.ConnectionString);
        var clock = new Clock();
        var volume = $@"\\?\Volume{{{Guid.NewGuid()}}}\";
        var rootId = await SeedAsync(factory, clock, volume, unavailable: false);
        var options = new SourceJournalOptions { Enabled = true, ApprovedVolumes = [volume] };
        var watch = new SqlSourceRootWatchStore(factory, clock, options);
        var journal = new SqlSourceChangeJournalStore(factory, clock);
        async Task WatchAsync()
        {
            for (var index = 0; index < 3; index++)
                await watch.RecordSignalAsync(new(new(rootId), SourceWatchSignalKind.Changed, clock.Now, [new("one.txt")]), default);
            clock.Now += TimeSpan.FromSeconds(3);
            var batch = Assert.IsType<ClaimedSourceWatchBatch>(await watch.ClaimDueBatchAsync(clock.Now, "watch", TimeSpan.FromMinutes(1), default));
            await watch.ReleaseScanAsync(batch, default);
        }
        async Task JournalAsync()
        {
            var claim = Assert.IsType<JournalReadClaim>(await journal.ClaimVolumeAsync(volume, clock.Now, true, default));
            var page = new JournalPage(claim.Cursor, claim.Cursor with { NextUsn = 500 },
                [new(200, "000000000000002A", "0000000000000007", "one.txt", 1, false)]);
            await journal.AcceptAsync(claim, page, default);
            await journal.AcceptAsync(claim, page, default);
        }
        try
        {
            if (watcherFirst)
            {
                await WatchAsync();
                await using var beforeJournal = factory.CreateDbContext();
                var forced = Assert.Single(await beforeJournal.SourcePendingChanges.Where(x => x.SourceRootId == rootId).ToArrayAsync());
                Assert.Equal(1, forced.LastUnjournaledGeneration);
                Assert.False(await beforeJournal.SourceScanRequests.AnyAsync(x => x.SourceRootId == rootId));
                // An unqualified entry keeps forced work while a wake races ahead of the record.
                var empty = Assert.IsType<JournalReadClaim>(await journal.ClaimVolumeAsync(volume, clock.Now, true, default));
                await journal.AcceptAsync(empty, new(empty.Cursor, empty.Cursor, []), default);
                await JournalAsync();
            }
            else
            {
                await JournalAsync();
                await WatchAsync();
            }
            await using var verify = factory.CreateDbContext();
            var pending = Assert.Single(await verify.SourcePendingChanges.Where(x => x.SourceRootId == rootId).ToArrayAsync());
            Assert.Equal("one.txt", pending.RelativePath);
            Assert.Equal(2, pending.Generation);
            Assert.Equal(200, pending.LastContentUsn);
            Assert.Equal(watcherFirst ? 1 : 2, pending.LastUnjournaledGeneration);
            Assert.Equal(500, (await verify.SourceVolumeJournalCheckpoints.SingleAsync(x => x.VolumeIdentity == volume)).NextUsn);
            Assert.False(await verify.SourceScanRequests.AnyAsync(x => x.SourceRootId == rootId));
        }
        finally { await CleanupAsync(factory, rootId, volume); }
    }

    [NativeSqlServerFact]
    public async Task Degraded_rename_and_later_edit_survive_claim_restart_and_use_the_shared_pending_pipeline()
    {
        var factory = new SourceJournalStoreIntegrationTests.ContextFactory(fixture.ConnectionString);
        var clock = new Clock();
        var volume = $@"\\?\Volume{{{Guid.NewGuid()}}}\";
        var rootId = await SeedAsync(factory, clock, volume, unavailable: true);
        var options = new SourceJournalOptions { Enabled = true, ApprovedVolumes = [volume] };
        var watch = new SqlSourceRootWatchStore(factory, clock, options);
        try
        {
            await watch.RecordSignalAsync(new(new(rootId), SourceWatchSignalKind.Renamed, clock.Now, [new("old.txt"), new("new.txt")]), default);
            clock.Now += TimeSpan.FromSeconds(3);
            var first = Assert.IsType<ClaimedSourceWatchBatch>(await watch.ClaimDueBatchAsync(clock.Now, "first", TimeSpan.FromMinutes(1), default));
            clock.Now += TimeSpan.FromSeconds(1);
            await watch.RecordSignalAsync(new(new(rootId), SourceWatchSignalKind.Changed, clock.Now, [new("new.txt"), new("other.txt")]), default);
            await watch.ReleaseScanAsync(first, default);
            await using (var afterFirst = factory.CreateDbContext())
            {
                Assert.Equal(2, await afterFirst.SourcePendingChanges.CountAsync(x => x.SourceRootId == rootId));
                Assert.False(await afterFirst.SourceScanRequests.AnyAsync(x => x.SourceRootId == rootId));
            }
            // New coordinator, using only persisted hints; no in-memory hint dictionary survives.
            var restarted = new SqlSourceRootWatchStore(factory, clock, options);
            clock.Now += TimeSpan.FromSeconds(3);
            var second = Assert.IsType<ClaimedSourceWatchBatch>(await restarted.ClaimDueBatchAsync(clock.Now, "restarted", TimeSpan.FromMinutes(1), default));
            await restarted.ReleaseScanAsync(second, default);
            await using var verify = factory.CreateDbContext();
            var pending = await verify.SourcePendingChanges.Where(x => x.SourceRootId == rootId).OrderBy(x => x.RelativePath).ToArrayAsync();
            Assert.Equal(new[] { "new.txt", "old.txt", "other.txt" }, pending.Select(x => x.RelativePath));
            Assert.All(pending, item => Assert.NotEqual(0, item.ReasonFlags & 1)); // Force capture even when metadata matches.
            Assert.True(pending.Single(x => x.RelativePath == "new.txt").Generation > 1);
            Assert.Equal(100, (await verify.SourceVolumeJournalCheckpoints.SingleAsync(x => x.VolumeIdentity == volume)).NextUsn);
            Assert.False(await verify.SourceScanRequests.AnyAsync(x => x.SourceRootId == rootId));
        }
        finally { await CleanupAsync(factory, rootId, volume); }
    }

    private static async Task<Guid> SeedAsync(SourceJournalStoreIntegrationTests.ContextFactory factory, Clock clock, string volume, bool unavailable)
    {
        var id = Guid.NewGuid();
        await using var db = factory.CreateDbContext();
        var root = new SourceRootConfigurationEntity { Id = id, CanonicalPath = $@"I:\synthetic-journal-watch-{id:N}", DisplayName = "Watcher convergence fixture",
            Recursive = true, MaximumFileBytes = 1024, IncludePatternsJson = "[]", ExcludePatternsJson = "[]", AllowedClassificationsJson = "[]",
            ReconciliationCadenceSeconds = 900, ConfigurationRevision = 1, CreatedAtUtc = clock.Now, UpdatedAtUtc = clock.Now };
        db.SourceRootConfigurations.Add(root);
        db.SourceRootScanStates.Add(new() { SourceRootId = id, VolumeIdentity = volume, PolicyFingerprint = SqlRepositoryWorkRecovery.Policy(root),
            BaselineEpoch = 1, Mode = "Journal", CoveredUsn = 100 });
        db.SourceVolumeJournalCheckpoints.Add(new() { VolumeIdentity = volume, JournalId = "0000000000000001", NextUsn = 100,
            UnavailableSinceUtc = unavailable ? clock.Now : null });
        db.SourceInventoryEntries.Add(new() { Id = Guid.NewGuid(), SourceRootId = id, RelativePath = ".", PathFingerprint = "root", FileId = "0000000000000007",
            IsDirectory = true, BaselineEpoch = 1 });
        await db.SaveChangesAsync();
        return id;
    }

    private static async Task CleanupAsync(SourceJournalStoreIntegrationTests.ContextFactory factory, Guid rootId, string volume)
    {
        await using var db = factory.CreateDbContext();
        await db.AuditEvents.Where(x => x.SourceRootId == rootId).ExecuteDeleteAsync();
        await db.SourceScanOutbox.Where(x => x.SourceScanRequest.SourceRootId == rootId).ExecuteDeleteAsync();
        await db.SourceScanJobs.Where(x => x.SourceScanRequest.SourceRootId == rootId).ExecuteDeleteAsync();
        await db.SourceScanRequests.Where(x => x.SourceRootId == rootId).ExecuteDeleteAsync();
        await db.SourceRootWatchStates.Where(x => x.SourceRootId == rootId).ExecuteDeleteAsync();
        await db.SourceRootConfigurations.Where(x => x.Id == rootId).ExecuteDeleteAsync();
        await db.SourceVolumeJournalCheckpoints.Where(x => x.VolumeIdentity == volume).ExecuteDeleteAsync();
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
