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
    [NativeSqlServerTheory]
    [InlineData("sealed-known", null)]
    [InlineData("unsealed-known", "one.txt")]
    [InlineData("known-id-new-path", "new.txt")]
    [InlineData("unknown-id", "unknown.txt")]
    [InlineData("old-epoch", "one.txt")]
    public async Task Pure_close_advances_evidence_and_only_suppresses_the_same_current_sealed_path(string kind, string? expectedPath)
    {
        await using var scenario = await CloseScenario.CreateAsync(fixture.ConnectionString, kind);
        var change = new JournalChange(200, kind == "unknown-id" ? "000000000000002B" : "000000000000002A", "0000000000000007",
            kind == "known-id-new-path" ? "new.txt" : kind == "unknown-id" ? "unknown.txt" : "one.txt", 0x80000000, false);
        await scenario.AcceptAsync(change);
        await using var inspect = scenario.Factory.CreateDbContext();
        var pending = await inspect.SourcePendingChanges.Where(x => x.SourceRootId == scenario.Id).ToArrayAsync();
        if (expectedPath is null) Assert.Empty(pending);
        else Assert.Equal(expectedPath, Assert.Single(pending).RelativePath);
        Assert.Equal(300, (await inspect.SourceVolumeJournalCheckpoints.SingleAsync(x => x.VolumeIdentity == scenario.Volume)).NextUsn);
        var entry = await inspect.SourceInventoryEntries.SingleAsync(x => x.SourceRootId == scenario.Id && x.RelativePath == "one.txt");
        if (kind is not ("unknown-id" or "old-epoch"))
        { Assert.Equal(200, entry.LatestJournalUsn); Assert.Equal(0x80000000L, entry.LatestJournalReason); }
    }

    [NativeSqlServerFact]
    public async Task Ignored_close_does_not_erase_or_advance_the_generation_of_existing_work()
    {
        await using var scenario = await CloseScenario.CreateAsync(fixture.ConnectionString, "sealed-known");
        string original;
        await using (var setup = scenario.Factory.CreateDbContext())
        {
            var row = CloseScenario.Pending(scenario.Id, "one.txt"); setup.SourcePendingChanges.Add(row);
            await setup.SaveChangesAsync(); original = JsonSerializer.Serialize(row);
        }
        await scenario.AcceptAsync(new(200, "000000000000002A", "0000000000000007", "one.txt", 0x80000000, false));
        await using var inspect = scenario.Factory.CreateDbContext();
        Assert.Equal(original, JsonSerializer.Serialize(await inspect.SourcePendingChanges.SingleAsync(x => x.SourceRootId == scenario.Id)));
        Assert.Equal(7, (await inspect.SourceRootScanStates.SingleAsync(x => x.SourceRootId == scenario.Id)).Generation);
    }

    [NativeSqlServerFact]
    public async Task Sealed_close_at_the_pending_limit_cannot_invalidate_coverage_with_self_noise()
    {
        await using var scenario = await CloseScenario.CreateAsync(fixture.ConnectionString, "sealed-known");
        await using (var setup = scenario.Factory.CreateDbContext())
        {
            setup.SourcePendingChanges.AddRange(Enumerable.Range(0, 10000).Select(index => CloseScenario.Pending(scenario.Id, $"existing-debt-{index}.txt")));
            await setup.SaveChangesAsync();
        }
        await scenario.AcceptAsync(new(200, "000000000000002A", "0000000000000007", "one.txt", 0x80000000, false));
        await using var inspect = scenario.Factory.CreateDbContext();
        var state = await inspect.SourceRootScanStates.SingleAsync(x => x.SourceRootId == scenario.Id);
        Assert.Equal(1, state.BaselineEpoch); Assert.False(state.RecoveryRequired);
        Assert.Equal(10000, await inspect.SourcePendingChanges.CountAsync(x => x.SourceRootId == scenario.Id));
        Assert.Equal(300, (await inspect.SourceVolumeJournalCheckpoints.SingleAsync(x => x.VolumeIdentity == scenario.Volume)).NextUsn);
    }

    private sealed class CloseScenario(string connection) : IAsyncDisposable
    {
        internal ContextFactory Factory { get; } = new(connection);
        internal Guid Id { get; } = Guid.NewGuid();
        internal string Volume { get; } = $@"\\?\Volume{{{Guid.NewGuid()}}}\";
        internal static async Task<CloseScenario> CreateAsync(string connection, string kind)
        {
            var scenario = new CloseScenario(connection);
            await using var db = scenario.Factory.CreateDbContext();
            var now = DateTimeOffset.UtcNow;
            db.SourceRootConfigurations.Add(new() { Id = scenario.Id, CanonicalPath = $@"I:\close-{scenario.Id:N}", DisplayName = "CLOSE scope fixture",
                Recursive = true, MaximumFileBytes = 1024, ReconciliationCadenceSeconds = 900, ConfigurationRevision = 1, CreatedAtUtc = now, UpdatedAtUtc = now });
            db.SourceRootScanStates.Add(new() { SourceRootId = scenario.Id, VolumeIdentity = scenario.Volume, BaselineEpoch = 1, Generation = 7, Mode = "Journal", NamespaceComplete = true });
            db.SourceVolumeJournalCheckpoints.Add(new() { VolumeIdentity = scenario.Volume, JournalId = "0000000000000001", NextUsn = 100 });
            db.SourceInventoryEntries.Add(new() { Id = Guid.NewGuid(), SourceRootId = scenario.Id, RelativePath = ".", PathFingerprint = SqlSourceChangeJournalStore.PathKey("."),
                FileId = "0000000000000007", IsDirectory = true, BaselineEpoch = 1 });
            db.SourceInventoryEntries.Add(new() { Id = Guid.NewGuid(), SourceRootId = scenario.Id, RelativePath = "one.txt", PathFingerprint = SqlSourceChangeJournalStore.PathKey("one.txt"),
                FileId = "000000000000002A", ParentId = "0000000000000007", BaselineEpoch = kind == "old-epoch" ? 0 : 1,
                IsSealed = kind != "unsealed-known", SealProfileId = "qualified-proof" });
            await db.SaveChangesAsync(); return scenario;
        }
        internal static SourcePendingChangeEntity Pending(Guid root, string path) => new()
        {
            Id = Guid.NewGuid(), SourceRootId = root, RelativePath = path, PathFingerprint = SqlSourceChangeJournalStore.PathKey(path),
            ReasonFlags = 1, Generation = 7, FirstUsn = 150, LastUsn = 150, LastContentUsn = 150, LastUnjournaledGeneration = 0
        };
        internal async Task AcceptAsync(JournalChange change)
        {
            var store = new SqlSourceChangeJournalStore(Factory, TimeProvider.System);
            var claim = Assert.IsType<JournalReadClaim>(await store.ClaimVolumeAsync(Volume, DateTimeOffset.UtcNow, true, default));
            var page = new JournalPage(claim.Cursor, claim.Cursor with { NextUsn = 300 }, [change]);
            await store.AcceptAsync(claim, page, default); await store.AcceptAsync(claim, page, default);
        }
        public async ValueTask DisposeAsync()
        {
            await using var db = Factory.CreateDbContext();
            await db.SourceRootConfigurations.Where(x => x.Id == Id).ExecuteDeleteAsync();
            await db.SourceVolumeJournalCheckpoints.Where(x => x.VolumeIdentity == Volume).ExecuteDeleteAsync();
        }
    }
}
