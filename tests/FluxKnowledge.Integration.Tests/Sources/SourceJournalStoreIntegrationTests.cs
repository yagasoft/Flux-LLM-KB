using FluxKnowledge.Application.Ports;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using FluxKnowledge.Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Sources;

public sealed partial class SourceJournalStoreIntegrationTests(NativeSqlServerFixture fixture) : IClassFixture<NativeSqlServerFixture>
{
    [NativeSqlServerFact]
    public async Task Latest_actual_record_does_not_inherit_an_old_close_or_promote_a_stale_capture_on_replay()
    {
        var rootId = Guid.NewGuid();
        var volume = $@"\\?\Volume{{{Guid.NewGuid()}}}\";
        var clock = new Clock(DateTimeOffset.UtcNow);
        var factory = new ContextFactory(fixture.ConnectionString);
        var fileKey = SqlSourceChangeJournalStore.PathKey("one.txt");
        await using (var setup = factory.CreateDbContext())
        {
            setup.SourceRootConfigurations.Add(new() { Id = rootId, CanonicalPath = $@"I:\record-order-{rootId:N}", DisplayName = "Record order fixture",
                Recursive = true, MaximumFileBytes = 1024, ReconciliationCadenceSeconds = 900, ConfigurationRevision = 1,
                CreatedAtUtc = clock.Now, UpdatedAtUtc = clock.Now });
            setup.SourceRootScanStates.Add(new() { SourceRootId = rootId, VolumeIdentity = volume, BaselineEpoch = 1,
                Mode = "Journal", NamespaceComplete = true });
            setup.SourceVolumeJournalCheckpoints.Add(new() { VolumeIdentity = volume, JournalId = "0000000000000001", NextUsn = 100 });
            setup.SourceInventoryEntries.Add(new() { Id = Guid.NewGuid(), SourceRootId = rootId, RelativePath = ".", PathFingerprint = "root",
                FileId = "0000000000000007", IsDirectory = true, BaselineEpoch = 1 });
            setup.SourceInventoryEntries.Add(new() { Id = Guid.NewGuid(), SourceRootId = rootId, RelativePath = "one.txt", PathFingerprint = fileKey,
                FileId = "000000000000002A", ParentId = "0000000000000007", BaselineEpoch = 1, IsSealed = true, SealProfileId = "prior-profile" });
            await setup.SaveChangesAsync();
        }
        try
        {
            var store = new SqlSourceChangeJournalStore(factory, clock);
            var claim = Assert.IsType<JournalReadClaim>(await store.ClaimVolumeAsync(volume, clock.Now, true, default));
            var page = new JournalPage(claim.Cursor, claim.Cursor with { NextUsn = 300 },
                [new(150, "000000000000002A", "0000000000000007", "one.txt", 0x80000001, false),
                 new(250, "000000000000002A", "0000000000000007", "one.txt", 1, false)]);
            await store.AcceptAsync(claim, page, default);
            await store.AcceptAsync(claim, page, default);
            await using (var inspect = factory.CreateDbContext())
            {
                var entry = await inspect.SourceInventoryEntries.SingleAsync(x => x.SourceRootId == rootId && x.PathFingerprint == fileKey);
                Assert.Equal(250, entry.LatestJournalUsn); Assert.Equal(1, entry.LatestJournalReason); Assert.False(entry.IsSealed);
                var pending = await inspect.SourcePendingChanges.SingleAsync(x => x.SourceRootId == rootId);
                Assert.Equal(1, pending.Generation); Assert.Equal(250, pending.LastContentUsn); Assert.Equal(0, pending.LastUnjournaledGeneration);
            }
            var closing = Assert.IsType<JournalReadClaim>(await store.ClaimVolumeAsync(volume, clock.Now, true, default));
            await store.AcceptAsync(closing, new(closing.Cursor, closing.Cursor with { NextUsn = 500 },
                [new(400, "000000000000002A", "0000000000000007", "one.txt", 0x80000000, false)]), default);
            await using var verify = factory.CreateDbContext();
            var latest = await verify.SourceInventoryEntries.SingleAsync(x => x.SourceRootId == rootId && x.PathFingerprint == fileKey);
            Assert.Equal(400, latest.LatestJournalUsn); Assert.Equal(0x80000000L, latest.LatestJournalReason); Assert.False(latest.IsSealed);
            Assert.Equal(250, (await verify.SourcePendingChanges.SingleAsync(x => x.SourceRootId == rootId)).LastContentUsn);
            Assert.Equal(500, (await verify.SourceVolumeJournalCheckpoints.SingleAsync(x => x.VolumeIdentity == volume)).NextUsn);
        }
        finally
        {
            await using var cleanup = factory.CreateDbContext();
            await cleanup.SourceRootConfigurations.Where(x => x.Id == rootId).ExecuteDeleteAsync();
            await cleanup.SourceVolumeJournalCheckpoints.Where(x => x.VolumeIdentity == volume).ExecuteDeleteAsync();
        }
    }

