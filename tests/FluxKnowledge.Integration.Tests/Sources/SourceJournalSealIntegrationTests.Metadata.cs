using FluxKnowledge.Application.Ports;
using FluxKnowledge.Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Sources;

public sealed partial class SourceJournalSealIntegrationTests
{
    [NativeSqlServerFact]
    public async Task Later_metadata_only_work_preserves_sealed_content_reuse_and_its_own_reconciliation_generation()
    {
        await using var scenario = await Scenario.CreateAsync(fixture.ConnectionString, qualified: true);
        var root = await scenario.ScanAsync("metadata-baseline");
        await scenario.DrainAsync();
        Guid revision;
        DateTimeOffset checksumTime;
        await using (var db = scenario.Factory.CreateDbContext())
        { revision = (await db.SourceRevisions.SingleAsync()).Id; checksumTime = (await db.SourceArtifacts.SingleAsync()).ChecksumVerifiedAtUtc; }
        await scenario.SignalUnknownNamespaceAsync();
        Guid pendingId;
        long priorGeneration;
        await using (var db = scenario.Factory.CreateDbContext())
        { var prior = await db.SourcePendingChanges.SingleAsync(x => x.RelativePath == "."); pendingId = prior.Id; priorGeneration = prior.Generation; }
        scenario.Clock.Now += root.ReconciliationCadence + TimeSpan.FromSeconds(1);
        SourceIncrementalPlan? frozen = null;
        scenario.BeforeReuse = async plan => { frozen = plan; await scenario.SignalUnknownNamespaceAsync(); };
        scenario.SourceBytes = scenario.ArtifactSourceBytes = scenario.RetainedBytes = 0;
        scenario.ReadTrace.Clear();
        await scenario.ScanAsync("metadata-frozen-plan");
        Assert.NotNull(frozen);
        Assert.Contains(frozen.Targets, target => target.RelativePath == "." && target.Generation == priorGeneration);
        Assert.True(scenario.SourceBytes + scenario.ArtifactSourceBytes == 0, string.Join(Environment.NewLine, scenario.ReadTrace));
        Assert.Equal(0, scenario.RetainedBytes);
        await using var verified = scenario.Factory.CreateDbContext();
        Assert.Equal(revision, (await verified.SourceRevisions.SingleAsync()).Id);
        Assert.Equal(checksumTime, (await verified.SourceArtifacts.SingleAsync()).ChecksumVerifiedAtUtc);
        var pending = Assert.Single(await verified.SourcePendingChanges.Where(x => x.RelativePath == ".").ToArrayAsync());
        Assert.Equal(SourcePendingReasons.NamespaceMetadata, pending.ReasonFlags);
        Assert.Equal(pendingId, pending.Id);
        Assert.True(pending.Generation > frozen.Generation);
        Assert.True(pending.NotBeforeUtc > scenario.Clock.Now);
        Assert.False((await verified.SourceRootScanStates.SingleAsync()).NamespaceComplete);
    }

    [NativeSqlServerTheory]
    [InlineData(1L)] // A forced content obligation merged with metadata still requires hashing.
    [InlineData(SourcePendingReasons.ScopedNamespace)]
    [InlineData(SourcePendingReasons.GitMembership)]
    public async Task Later_substantive_work_merged_with_metadata_still_refuses_reuse(long additionalReason)
    {
        await using var scenario = await Scenario.CreateAsync(fixture.ConnectionString, qualified: true);
        var root = await scenario.ScanAsync("mixed-metadata-baseline");
        await scenario.DrainAsync();
        await scenario.SignalUnknownNamespaceAsync();
        scenario.Clock.Now += root.ReconciliationCadence + TimeSpan.FromSeconds(1);
        SourceIncrementalPlan? frozen = null;
        scenario.BeforeReuse = async plan =>
        {
            frozen = plan;
            await scenario.SignalUnknownNamespaceAsync();
            // Inject the distinct durable obligation into the real metadata row after
            // claim freezing; provenance remains an explicit later unjournaled generation.
            await using var db = scenario.Factory.CreateDbContext();
            var pending = await db.SourcePendingChanges.SingleAsync(x => x.RelativePath == ".");
            pending.ReasonFlags |= additionalReason;
            if (additionalReason == 1) pending.LastUnjournaledGeneration = pending.Generation;
            await db.SaveChangesAsync();
        };
        scenario.SourceBytes = scenario.ArtifactSourceBytes = 0;
        scenario.ReadTrace.Clear();
        await scenario.ScanAsync("mixed-metadata-frozen-plan");
        Assert.NotNull(frozen);
        Assert.True(scenario.SourceBytes >= 64, "Later substantive obligations cannot be hidden by the metadata-only optimisation.");
        Assert.Contains(scenario.ReadTrace, trace => trace.Contains("current-pending-obligation", StringComparison.Ordinal));
        await using var verified = scenario.Factory.CreateDbContext();
        var remaining = await verified.SourcePendingChanges.SingleAsync(x => x.RelativePath == ".");
        Assert.True(remaining.Generation > frozen.Generation);
        Assert.Equal(additionalReason, remaining.ReasonFlags & additionalReason);
        Assert.False((await verified.SourceRootScanStates.SingleAsync()).NamespaceComplete);
    }
}
