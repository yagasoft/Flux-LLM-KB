using FluxKnowledge.Application.Ports;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using FluxKnowledge.Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Sources;

public sealed partial class SourceJournalStoreIntegrationTests
{
    [NativeSqlServerFact]
    public async Task Journal_accepts_alias_fanout_above_ten_thousand_without_losing_existing_work()
    {
        await using var area = await PendingCapacity.CreateAsync(fixture.ConnectionString);
        await using (var db = area.Factory.CreateDbContext())
        {
            // Ten changed physical identities, each with 1,001 known aliases: a
            // small native page can fan out beyond the former per-root limit.
            db.SourceInventoryEntries.AddRange(Enumerable.Range(0, 10).SelectMany(file => Enumerable.Range(0, 1001).Select(link =>
                new SourceInventoryEntryEntity { Id = Guid.NewGuid(), SourceRootId = area.RootId,
                    RelativePath = $"alias-{file}-{link}.txt", PathFingerprint = SqlSourceChangeJournalStore.PathKey($"alias-{file}-{link}.txt"),
                    FileId = (1000 + file).ToString("X16"), ParentId = "0000000000000007", BaselineEpoch = 1 })));
            await db.SaveChangesAsync();
        }
        var store = new SqlSourceChangeJournalStore(area.Factory, area.Clock);
        var claim = Assert.IsType<JournalReadClaim>(await store.ClaimVolumeAsync(area.Volume, area.Clock.Now, true, default));
        await store.AcceptAsync(claim,
            new(claim.Cursor, claim.Cursor with { NextUsn = 200 }, Enumerable.Range(0, 10).Select(file =>
                new JournalChange(110 + file, (1000 + file).ToString("X16"), "0000000000000007", $"alias-{file}-0.txt", 1, false)).ToArray()), default);
        await using var after = area.Factory.CreateDbContext();
        Assert.Equal(20010, await after.SourcePendingChanges.CountAsync(x => x.SourceRootId == area.RootId));
        Assert.Equal(10000, await after.SourcePendingChanges.CountAsync(x => x.SourceRootId == area.RootId && x.RelativePath.StartsWith("existing-")));
        Assert.Equal(10010, await after.SourceInventoryEntries.CountAsync(x => x.SourceRootId == area.RootId && x.LatestJournalUsn != null));
        Assert.Equal(200, (await after.SourceVolumeJournalCheckpoints.SingleAsync(x => x.VolumeIdentity == area.Volume)).NextUsn);
    }

    [NativeSqlServerFact]
    public async Task Journal_commits_all_roots_and_the_shared_cursor_when_one_backlog_exceeds_ten_thousand()
    {
        await using var area = await PendingCapacity.CreateAsync(fixture.ConnectionString);
        var other = Guid.NewGuid();
        try
        {
            Guid full, empty;
            await using (var db = area.Factory.CreateDbContext())
            {
                db.SourceRootConfigurations.Add(new() { Id = other, CanonicalPath = $@"I:\pending-capacity-{other:N}",
                    DisplayName = "Shared pending capacity fixture", Recursive = true, MaximumFileBytes = 1024,
                    ReconciliationCadenceSeconds = 604800, ConfigurationRevision = 1, CreatedAtUtc = area.Clock.Now, UpdatedAtUtc = area.Clock.Now });
                db.SourceRootScanStates.Add(new() { SourceRootId = other, VolumeIdentity = area.Volume, BaselineEpoch = 1,
                    RecoveryGeneration = 1, Generation = 1, Mode = "Journal", NamespaceComplete = true });
                db.SourceInventoryEntries.Add(new() { Id = Guid.NewGuid(), SourceRootId = other, RelativePath = ".",
                    PathFingerprint = SqlSourceChangeJournalStore.PathKey("."), FileId = "0000000000000007", IsDirectory = true, BaselineEpoch = 1 });
                await db.SaveChangesAsync();
                var ordered = await db.SourceRootConfigurations.Where(x => x.Id == area.RootId || x.Id == other)
                    .OrderBy(x => x.Id).Select(x => x.Id).ToArrayAsync();
                empty = ordered[0]; full = ordered[1];
                if (full != area.RootId)
                    await db.SourcePendingChanges.Where(x => x.SourceRootId == area.RootId).ExecuteUpdateAsync(x => x.SetProperty(p => p.SourceRootId, full));
            }
            var store = new SqlSourceChangeJournalStore(area.Factory, area.Clock);
            var claim = Assert.IsType<JournalReadClaim>(await store.ClaimVolumeAsync(area.Volume, area.Clock.Now, true, default));
            await store.AcceptAsync(claim,
                new(claim.Cursor, claim.Cursor with { NextUsn = 200 }, [new(150, "000000000000002A", "0000000000000007", "new.txt", 1, false)]), default);
            await using var after = area.Factory.CreateDbContext();
            Assert.Equal(10001, await after.SourcePendingChanges.CountAsync(x => x.SourceRootId == full));
            Assert.True(await after.SourcePendingChanges.AnyAsync(x => x.SourceRootId == empty && x.RelativePath == "new.txt"));
            Assert.All(await after.SourceRootScanStates.Where(x => x.SourceRootId == full || x.SourceRootId == empty).ToArrayAsync(),
                state => Assert.Equal(200, state.CoveredUsn));
            Assert.Equal(200, (await after.SourceVolumeJournalCheckpoints.SingleAsync(x => x.VolumeIdentity == area.Volume)).NextUsn);
        }
        finally
        {
            await using var db = area.Factory.CreateDbContext();
            await db.SourcePendingChanges.Where(x => x.SourceRootId == other).ExecuteDeleteAsync();
            await db.SourceInventoryEntries.Where(x => x.SourceRootId == other).ExecuteDeleteAsync();
            await db.SourceRootScanStates.Where(x => x.SourceRootId == other).ExecuteDeleteAsync();
            await db.SourceRootConfigurations.Where(x => x.Id == other).ExecuteDeleteAsync();
        }
    }