    [NativeSqlServerFact]
    public async Task Repeated_unresolved_records_keep_paced_namespace_debt_without_content_epoch_churn()
    {
        var rootId = Guid.NewGuid();
        var volume = $@"\\?\Volume{{{Guid.NewGuid()}}}\";
        var clock = new Clock(DateTimeOffset.UtcNow);
        var started = clock.Now;
        var factory = new ContextFactory(fixture.ConnectionString);
        await using (var setup = factory.CreateDbContext())
        {
            setup.SourceRootConfigurations.Add(new() { Id = rootId, CanonicalPath = $@"I:\namespace-{rootId:N}", DisplayName = "Namespace fixture",
                Recursive = true, MaximumFileBytes = 1024, IncludePatternsJson = "[]", ExcludePatternsJson = "[]", AllowedClassificationsJson = "[]",
                ReconciliationCadenceSeconds = 900, ConfigurationRevision = 1, CreatedAtUtc = started, UpdatedAtUtc = started });
            setup.SourceRootScanStates.Add(new() { SourceRootId = rootId, VolumeIdentity = volume, BaselineEpoch = 4, RecoveryGeneration = 7,
                Mode = "JournalPartial", NamespaceComplete = false, LastReconciliationAtUtc = started });
            setup.SourceVolumeJournalCheckpoints.Add(new() { VolumeIdentity = volume, JournalId = "0000000000000001", NextUsn = 100 });
            await setup.SaveChangesAsync();
        }
        try
        {
            var store = new SqlSourceChangeJournalStore(factory, clock);
            for (var index = 0; index < 5; index++)
            {
                var claim = Assert.IsType<JournalReadClaim>(await store.ClaimVolumeAsync(volume, clock.Now, true, default));
                await store.AcceptAsync(claim, new(claim.Cursor, claim.Cursor with { NextUsn = claim.Cursor.NextUsn + 100 },
                    [new(claim.Cursor.NextUsn + 1, (100 + index).ToString("X16"), "0000000000009999", "unresolved.txt", 1, false)]), default);
                clock.Now += TimeSpan.FromMinutes(1);
            }
            await using var verify = factory.CreateDbContext();
            var state = await verify.SourceRootScanStates.SingleAsync(x => x.SourceRootId == rootId);
            Assert.Equal(4, state.BaselineEpoch);
            Assert.Equal(7, state.RecoveryGeneration);
            Assert.False(state.RecoveryRequired);
            Assert.Equal(5, state.NamespaceGeneration);
            var debt = Assert.Single(await verify.SourcePendingChanges.Where(x => x.SourceRootId == rootId).ToArrayAsync());
            Assert.Equal(".", debt.RelativePath);
            Assert.Equal(SourcePendingReasons.NamespaceMetadata, debt.ReasonFlags);
            Assert.Equal(started.AddMinutes(15), debt.NotBeforeUtc);
            Assert.False(await verify.SourceScanRequests.AnyAsync(x => x.SourceRootId == rootId));
            Assert.Equal(600, (await verify.SourceVolumeJournalCheckpoints.SingleAsync(x => x.VolumeIdentity == volume)).NextUsn);
        }
        finally
        {
            await using var cleanup = factory.CreateDbContext();
            await cleanup.SourceRootConfigurations.Where(x => x.Id == rootId).ExecuteDeleteAsync();
            await cleanup.SourceVolumeJournalCheckpoints.Where(x => x.VolumeIdentity == volume).ExecuteDeleteAsync();
        }
    }

