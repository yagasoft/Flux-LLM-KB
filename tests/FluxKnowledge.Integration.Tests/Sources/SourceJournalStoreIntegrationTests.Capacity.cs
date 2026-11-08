using System.Text.Json;
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
    public async Task Capacity_marks_an_indivisible_alias_page_too_large_without_consuming_known_paths_or_inventory_evidence()
    {
        await using var area = await PendingCapacity.CreateAsync(fixture.ConnectionString);
        await using (var db = area.Factory.CreateDbContext())
        {
            // Ten changed physical identities, each with 1,001 known aliases: a
            // small native page can fan out beyond the bounded per-root queue.
            db.SourceInventoryEntries.AddRange(Enumerable.Range(0, 10).SelectMany(file => Enumerable.Range(0, 1001).Select(link =>
                new SourceInventoryEntryEntity { Id = Guid.NewGuid(), SourceRootId = area.RootId,
                    RelativePath = $"alias-{file}-{link}.txt", PathFingerprint = SqlSourceChangeJournalStore.PathKey($"alias-{file}-{link}.txt"),
                    FileId = (1000 + file).ToString("X16"), ParentId = "0000000000000007", BaselineEpoch = 1 })));
            await db.SaveChangesAsync();
        }
        var before = await area.PendingSnapshotAsync();
        var store = new SqlSourceChangeJournalStore(area.Factory, area.Clock);
        var claim = Assert.IsType<JournalReadClaim>(await store.ClaimVolumeAsync(area.Volume, area.Clock.Now, true, default));
        var error = await Assert.ThrowsAsync<SourcePendingCapacityException>(() => store.AcceptAsync(claim,
            new(claim.Cursor, claim.Cursor with { NextUsn = 200 }, Enumerable.Range(0, 10).Select(file =>
                new JournalChange(110 + file, (1000 + file).ToString("X16"), "0000000000000007", $"alias-{file}-0.txt", 1, false)).ToArray()), default).AsTask());
        Assert.True(error.PageTooLarge);
        Assert.Equal(before, await area.PendingSnapshotAsync());
        await using var after = area.Factory.CreateDbContext();
        Assert.False(await after.SourceInventoryEntries.AnyAsync(x => x.SourceRootId == area.RootId && x.LatestJournalUsn != null));
        Assert.Equal(100, (await after.SourceVolumeJournalCheckpoints.SingleAsync(x => x.VolumeIdentity == area.Volume)).NextUsn);
    }

    [NativeSqlServerFact]
    public async Task Capacity_refusal_on_the_later_root_rolls_back_earlier_root_progress_and_the_shared_cursor()
    {
        await using var area = await PendingCapacity.CreateAsync(fixture.ConnectionString);
        var other = Guid.NewGuid();
        try
        {
            Guid full, empty;
            string before;
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
                before = JsonSerializer.Serialize(await db.SourceRootScanStates.AsNoTracking().OrderBy(x => x.SourceRootId).ToArrayAsync());
            }
            var store = new SqlSourceChangeJournalStore(area.Factory, area.Clock);
            var claim = Assert.IsType<JournalReadClaim>(await store.ClaimVolumeAsync(area.Volume, area.Clock.Now, true, default));
            await Assert.ThrowsAsync<SourcePendingCapacityException>(() => store.AcceptAsync(claim,
                new(claim.Cursor, claim.Cursor with { NextUsn = 200 }, [new(150, "000000000000002A", "0000000000000007", "new.txt", 1, false)]), default).AsTask());
            await using var after = area.Factory.CreateDbContext();
            Assert.Equal(before, JsonSerializer.Serialize(await after.SourceRootScanStates.AsNoTracking().OrderBy(x => x.SourceRootId).ToArrayAsync()));
            Assert.Equal(10000, await after.SourcePendingChanges.CountAsync(x => x.SourceRootId == full));
            Assert.False(await after.SourcePendingChanges.AnyAsync(x => x.SourceRootId == empty));
            Assert.Equal(100, (await after.SourceVolumeJournalCheckpoints.SingleAsync(x => x.VolumeIdentity == area.Volume)).NextUsn);
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
    public async Task Capacity_refuses_a_new_path_atomically_and_retries_after_admitted_work_drains()
    {
        await using var area = await PendingCapacity.CreateAsync(fixture.ConnectionString);
        var store = new SqlSourceChangeJournalStore(area.Factory, area.Clock);
        var claim = Assert.IsType<JournalReadClaim>(await store.ClaimVolumeAsync(area.Volume, area.Clock.Now, true, default));
        var before = await area.PendingSnapshotAsync();
        var page = new JournalPage(claim.Cursor, claim.Cursor with { NextUsn = 200 },
            [new(150, "000000000000002A", "0000000000000007", "new.txt", 1, false)]);
        var failure = await Assert.ThrowsAnyAsync<IOException>(() => store.AcceptAsync(claim, page, default).AsTask());
        Assert.Contains("PendingCapacity", failure.Message, StringComparison.Ordinal);
        Assert.Equal(before, await area.PendingSnapshotAsync());
        await using (var failed = area.Factory.CreateDbContext())
        {
            Assert.Equal(100, (await failed.SourceVolumeJournalCheckpoints.SingleAsync(x => x.VolumeIdentity == area.Volume)).NextUsn);
            Assert.Equal(1, (await failed.SourceRootScanStates.SingleAsync(x => x.SourceRootId == area.RootId)).Generation);
            Assert.False(await failed.AuditEvents.AnyAsync(x => x.SourceRootId == area.RootId && x.EventType == "source.journal.recovery_required"));
            // Represents one normally completed admitted path; no source/retained
            // data or production work is touched by this disposable arrangement.
            failed.SourcePendingChanges.Remove(await failed.SourcePendingChanges.FirstAsync(x => x.SourceRootId == area.RootId));
            await failed.SaveChangesAsync();
        }
        await store.AcceptAsync(claim, page, default);
        await using var recovered = area.Factory.CreateDbContext();
        Assert.Equal(10000, await recovered.SourcePendingChanges.CountAsync(x => x.SourceRootId == area.RootId));
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
        internal async Task<string> PendingSnapshotAsync()
        {
            await using var db = Factory.CreateDbContext();
            return JsonSerializer.Serialize(await db.SourcePendingChanges.AsNoTracking().Where(x => x.SourceRootId == RootId).OrderBy(x => x.Id).ToArrayAsync());
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
