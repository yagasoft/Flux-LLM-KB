using System.Text.Json;
using System.Text.Json.Nodes;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Integrations.Files;
using FluxKnowledge.Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Sources;

public sealed partial class SourceVerificationExecutionIntegrationTests
{
    [NativeSqlServerFact]
    public async Task A_pending_item_with_a_removal_hold_is_not_read_or_turned_into_content_debt()
    {
        await using var scenario = await Scenario.CreateAsync(fixture.ConnectionString);
        await scenario.BaselineAsync();
        await new SqlSourceRemovalReconciler(scenario.Factory, TimeProvider.System, new WindowsSourceObservationReader()).ReconcileAsync(default);
        var run = await scenario.Verification.StartAsync(scenario.RootId, true, default);
        Guid pendingId;
        long generation;
        await using (var db = scenario.Factory.CreateDbContext())
        {
            Assert.Equal("Pending", await db.SourceVerificationItems.Where(x => x.RunId == run && x.BindingJson.Contains("one.txt")).Select(x => x.State).SingleAsync());
            var state = await db.SourceRootScanStates.SingleAsync();
            generation = ++state.Generation;
            pendingId = Guid.NewGuid();
            db.SourcePendingChanges.Add(new() { Id = pendingId, SourceRootId = scenario.RootId.Value,
                RelativePath = "one.txt", PathFingerprint = SqlSourceChangeJournalStore.PathKey("one.txt"),
                Generation = generation, ReasonFlags = SourcePendingReasons.RemovalPending, NotBeforeUtc = DateTimeOffset.UtcNow.AddHours(1) });
            await db.SaveChangesAsync();
        }
        var claim = Assert.IsType<ClaimedSourceScan>(await scenario.Control.ClaimNextReleasedAsync("pending-removal", DateTimeOffset.UtcNow, TimeSpan.FromMinutes(10), default));
        var batch = Assert.IsType<SourceVerificationBatch>(await scenario.Verification.PrepareAsync(claim.SourceRoot, claim.ScanRequest, default));
        Assert.DoesNotContain(batch.Items, x => x.Binding.RelativePath == "one.txt");
        Assert.Contains(batch.Items, x => x.Binding.RelativePath == "two.txt");
        await using var verify = scenario.Factory.CreateDbContext();
        var pending = await verify.SourcePendingChanges.SingleAsync(x => x.Id == pendingId);
        Assert.Equal(SourcePendingReasons.RemovalPending, pending.ReasonFlags);
        Assert.Equal(generation, pending.Generation);
        Assert.Equal("AwaitingCapture", await verify.SourceVerificationItems.Where(x => x.RunId == run && x.BindingJson.Contains("one.txt")).Select(x => x.State).SingleAsync());
    }

    [NativeSqlServerTheory]
    [InlineData("valid")]
    [InlineData("root")]
    [InlineData("path")]
    [InlineData("inventory")]
    [InlineData("version")]
    [InlineData("removal-debt")]
    [InlineData("recapture-debt")]
    public async Task Guarded_absence_resolves_only_its_exact_authority_and_binding_without_covering_debt(string change)
    {
        await using var scenario = await Scenario.CreateAsync(fixture.ConnectionString);
        await scenario.BaselineAsync();
        await scenario.Verification.StartAsync(scenario.RootId, true, default);
        File.Delete(Path.Combine(scenario.Sources, "one.txt"));
        await using (var db = scenario.Factory.CreateDbContext())
        {
            var state = await db.SourceRootScanStates.SingleAsync();
            var key = SqlSourceChangeJournalStore.PathKey("one.txt");
            var pending = await db.SourcePendingChanges.SingleOrDefaultAsync(x => x.PathFingerprint == key);
            if (pending is null)
            {
                pending = new() { Id = Guid.NewGuid(), SourceRootId = scenario.RootId.Value, RelativePath = "one.txt", PathFingerprint = key };
                db.SourcePendingChanges.Add(pending);
            }
            pending.ReasonFlags |= SourcePendingReasons.RemovalPending;
            pending.Generation = ++state.Generation;
            await db.SaveChangesAsync();
        }
        Assert.Equal(1, await new SqlSourceRemovalReconciler(scenario.Factory, TimeProvider.System, new WindowsSourceObservationReader()).ReconcileAsync(default));
        await RunOrdinaryChangesAsync(scenario);
        await using (var db = scenario.Factory.CreateDbContext())
        {
            var revision = await db.SourceRevisions.SingleAsync(x => x.CanonicalPath.EndsWith("one.txt"));
            var witness = JsonSerializer.Deserialize<SourceGuardedRemovalWitness>(revision.RetentionEvidenceJson!)!;
            Assert.Equal(2, witness.Version);
            if (change is "removal-debt" or "recapture-debt")
            {
                var state = await db.SourceRootScanStates.SingleAsync();
                var generation = ++state.Generation;
                db.SourcePendingChanges.Add(new() { Id = Guid.NewGuid(), SourceRootId = scenario.RootId.Value,
                    RelativePath = "one.txt", PathFingerprint = SqlSourceChangeJournalStore.PathKey("one.txt"), Generation = generation,
                    RetryGeneration = generation, NotBeforeUtc = DateTimeOffset.UtcNow.AddHours(1),
                    ReasonFlags = change == "removal-debt" ? SourcePendingReasons.RemovalPending : SourcePendingReasons.FullRecapture });
            }
            else if (change != "valid")
            {
                var json = JsonNode.Parse(revision.RetentionEvidenceJson!)!;
                if (change == "root") json["PhysicalIdentityFingerprint"] = new string('0', 64);
                if (change == "path") json["RelativePath"] = "another.txt";
                if (change == "inventory") json["InventoryEntryId"] = Guid.NewGuid();
                if (change == "version") json["Version"] = 1;
                revision.RetentionEvidenceJson = json.ToJsonString();
            }
            await db.SaveChangesAsync();
        }
        await RunVerificationAsync(scenario);
        var status = (await scenario.Verification.ReadAsync(scenario.RootId, default))!;
        if (change == "valid") { Assert.Equal("Verified", status.Run!.State); Assert.Equal(1, status.Run.Resolved); }
        else { Assert.NotEqual("Verified", status.Run!.State); Assert.Equal(0, status.Run.Resolved); }
    }
}
