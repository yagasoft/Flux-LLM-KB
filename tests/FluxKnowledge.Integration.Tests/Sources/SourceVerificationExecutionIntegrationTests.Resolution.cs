using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Sources;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Integrations.Files;
using FluxKnowledge.Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Sources;

public sealed partial class SourceVerificationExecutionIntegrationTests
{
    [NativeSqlServerTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Git_untracking_resolves_only_until_a_current_authoritative_successor_is_retracked(bool retrack)
    {
        await using var scenario = await Scenario.CreateAsync(fixture.ConnectionString, gitTracked: true);
        await scenario.BaselineAsync();
        var run = await scenario.Verification.StartAsync(scenario.RootId, true, default);
        scenario.Git("rm", "--cached", "--quiet", "one.txt");
        await RunOrdinaryChangesAsync(scenario);
        if (retrack)
        {
            await File.WriteAllTextAsync(Path.Combine(scenario.Sources, "one.txt"), new string('c', 64));
            scenario.Git("add", "one.txt");
            await RunOrdinaryChangesAsync(scenario);
        }
        await RunVerificationAsync(scenario);
        var status = (await scenario.Verification.ReadAsync(scenario.RootId, default))!;
        Assert.Equal("Verified", status.Run!.State);
        Assert.Equal(1, status.Run.Verified);
        Assert.Equal(1, status.Run.Resolved);
        Assert.Equal(retrack ? 160 : 96, status.Run.SourceBytesRead);
        await using var db = scenario.Factory.CreateDbContext();
        Assert.True(await db.SourceVerificationItems.AnyAsync(x => x.RunId == run && x.State == (retrack ? "CapturedSuccessor" : "OutOfScope")));
    }

    [NativeSqlServerFact]
    public async Task New_cycle_excludes_already_suppressed_inventory_and_preserves_unbound_debt()
    {
        await using var scenario = await Scenario.CreateAsync(fixture.ConnectionString);
        await scenario.BaselineAsync();
        File.Delete(Path.Combine(scenario.Sources, "one.txt"));
        await RunOrdinaryChangesAsync(scenario);
        await using (var db = scenario.Factory.CreateDbContext())
        {
            var epoch = await db.SourceRootScanStates.Select(x => x.BaselineEpoch).SingleAsync();
            db.SourceInventoryEntries.Add(new() { Id = Guid.NewGuid(), SourceRootId = scenario.RootId.Value, RelativePath = "unbound.txt",
                PathFingerprint = SqlSourceChangeJournalStore.PathKey("unbound.txt"), FileId = "unbound", BaselineEpoch = epoch });
            await db.SaveChangesAsync();
        }
        var run = await scenario.Verification.StartAsync(scenario.RootId, true, default);
        await using var after = scenario.Factory.CreateDbContext();
        var items = await after.SourceVerificationItems.Where(x => x.RunId == run).ToArrayAsync();
        Assert.Equal(2, items.Length);
        Assert.Contains(items, item => item.ExpectedRevisionId is null);
        Assert.DoesNotContain(items, item => item.BindingJson.Contains("one.txt", StringComparison.Ordinal));
    }

    [NativeSqlServerTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Current_capture_is_rehashed_without_rewriting_the_frozen_binding(bool sameRevision)
    {
        await using var scenario = await Scenario.CreateAsync(fixture.ConnectionString);
        await scenario.BaselineAsync();
        var run = await scenario.Verification.StartAsync(scenario.RootId, true, default);
        await using var before = scenario.Factory.CreateDbContext();
        var frozen = await before.SourceVerificationItems.Where(x => x.RunId == run).ToDictionaryAsync(x => x.Id, x => x.BindingJson);
        await File.WriteAllTextAsync(Path.Combine(scenario.Sources, "one.txt"), new string(sameRevision ? 'a' : 'c', 64));
        await RunOrdinaryChangesAsync(scenario);
        await RunVerificationAsync(scenario);
        var status = (await scenario.Verification.ReadAsync(scenario.RootId, default))!;
        Assert.Equal("Verified", status.Run!.State);
        Assert.Equal(sameRevision ? 2 : 1, status.Run.Verified);
        Assert.Equal(sameRevision ? 0 : 1, status.Run.Resolved);
        Assert.Equal(160, status.Run.SourceBytesRead);
        Assert.Equal(160, status.Run.RetainedBytesRead);
        await using var after = scenario.Factory.CreateDbContext();
        Assert.All(await after.SourceVerificationItems.Where(x => x.RunId == run).ToArrayAsync(), item =>
        { Assert.Equal(frozen[item.Id], item.BindingJson); Assert.NotNull(item.ReceiptJson); });
    }

    [NativeSqlServerTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Authoritative_deletion_resolves_only_until_a_current_successor_reappears(bool recreate)
    {
        await using var scenario = await Scenario.CreateAsync(fixture.ConnectionString);
        await scenario.BaselineAsync();
        var run = await scenario.Verification.StartAsync(scenario.RootId, true, default);
        File.Delete(Path.Combine(scenario.Sources, "one.txt"));
        await RunOrdinaryChangesAsync(scenario);
        if (recreate)
        {
            await File.WriteAllTextAsync(Path.Combine(scenario.Sources, "one.txt"), new string('c', 64));
            await RunOrdinaryChangesAsync(scenario);
        }
        await RunVerificationAsync(scenario);
        var status = (await scenario.Verification.ReadAsync(scenario.RootId, default))!;
        Assert.Equal("Verified", status.Run!.State);
        Assert.Equal(1, status.Run.Verified);
        Assert.Equal(1, status.Run.Resolved);
        Assert.Equal(recreate ? 160 : 96, status.Run.SourceBytesRead);
        await using var db = scenario.Factory.CreateDbContext();
        Assert.Equal(recreate ? "CapturedSuccessor" : "Deleted", (await db.SourceVerificationItems.SingleAsync(x => x.RunId == run && x.State != "Verified")).State);
    }

    [NativeSqlServerTheory]
    [InlineData("legacy")]
    [InlineData("pre-cycle")]
    [InlineData("superseded")]
    public async Task Nonqualifying_suppression_never_becomes_a_verified_deletion(string evidence)
    {
        await using var scenario = await Scenario.CreateAsync(fixture.ConnectionString);
        await scenario.BaselineAsync();
        var run = await scenario.Verification.StartAsync(scenario.RootId, true, default);
        File.Delete(Path.Combine(scenario.Sources, "one.txt"));
        await RunOrdinaryChangesAsync(scenario);
        await using (var db = scenario.Factory.CreateDbContext())
        {
            var revision = await db.SourceRevisions.SingleAsync(x => x.CanonicalPath.EndsWith("one.txt"));
            if (evidence == "pre-cycle") revision.SuppressedAtUtc = DateTimeOffset.UtcNow.AddDays(-1);
            else revision.RetentionEvidenceJson = evidence == "legacy" ? "{\"reason\":\"incremental-authoritative-absence\"}" : "{\"reason\":\"superseded-by-verified-physical-capture\"}";
            await db.SaveChangesAsync();
        }
        await RunVerificationAsync(scenario);
        var status = (await scenario.Verification.ReadAsync(scenario.RootId, default))!;
        Assert.NotEqual("Verified", status.Run!.State);
        Assert.Equal(0, status.Run.Resolved);
        await using var after = scenario.Factory.CreateDbContext();
        Assert.False(await after.SourceVerificationItems.AnyAsync(x => x.RunId == run && x.State == "Deleted"));
    }

    private static async Task RunOrdinaryChangesAsync(Scenario scenario)
    {
        await using (var db = scenario.Factory.CreateDbContext())
        {
            var point = await db.SourceVolumeJournalCheckpoints.SingleAsync();
            await scenario.Incremental.ReadVerificationBarrierAsync(point.VolumeIdentity, point.JournalId, default);
        }
        await using (var db = scenario.Factory.CreateDbContext())
            await db.SourcePendingChanges.Where(x => x.SourceRootId == scenario.RootId.Value).ExecuteUpdateAsync(set => set.SetProperty(x => x.NotBeforeUtc, DateTimeOffset.UtcNow));
        await scenario.Incremental.PumpAsync(default);
        var claim = Assert.IsType<ClaimedSourceScan>(await scenario.Control.ClaimNextReleasedAsync("ordinary-change", DateTimeOffset.UtcNow, TimeSpan.FromMinutes(10), default));
        Assert.False(claim.ScanRequest.IsVerification);
        var worker = new SourceScanWorker(new LocalSourceEnumerator(), scenario.Control, scenario.Artifacts,
            new SqlSourceActivityStore(scenario.Factory, TimeProvider.System), incrementalCoordinator: scenario.Incremental);
        await scenario.Control.CompleteAsync(claim, await worker.ScanAsync(claim.SourceRoot, claim.ScanRequest, default), null, default);
        await scenario.SettleNamespaceAsync(worker);
    }

    private static async Task RunVerificationAsync(Scenario scenario)
    {
        var claim = Assert.IsType<ClaimedSourceScan>(await scenario.Control.ClaimNextReleasedAsync("verification", DateTimeOffset.UtcNow, TimeSpan.FromMinutes(10), default));
        Assert.True(claim.ScanRequest.IsVerification);
        var worker = new SourceVerificationWorker(scenario.Verification, new WindowsSourceVerificationReaderSlot(scenario.Runtime),
            new WindowsSourceVerificationContentReader(TimeProvider.System), scenario.Artifacts, scenario.Artifacts, TimeProvider.System);
        await scenario.Control.CompleteAsync(claim, await worker.ScanAsync(claim.SourceRoot, claim.ScanRequest, default), null, default);
    }
}
