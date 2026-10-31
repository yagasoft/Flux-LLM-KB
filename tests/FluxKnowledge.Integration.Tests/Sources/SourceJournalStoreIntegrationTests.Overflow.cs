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
    public async Task Pending_path_overflow_coalesces_durable_recovery_without_restarting_a_frozen_scan()
    {
        var rootId = Guid.NewGuid();
        var volume = $@"\\?\Volume{{{Guid.NewGuid()}}}\";
        var clock = new Clock(DateTimeOffset.UtcNow);
        var factory = new ContextFactory(fixture.ConnectionString);
        await using (var db = factory.CreateDbContext())
        {
            db.SourceRootConfigurations.Add(new() { Id = rootId, CanonicalPath = $@"I:\overflow-{rootId:N}", DisplayName = "Overflow fixture",
                Recursive = true, MaximumFileBytes = 1024, ReconciliationCadenceSeconds = 900, ConfigurationRevision = 1,
                CreatedAtUtc = clock.Now, UpdatedAtUtc = clock.Now });
            db.SourceRootScanStates.Add(new() { SourceRootId = rootId, VolumeIdentity = volume, BaselineEpoch = 4,
                RecoveryGeneration = 7, Generation = 1, Mode = "Journal", NamespaceComplete = true });
            db.SourceVolumeJournalCheckpoints.Add(new() { VolumeIdentity = volume, JournalId = "0000000000000001", NextUsn = 100 });
            db.SourceInventoryEntries.Add(new() { Id = Guid.NewGuid(), SourceRootId = rootId, RelativePath = ".", PathFingerprint = SqlSourceChangeJournalStore.PathKey("."),
                FileId = "0000000000000007", IsDirectory = true, BaselineEpoch = 4 });
            db.SourceInventoryEntries.Add(new() { Id = Guid.NewGuid(), SourceRootId = rootId, RelativePath = "closed.txt", PathFingerprint = SqlSourceChangeJournalStore.PathKey("closed.txt"),
                FileId = "000000000000002A", ParentId = "0000000000000007", IsSealed = true, BaselineEpoch = 4 });
            db.SourcePendingChanges.AddRange(Enumerable.Range(0, 10000).Select(index => new SourcePendingChangeEntity
            {
                Id = Guid.NewGuid(), SourceRootId = rootId, RelativePath = $"existing-{index}.txt",
                PathFingerprint = SqlSourceChangeJournalStore.PathKey($"existing-{index}.txt"),
                ReasonFlags = 1, Generation = 1, FirstUsn = 10, LastUsn = 50, LastContentUsn = 50,
                LastUnjournaledGeneration = 0, FirstObservedAtUtc = clock.Now.AddMinutes(-1)
            }));
            await db.SaveChangesAsync();
        }
        try
        {
            var store = new SqlSourceChangeJournalStore(factory, clock);
            long previousGeneration = 1;
            for (var page = 0; page < 2; page++)
            {
                var claim = Assert.IsType<JournalReadClaim>(await store.ClaimVolumeAsync(volume, clock.Now, true, default));
                await store.AcceptAsync(claim, new(claim.Cursor, claim.Cursor with { NextUsn = claim.Cursor.NextUsn + 100 },
                    [new(claim.Cursor.NextUsn + 50, (1000 + page).ToString("X16"), "0000000000000007", $"new-{page}.txt", 0x200, false)]), default);
                await using var inspect = factory.CreateDbContext();
                var state = await inspect.SourceRootScanStates.SingleAsync(x => x.SourceRootId == rootId);
                Assert.Equal(4, state.BaselineEpoch);
                Assert.Equal(7, state.RecoveryGeneration);
                Assert.False(state.RecoveryRequired);
                Assert.False(state.NamespaceComplete);
                var pending = Assert.Single(await inspect.SourcePendingChanges.Where(x => x.SourceRootId == rootId).ToArrayAsync());
                Assert.Equal(".", pending.RelativePath);
                Assert.True(pending.IsSubtree);
                Assert.True(pending.Generation > previousGeneration);
                Assert.True(pending.LastUnjournaledGeneration > 0);
                Assert.Equal(claim.Cursor.NextUsn + 50, pending.LastUsn);
                Assert.Equal(claim.Cursor.NextUsn + 100,
                    (await inspect.SourceVolumeJournalCheckpoints.SingleAsync(x => x.VolumeIdentity == volume)).NextUsn);
                previousGeneration = pending.Generation;
                clock.Now += TimeSpan.FromSeconds(3);
            }
            long captureBoundary, namespaceGeneration;
            await using (var beforeClose = factory.CreateDbContext())
            {
                captureBoundary = (await beforeClose.SourcePendingChanges.SingleAsync(x => x.SourceRootId == rootId)).LastUnjournaledGeneration!.Value;
                namespaceGeneration = (await beforeClose.SourceRootScanStates.SingleAsync(x => x.SourceRootId == rootId)).NamespaceGeneration;
            }
            var closingClaim = Assert.IsType<JournalReadClaim>(await store.ClaimVolumeAsync(volume, clock.Now, true, default));
            await store.AcceptAsync(closingClaim, new(closingClaim.Cursor, closingClaim.Cursor with { NextUsn = 400 },
                [new(350, "000000000000002A", "0000000000000007", "closed.txt", 0x80000000, false)]), default);
            await using var verifyClose = factory.CreateDbContext();
            var coarse = await verifyClose.SourcePendingChanges.SingleAsync(x => x.SourceRootId == rootId);
            Assert.Equal(captureBoundary, coarse.LastUnjournaledGeneration);
            Assert.Equal(250, coarse.LastContentUsn);
            Assert.Equal(previousGeneration, coarse.Generation);
            Assert.Equal(namespaceGeneration, (await verifyClose.SourceRootScanStates.SingleAsync(x => x.SourceRootId == rootId)).NamespaceGeneration);

            // Unqualified/fallback captures are not sealed. Their own known-path
            // CLOSEs must not keep a coarse obligation alive or recreate removal debt.
            (await verifyClose.SourceInventoryEntries.SingleAsync(x => x.SourceRootId == rootId && x.RelativePath == "closed.txt")).IsSealed = false;
            coarse.ReasonFlags &= ~SourcePendingReasons.RemovalPending;
            await verifyClose.SaveChangesAsync();
            for (var repeat = 0; repeat < 2; repeat++)
            {
                var claim = Assert.IsType<JournalReadClaim>(await store.ClaimVolumeAsync(volume, clock.Now, true, default));
                var usn = claim.Cursor.NextUsn + 50;
                await store.AcceptAsync(claim, new(claim.Cursor, claim.Cursor with { NextUsn = claim.Cursor.NextUsn + 100 },
                    [new(usn, "000000000000002A", "0000000000000007", "closed.txt", 0x80000000, false)]), default);
                await using var check = factory.CreateDbContext();
                var pending = await check.SourcePendingChanges.SingleAsync(x => x.SourceRootId == rootId);
                Assert.Equal(previousGeneration, pending.Generation);
                Assert.Equal(0, pending.ReasonFlags & SourcePendingReasons.RemovalPending);
                Assert.Equal(captureBoundary, pending.LastUnjournaledGeneration);
                Assert.Equal(250, pending.LastContentUsn);
                Assert.Equal(namespaceGeneration, (await check.SourceRootScanStates.SingleAsync(x => x.SourceRootId == rootId)).NamespaceGeneration);
                var observed = await check.SourceInventoryEntries.SingleAsync(x => x.SourceRootId == rootId && x.RelativePath == "closed.txt");
                Assert.False(observed.IsSealed);
                Assert.Equal(usn, observed.LatestJournalUsn);
                Assert.Equal(claim.Cursor.NextUsn + 100, (await check.SourceVolumeJournalCheckpoints.SingleAsync(x => x.VolumeIdentity == volume)).NextUsn);
            }
            var edit = Assert.IsType<JournalReadClaim>(await store.ClaimVolumeAsync(volume, clock.Now, true, default));
            await store.AcceptAsync(edit, new(edit.Cursor, edit.Cursor with { NextUsn = edit.Cursor.NextUsn + 100 },
                [new(edit.Cursor.NextUsn + 50, "000000000000002A", "0000000000000007", "closed.txt", 0x80000001, false)]), default);
            await using var afterEdit = factory.CreateDbContext();
            var edited = await afterEdit.SourcePendingChanges.SingleAsync(x => x.SourceRootId == rootId);
            Assert.True(edited.Generation > previousGeneration);
            Assert.NotEqual(0, edited.ReasonFlags & SourcePendingReasons.RemovalPending);
            Assert.Equal(edit.Cursor.NextUsn + 50, edited.LastContentUsn);
        }
        finally
        {
            await using var cleanup = factory.CreateDbContext();
            await cleanup.SourceRootConfigurations.Where(x => x.Id == rootId).ExecuteDeleteAsync();
            await cleanup.SourceVolumeJournalCheckpoints.Where(x => x.VolumeIdentity == volume).ExecuteDeleteAsync();
        }
    }
}