    [NativeSqlServerFact]
    public async Task Journal_admits_new_paths_above_ten_thousand_and_replays_without_duplicates()
    {
        await using var area = await PendingCapacity.CreateAsync(fixture.ConnectionString);
        var store = new SqlSourceChangeJournalStore(area.Factory, area.Clock);
        var claim = Assert.IsType<JournalReadClaim>(await store.ClaimVolumeAsync(area.Volume, area.Clock.Now, true, default));
        var page = new JournalPage(claim.Cursor, claim.Cursor with { NextUsn = 200 },
            [new(150, "000000000000002A", "0000000000000007", "new.txt", 1, false)]);
        await store.AcceptAsync(claim, page, default);
        await store.AcceptAsync(claim, page, default);
        await using var recovered = area.Factory.CreateDbContext();
        Assert.Equal(10001, await recovered.SourcePendingChanges.CountAsync(x => x.SourceRootId == area.RootId));
        Assert.Equal(2, (await recovered.SourceRootScanStates.SingleAsync(x => x.SourceRootId == area.RootId)).Generation);
        Assert.True(await recovered.SourcePendingChanges.AnyAsync(x => x.SourceRootId == area.RootId && x.RelativePath == "new.txt"));
        Assert.Equal(200, (await recovered.SourceVolumeJournalCheckpoints.SingleAsync(x => x.VolumeIdentity == area.Volume)).NextUsn);
    }

    [NativeSqlServerFact]
    public async Task Capacity_allows_existing_path_updates_without_coalescing_known_changes()
    {
        await using var area = await PendingCapacity.CreateAsync(fixture.ConnectionString);
        var store = new SqlSourceChangeJournalStore(area.Factory, area.Clock);
        var claim = Assert.IsType<JournalReadClaim>(await store.ClaimVolumeAsync(area.Volume, area.Clock.Now, true, default));
        await store.AcceptAsync(claim, new(claim.Cursor, claim.Cursor with { NextUsn = 200 },
            [new(150, "000000000000002A", "0000000000000007", "existing-0.txt", 1, false)]), default);
        await using var db = area.Factory.CreateDbContext();
        Assert.Equal(10000, await db.SourcePendingChanges.CountAsync(x => x.SourceRootId == area.RootId));
        Assert.False(await db.SourcePendingChanges.AnyAsync(x => x.SourceRootId == area.RootId && x.RelativePath == "."));
        Assert.Equal(150, (await db.SourcePendingChanges.SingleAsync(x => x.SourceRootId == area.RootId && x.RelativePath == "existing-0.txt")).LastContentUsn);
    }