    [NativeSqlServerFact]
    public async Task Registration_during_an_outstanding_read_replays_the_range_for_the_new_root_before_cursor_advance()
    {
        var ids = new[] { Guid.NewGuid(), Guid.NewGuid() };
        var volume = $@"\\?\Volume{{{Guid.NewGuid()}}}\";
        var factory = new ContextFactory(fixture.ConnectionString);
        var now = DateTimeOffset.UtcNow;
        async Task RegisterAsync(int index)
        {
            await using var db = factory.CreateDbContext();
            db.SourceRootConfigurations.Add(new() { Id = ids[index], CanonicalPath = $@"I:\registration-{ids[index]:N}", DisplayName = "Registration fixture",
                Recursive = true, MaximumFileBytes = 1024, IncludePatternsJson = "[]", ExcludePatternsJson = "[]", AllowedClassificationsJson = "[]",
                ReconciliationCadenceSeconds = 900, ConfigurationRevision = 1, CreatedAtUtc = now, UpdatedAtUtc = now });
            db.SourceRootScanStates.Add(new() { SourceRootId = ids[index], VolumeIdentity = volume, BaselineEpoch = 1, Mode = "Journal", NamespaceComplete = true, PolicyFingerprint = "test" });
            db.SourceInventoryEntries.Add(new() { Id = Guid.NewGuid(), SourceRootId = ids[index], RelativePath = ".", PathFingerprint = "root",
                FileId = (7 + index).ToString("X16"), IsDirectory = true, BaselineEpoch = 1 });
            var point = await db.SourceVolumeJournalCheckpoints.SingleOrDefaultAsync(x => x.VolumeIdentity == volume);
            if (point is null) db.SourceVolumeJournalCheckpoints.Add(new() { VolumeIdentity = volume, JournalId = "0000000000000001", NextUsn = 100, RegistrationGeneration = 1 });
            else point.RegistrationGeneration++;
            await db.SaveChangesAsync();
        }
        try
        {
            await RegisterAsync(0);
            var store = new SqlSourceChangeJournalStore(factory, TimeProvider.System);
            var first = Assert.IsType<JournalReadClaim>(await store.ClaimVolumeAsync(volume, now, true, default));
            var page = new JournalPage(first.Cursor, first.Cursor with { NextUsn = 500 },
                [new(200, "000000000000002A", "0000000000000007", "first.txt", 1, false),
                 new(300, "000000000000002B", "0000000000000008", "second.txt", 1, false)]);
            await RegisterAsync(1);
            await store.AcceptAsync(first, page, default);
            await using (var verify = factory.CreateDbContext())
            {
                Assert.Equal(100, (await verify.SourceVolumeJournalCheckpoints.SingleAsync(x => x.VolumeIdentity == volume)).NextUsn);
                Assert.False(await verify.SourcePendingChanges.AnyAsync(x => ids.Contains(x.SourceRootId)));
            }
            var retry = Assert.IsType<JournalReadClaim>(await store.ClaimVolumeAsync(volume, DateTimeOffset.UtcNow, true, default));
            Assert.True(retry.RegistrationGeneration > first.RegistrationGeneration);
            await store.AcceptAsync(retry, page, default);
            await using var final = factory.CreateDbContext();
            Assert.Equal(500, (await final.SourceVolumeJournalCheckpoints.SingleAsync(x => x.VolumeIdentity == volume)).NextUsn);
            Assert.Equal("first.txt", (await final.SourcePendingChanges.SingleAsync(x => x.SourceRootId == ids[0])).RelativePath);
            Assert.Equal("second.txt", (await final.SourcePendingChanges.SingleAsync(x => x.SourceRootId == ids[1])).RelativePath);
        }
        finally
        {
            await using var cleanup = factory.CreateDbContext();
            await cleanup.SourceRootConfigurations.Where(x => ids.Contains(x.Id)).ExecuteDeleteAsync();
            await cleanup.SourceVolumeJournalCheckpoints.Where(x => x.VolumeIdentity == volume).ExecuteDeleteAsync();
        }
    }

