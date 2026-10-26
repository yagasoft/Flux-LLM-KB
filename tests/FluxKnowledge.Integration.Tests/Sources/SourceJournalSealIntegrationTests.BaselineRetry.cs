using System.Security.Cryptography;
using System.Text.Json.Nodes;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Sources;

public sealed partial class SourceJournalSealIntegrationTests
{
    [NativeSqlServerTheory]
    [InlineData("unchanged")]
    [InlineData("later-edit")]
    [InlineData("qualification-lost")]
    [InlineData("new-seal-writes-disabled")]
    [InlineData("receipt-v2")]
    [InlineData("epoch-lost")]
    [InlineData("partial-namespace")]
    public async Task Failed_baseline_reuses_only_current_qualified_seals_and_still_walks_the_namespace(string change)
    {
        await using var scenario = await Scenario.CreateAsync(fixture.ConnectionString, qualified: true);
        var failed = await scenario.ClaimAsync("baseline-before-completion");
        scenario.FailNextCompletion = true;
        await Assert.ThrowsAsync<SourceSnapshotChangedException>(() => scenario.RunAsync(failed));
        long epoch, recovery;
        await using (var db = scenario.Factory.CreateDbContext())
        {
            var state = await db.SourceRootScanStates.SingleAsync();
            Assert.True(state.RecoveryRequired);
            Assert.False(state.NamespaceComplete);
            epoch = state.BaselineEpoch; recovery = state.RecoveryGeneration;
            Assert.True((await db.SourceInventoryEntries.SingleAsync(x => x.RelativePath == "one.txt")).IsSealed);
            if (change == "receipt-v2")
            {
                var entry = await db.SourceInventoryEntries.SingleAsync(x => x.RelativePath == "one.txt");
                var observation = JsonNode.Parse(entry.ObservationJson!)!;
                observation["Version"] = 2;
                entry.ObservationJson = observation.ToJsonString();
            }
            if (change == "epoch-lost")
                SqlSourceIncrementalState.InvalidateCoverage(state, "fixture-lost-journal-history", state.CoveredUsn);
            await db.SaveChangesAsync();
        }
        if (change == "later-edit") scenario.Overwrite(0, 66); // Same size, restored timestamp.
        if (change == "qualification-lost") scenario.Options.SealQualifications = [];
        if (change == "new-seal-writes-disabled") scenario.Options.SealingEnabled = false;
        if (change == "partial-namespace")
            scenario.CompletionEvidence = [new("partial", ".", "fixture-incomplete-parent") { NamespaceIncomplete = true }];
        scenario.Clock.Now += TimeSpan.FromMinutes(11);
        scenario.Restart();
        var retry = await scenario.ClaimAsync("baseline-completion-retry");
        Assert.Equal(failed.ScanRequest.Id, retry.ScanRequest.Id);
        Assert.True(retry.ScanRequest.Lease!.Generation > failed.ScanRequest.Lease!.Generation);
        var plan = Assert.IsType<SourceIncrementalPlan>(await scenario.PrepareAsync(retry));
        Assert.True(plan.IsBaseline);
        if (change == "epoch-lost")
        {
            Assert.True(plan.BaselineEpoch > epoch);
            Assert.True(plan.RecoveryGeneration > recovery);
        }
        else { Assert.Equal(epoch, plan.BaselineEpoch); Assert.Equal(recovery, plan.RecoveryGeneration); }
        scenario.SourceBytes = scenario.ArtifactSourceBytes = scenario.RetainedBytes = 0;
        await scenario.RunAsync(retry);
        var mayReuse = change is "unchanged" or "partial-namespace" or "new-seal-writes-disabled";
        Assert.Equal(mayReuse ? 0 : 64, scenario.SourceBytes);
        if (mayReuse) Assert.Equal(0, scenario.ArtifactSourceBytes + scenario.RetainedBytes);
        Assert.Equal(1, scenario.Enumeration.LastEnumeratedEntries);
        await using var verified = scenario.Factory.CreateDbContext();
        var final = await verified.SourceRootScanStates.SingleAsync();
        Assert.False(final.RecoveryRequired);
        if (change == "partial-namespace")
        {
            Assert.False(final.NamespaceComplete);
            Assert.Contains("fixture-incomplete-parent", final.DeferredEvidenceJson);
            Assert.True(await verified.SourcePendingChanges.AnyAsync(x => x.RelativePath == "."));
        }
        var current = await verified.SourceRevisions.OrderByDescending(x => x.Revision).FirstAsync(x => x.CanonicalPath == scenario.Path);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(scenario.Path))), current.ContentSha256);
    }
}