    [NativeSqlServerFact]
    public async Task Metadata_recovery_is_recorded_above_ten_thousand_without_replacing_specific_paths()
    {
        await using var area = await PendingCapacity.CreateAsync(fixture.ConnectionString);
        await using (var db = area.Factory.CreateDbContext())
        {
            var state = await db.SourceRootScanStates.SingleAsync(x => x.SourceRootId == area.RootId);
            var pending = await db.SourcePendingChanges.Where(x => x.SourceRootId == area.RootId).ToListAsync();
            SqlSourcePendingWork.RequireMetadata(db, state, pending, area.Clock.Now);
            await db.SaveChangesAsync();
        }
        await using var after = area.Factory.CreateDbContext();
        Assert.Equal(10001, await after.SourcePendingChanges.CountAsync(x => x.SourceRootId == area.RootId));
        Assert.Equal(10000, await after.SourcePendingChanges.CountAsync(x => x.SourceRootId == area.RootId && x.ReasonFlags == 1 && x.Generation == 1));
        var metadata = await after.SourcePendingChanges.SingleAsync(x => x.SourceRootId == area.RootId && x.RelativePath == ".");
        Assert.Equal(SourcePendingReasons.NamespaceMetadata | SourcePendingReasons.RemovalPending, metadata.ReasonFlags);
        Assert.False((await after.SourceRootScanStates.SingleAsync(x => x.SourceRootId == area.RootId)).NamespaceComplete);
    }

    private sealed class PendingCapacity : IAsyncDisposable
    {
        internal Guid RootId { get; } = Guid.NewGuid();
        internal string Volume { get; } = $@"\\?\Volume{{{Guid.NewGuid()}}}\";
        internal Clock Clock { get; } = new(DateTimeOffset.UtcNow);
        internal ContextFactory Factory { get; }
        private PendingCapacity(string connection) => Factory = new(connection);
        internal static async Task<PendingCapacity> CreateAsync(string connection)
        {
            var area = new PendingCapacity(connection);
            await using var db = area.Factory.CreateDbContext();
            db.SourceRootConfigurations.Add(new() { Id = area.RootId, CanonicalPath = $@"I:\pending-capacity-{area.RootId:N}",
                DisplayName = "Pending capacity fixture", Recursive = true, MaximumFileBytes = 1024,
                ReconciliationCadenceSeconds = 604800, ConfigurationRevision = 1, CreatedAtUtc = area.Clock.Now, UpdatedAtUtc = area.Clock.Now });
            db.SourceRootScanStates.Add(new() { SourceRootId = area.RootId, VolumeIdentity = area.Volume, BaselineEpoch = 1,
                RecoveryGeneration = 1, Generation = 1, Mode = "Journal", NamespaceComplete = true });
            db.SourceVolumeJournalCheckpoints.Add(new() { VolumeIdentity = area.Volume, JournalId = "0000000000000001", NextUsn = 100 });
            db.SourceInventoryEntries.Add(new() { Id = Guid.NewGuid(), SourceRootId = area.RootId, RelativePath = ".",
                PathFingerprint = SqlSourceChangeJournalStore.PathKey("."), FileId = "0000000000000007", IsDirectory = true, BaselineEpoch = 1 });
            db.SourcePendingChanges.AddRange(Enumerable.Range(0, 10000).Select(index => new SourcePendingChangeEntity
            {
                Id = Guid.NewGuid(), SourceRootId = area.RootId, RelativePath = $"existing-{index}.txt",
                PathFingerprint = SqlSourceChangeJournalStore.PathKey($"existing-{index}.txt"), ReasonFlags = 1,
                Generation = 1, FirstUsn = 10, LastUsn = 50, LastContentUsn = 50, LastUnjournaledGeneration = 0,
                FirstObservedAtUtc = area.Clock.Now
            }));
            await db.SaveChangesAsync();
            return area;
        }
        public async ValueTask DisposeAsync()
        {
            await using var db = Factory.CreateDbContext();
            await db.SourcePendingChanges.Where(x => x.SourceRootId == RootId).ExecuteDeleteAsync();
            await db.SourceInventoryEntries.Where(x => x.SourceRootId == RootId).ExecuteDeleteAsync();
            await db.SourceRootScanStates.Where(x => x.SourceRootId == RootId).ExecuteDeleteAsync();
            await db.SourceRootConfigurations.Where(x => x.Id == RootId).ExecuteDeleteAsync();
            await db.SourceVolumeJournalCheckpoints.Where(x => x.VolumeIdentity == Volume).ExecuteDeleteAsync();
        }
    }
}