    [NativeSqlServerFact]
    public async Task Repeated_empty_reads_release_each_current_claim_and_a_later_append_is_not_delayed()
    {
        var volume = $@"\\?\Volume{{{Guid.NewGuid()}}}\";
        var clock = new Clock(DateTimeOffset.UtcNow);
        var factory = new ContextFactory(fixture.ConnectionString);
        await using (var setup = factory.CreateDbContext())
        {
            setup.SourceVolumeJournalCheckpoints.Add(new() { VolumeIdentity = volume, JournalId = "0000000000000001", NextUsn = 100 });
            await setup.SaveChangesAsync();
        }
        try
        {
            var store = new SqlSourceChangeJournalStore(factory, clock);
            for (var attempt = 0; attempt < 2; attempt++)
            {
                var claim = Assert.IsType<JournalReadClaim>(await store.ClaimAsync(clock.GetUtcNow(), default));
                Assert.Equal(volume, claim.Cursor.VolumeIdentity);
                await store.AcceptAsync(claim, new(claim.Cursor, claim.Cursor, []), default);
                await using var check = factory.CreateDbContext();
                var point = await check.SourceVolumeJournalCheckpoints.SingleAsync(x => x.VolumeIdentity == volume);
                Assert.Null(point.LeaseOwner);
                Assert.Null(point.LeaseExpiresAtUtc);
                clock.Now += TimeSpan.FromSeconds(3);
            }
            var next = Assert.IsType<JournalReadClaim>(await store.ClaimAsync(clock.GetUtcNow(), default));
            await store.AcceptAsync(next, new(next.Cursor, next.Cursor with { NextUsn = 500 },
                [new(200, "000000000000002A", "0000000000000007", "outside.txt", 1, false)]), default);
            await using var verify = factory.CreateDbContext();
            Assert.Equal(500, (await verify.SourceVolumeJournalCheckpoints.SingleAsync(x => x.VolumeIdentity == volume)).NextUsn);
        }
        finally
        {
            await using var cleanup = factory.CreateDbContext();
            await cleanup.SourceVolumeJournalCheckpoints.Where(x => x.VolumeIdentity == volume).ExecuteDeleteAsync();
        }
    }

    [NativeSqlServerFact]
    public async Task Cursor_and_dirty_work_commit_together_and_replay_is_idempotent()
    {
        var rootId = Guid.NewGuid();
        var volume = @"\\?\Volume{11111111-1111-1111-1111-111111111111}\";
        var now = DateTimeOffset.UtcNow;
        var factory = new ContextFactory(fixture.ConnectionString);
        await using (var setup = factory.CreateDbContext())
        {
            setup.SourceRootConfigurations.Add(new() { Id = rootId, CanonicalPath = $"I:\\journal-test-{rootId:N}", DisplayName = "Journal fixture",
                Recursive = true, MaximumFileBytes = 1024, IncludePatternsJson = "[]", ExcludePatternsJson = "[]", AllowedClassificationsJson = "[]",
                ReconciliationCadenceSeconds = 900, ConfigurationRevision = 1, CreatedAtUtc = now, UpdatedAtUtc = now });
            setup.SourceVolumeJournalCheckpoints.Add(new() { VolumeIdentity = volume, JournalId = "0000000000000001", NextUsn = 100 });
            setup.SourceRootScanStates.Add(new() { SourceRootId = rootId, VolumeIdentity = volume, BaselineEpoch = 1, Mode = "Journal", PolicyFingerprint = "test" });
            setup.SourceInventoryEntries.Add(new() { Id = Guid.NewGuid(), SourceRootId = rootId, RelativePath = ".", PathFingerprint = "root", FileId = "0000000000000007", ParentId = "", IsDirectory = true, BaselineEpoch = 1 });
            await setup.SaveChangesAsync();
        }
        try
        {
            var store = new SqlSourceChangeJournalStore(factory, TimeProvider.System);
            var claim = Assert.IsType<JournalReadClaim>(await store.ClaimAsync(now, default));
            var page = new JournalPage(new(volume, 1, 100), new(volume, 1, 500), [new(200, "000000000000002A", "0000000000000007", "a.txt", 1, false)]);
            await store.AcceptAsync(claim, page, default);
            await store.AcceptAsync(claim, page, default); // Simulates commit acknowledgement loss.
            await store.AcceptAsync(claim, page with
            {
                Changes = [page.Changes[0] with { ParentPath = @"I:\different-current-parent" }]
            }, default); // Current parent resolution is not part of the original native event.
            await using var verify = factory.CreateDbContext();
            Assert.Equal(500, (await verify.SourceVolumeJournalCheckpoints.SingleAsync(x => x.VolumeIdentity == volume)).NextUsn);
            var pending = Assert.Single(await verify.SourcePendingChanges.Where(x => x.SourceRootId == rootId).ToListAsync());
            Assert.Equal("a.txt", pending.RelativePath);
            Assert.Equal(1, pending.Generation);
        }
        finally
        {
            await using var cleanup = factory.CreateDbContext();
            await cleanup.SourceRootConfigurations.Where(x => x.Id == rootId).ExecuteDeleteAsync();
            await cleanup.SourceVolumeJournalCheckpoints.Where(x => x.VolumeIdentity == volume).ExecuteDeleteAsync();
        }
    }

