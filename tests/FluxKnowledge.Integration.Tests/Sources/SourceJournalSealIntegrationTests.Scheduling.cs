using FluxKnowledge.Application.Ports;
using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Sources;

public sealed partial class SourceJournalSealIntegrationTests
{
    [NativeSqlServerTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Journal_request_queued_first_cannot_starve_overdue_authority_reconciliation(bool gitTracked)
    {
        await using var scenario = await Scenario.CreateAsync(fixture.ConnectionString, qualified: true, gitTracked: gitTracked);
        var root = await scenario.ScanAsync("authority-baseline");
        await scenario.DrainAsync();
        for (var cycle = 0; cycle < 2; cycle++)
        {
            scenario.Clock.Now += root.ReconciliationCadence + TimeSpan.FromSeconds(1);
            scenario.Overwrite(cycle, (byte)(65 + cycle));
            await scenario.IntakeAsync(); // Dispatch gets first turn, before the recurring-request scheduler.
            await scenario.IntakeAsync(); // Let the first content debounce become due, still without a claim.
            await using (var db = scenario.Factory.CreateDbContext())
            {
                var queued = await db.SourceScanRequests.SingleAsync(x => x.State == (int)SourceScanRequestState.Released);
                Assert.Equal(3, queued.RequestKind);
            }
            SourceIncrementalPlan? frozen = null;
            scenario.BeforeReuse = plan => { frozen = plan; return Task.CompletedTask; };
            await scenario.ScanAsync("busy-authority-" + cycle);
            Assert.NotNull(frozen);
            Assert.True(frozen.IsReconciliation);
            Assert.True(frozen.IsNamespaceSweep);
            Assert.Contains(frozen.Targets, x => x.RelativePath == "." && x.IsSubtree &&
                x.ReasonFlags == SourcePendingReasons.NamespaceMetadata);
            await using var verify = scenario.Factory.CreateDbContext();
            Assert.Equal(scenario.Clock.Now, (await verify.SourceRootConfigurations.SingleAsync()).LastScanCompletedAtUtc);
            Assert.Equal(scenario.Clock.Now, (await verify.SourceRootScanStates.SingleAsync()).LastReconciliationAtUtc);
            Assert.False(await verify.SourceScanRequests.AnyAsync(x => x.State == (int)SourceScanRequestState.Running));
        }
    }

    [NativeSqlServerFact]
    public async Task Due_deadline_preserves_frozen_retry_then_promotes_the_next_fresh_manifest()
    {
        await using var scenario = await Scenario.CreateAsync(fixture.ConnectionString, qualified: true);
        var root = await scenario.ScanAsync("frozen-authority-baseline");
        await scenario.DrainAsync();
        await using (var db = scenario.Factory.CreateDbContext())
        {
            // Put the independent deadline just ahead, within this same ten-minute lease.
            (await db.SourceRootConfigurations.SingleAsync()).LastScanCompletedAtUtc =
                scenario.Clock.Now - root.ReconciliationCadence + TimeSpan.FromSeconds(30);
            await db.SaveChangesAsync();
        }
        scenario.Overwrite(0, 65);
        await scenario.IntakeAsync();
        await scenario.IntakeAsync();
        var claim = await scenario.ClaimAsync("frozen-before-due");
        var original = await scenario.PrepareAsync(claim);
        Assert.NotNull(original);
        Assert.False(original.IsReconciliation);
        scenario.Clock.Now += TimeSpan.FromSeconds(31);
        var retried = await scenario.PrepareAsync(claim);
        Assert.NotNull(retried);
        Assert.Equal(original.ManifestId, retried.ManifestId);
        Assert.False(retried.IsReconciliation);
        Assert.Equal(original.Targets, retried.Targets);
        await scenario.RunAsync(claim);

        scenario.Overwrite(1, 66);
        await scenario.IntakeAsync();
        await scenario.IntakeAsync();
        SourceIncrementalPlan? fresh = null;
        scenario.BeforeReuse = plan => { fresh = plan; return Task.CompletedTask; };
        await scenario.ScanAsync("fresh-after-due");
        Assert.NotNull(fresh);
        Assert.NotEqual(original.ManifestId, fresh.ManifestId);
        Assert.True(fresh.IsReconciliation);
        Assert.True(fresh.IsNamespaceSweep);
    }
}