    [NativeSqlServerFact]
    public async Task Continuous_changes_become_due_at_the_maximum_debounce_without_losing_later_usns()
    {
        var rootId = Guid.NewGuid();
        var volume = $@"\\?\Volume{{{Guid.NewGuid()}}}\";
        var clock = new Clock(DateTimeOffset.UtcNow);
        var started = clock.GetUtcNow();
        var factory = new ContextFactory(fixture.ConnectionString);
        await using (var setup = factory.CreateDbContext())
        {
            setup.SourceRootConfigurations.Add(new() { Id = rootId, CanonicalPath = $"I:\\journal-test-{rootId:N}", DisplayName = "Debounce fixture",
                Recursive = true, MaximumFileBytes = 1024, IncludePatternsJson = "[]", ExcludePatternsJson = "[]", AllowedClassificationsJson = "[]",
                ReconciliationCadenceSeconds = 900, ConfigurationRevision = 1, CreatedAtUtc = started, UpdatedAtUtc = started });
            setup.SourceVolumeJournalCheckpoints.Add(new() { VolumeIdentity = volume, JournalId = "0000000000000001", NextUsn = 100 });
            setup.SourceRootScanStates.Add(new() { SourceRootId = rootId, VolumeIdentity = volume, BaselineEpoch = 1, Mode = "Journal", PolicyFingerprint = "test" });
            setup.SourceInventoryEntries.Add(new() { Id = Guid.NewGuid(), SourceRootId = rootId, RelativePath = ".", PathFingerprint = "root", FileId = "0000000000000007", ParentId = "", IsDirectory = true, BaselineEpoch = 1 });
            await setup.SaveChangesAsync();
        }
        try
        {
            var store = new SqlSourceChangeJournalStore(factory, clock);
            for (var i = 0; i < 35; i++)
            {
                var claim = Assert.IsType<JournalReadClaim>(await store.ClaimAsync(clock.GetUtcNow(), default));
                var next = claim.Cursor with { NextUsn = claim.Cursor.NextUsn + 100 };
                await store.AcceptAsync(claim, new(claim.Cursor, next,
                    [new(claim.Cursor.NextUsn + 1, "000000000000002A", "0000000000000007", "busy.txt", 1, false)]), default);
                clock.Now += TimeSpan.FromSeconds(1);
            }
            await using var verify = factory.CreateDbContext();
            var pending = await verify.SourcePendingChanges.SingleAsync(x => x.SourceRootId == rootId);
            Assert.True(pending.NotBeforeUtc <= started.AddSeconds(30), "Continuous writes must not postpone eligibility beyond the maximum debounce.");
            Assert.Equal(35, pending.Generation);
            Assert.Equal(3501, pending.LastUsn);
            Assert.Equal(3600, (await verify.SourceVolumeJournalCheckpoints.SingleAsync(x => x.VolumeIdentity == volume)).NextUsn);
        }
        finally
        {
            await using var cleanup = factory.CreateDbContext();
            await cleanup.SourceRootConfigurations.Where(x => x.Id == rootId).ExecuteDeleteAsync();
            await cleanup.SourceVolumeJournalCheckpoints.Where(x => x.VolumeIdentity == volume).ExecuteDeleteAsync();
        }
    }

    internal sealed class ContextFactory(string connection) : IDbContextFactory<FluxKnowledgeDbContext>
    {
        public FluxKnowledgeDbContext CreateDbContext() => new(new DbContextOptionsBuilder<FluxKnowledgeDbContext>()
            .UseSqlServer(connection, sql => sql.EnableRetryOnFailure()).Options);
        public Task<FluxKnowledgeDbContext> CreateDbContextAsync(CancellationToken ct = default) => Task.FromResult(CreateDbContext());
    }

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        internal DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
