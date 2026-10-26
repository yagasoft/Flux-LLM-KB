using System.Security.Cryptography;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Sources;

public sealed partial class SourceJournalSealIntegrationTests
{
    [NativeSqlServerTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_new_baseline_claim_captures_existing_content_debt_and_acknowledges_only_its_frozen_generation(bool laterSignal)
    {
        await using var scenario = await Scenario.CreateAsync(fixture.ConnectionString, qualified: true);
        var first = await FailBaselineAsync(scenario);
        var original = Assert.IsType<SourceIncrementalPlan>(await scenario.PrepareAsync(first));
        await SignalDegradedAsync(scenario); // Preserve an actual prior degraded-content obligation.
        scenario.Clock.Now += TimeSpan.FromMinutes(11);
        scenario.Restart();
        var retry = await scenario.ClaimAsync("new-baseline-claim");
        var frozen = Assert.IsType<SourceIncrementalPlan>(await scenario.PrepareAsync(retry));
        Assert.NotEqual(original.ManifestId, frozen.ManifestId);
        Assert.Equal(original.ManifestId, frozen.SupersedesManifestId);
        Assert.Equal(retry.ScanRequest.Lease!.Generation, frozen.LeaseGeneration);
        var target = Assert.Single(frozen.Targets, x => x.RelativePath == "one.txt");
        Assert.True(target.LastUnjournaledGeneration > 0);
        if (laterSignal) scenario.BeforeReuse = _ => SignalDegradedAsync(scenario);
        scenario.SourceBytes = 0;
        await scenario.RunAsync(retry);
        Assert.Equal(64, scenario.SourceBytes);
        await using var db = scenario.Factory.CreateDbContext();
        var remaining = await db.SourcePendingChanges.SingleOrDefaultAsync(x => x.RelativePath == "one.txt");
        if (laterSignal) { Assert.NotNull(remaining); Assert.True(remaining.Generation > target.Generation); }
        else Assert.Null(remaining);
    }

    [NativeSqlServerFact]
    public async Task A_higher_baseline_claim_freezes_once_even_if_its_first_prepare_has_no_new_work()
    {
        await using var scenario = await Scenario.CreateAsync(fixture.ConnectionString, qualified: true);
        var first = await FailBaselineAsync(scenario);
        var old = Assert.IsType<SourceIncrementalPlan>(await scenario.PrepareAsync(first));
        scenario.Clock.Now += TimeSpan.FromMinutes(11);
        scenario.Restart();
        var retry = await scenario.ClaimAsync("fresh-baseline-freeze");
        var frozen = Assert.IsType<SourceIncrementalPlan>(await scenario.PrepareAsync(retry));
        Assert.NotEqual(old.ManifestId, frozen.ManifestId);
        Assert.Equal(retry.ScanRequest.Lease!.Generation, frozen.LeaseGeneration);
        await SignalDegradedAsync(scenario);
        var repeated = Assert.IsType<SourceIncrementalPlan>(await scenario.PrepareAsync(retry));
        Assert.Equal(frozen.ManifestId, repeated.ManifestId);
        Assert.Equal(frozen.Generation, repeated.Generation);
        Assert.Equal(frozen.Targets, repeated.Targets);
        await scenario.RunAsync(retry);
        await using var db = scenario.Factory.CreateDbContext();
        Assert.True((await db.SourcePendingChanges.SingleAsync(x => x.RelativePath == "one.txt")).Generation > frozen.Generation);
    }

    [NativeSqlServerTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Baselining_subtree_hints_preserve_real_edits_in_both_notification_orders(bool watcherFirst)
    {
        await using var scenario = await Scenario.CreateAsync(fixture.ConnectionString, qualified: true);
        var first = await scenario.ClaimAsync("baseline-notification-order");
        _ = await scenario.PrepareAsync(first);
        if (watcherFirst) { scenario.Overwrite(0, 66); await scenario.SignalAsync(subtree: true); }
        scenario.FailNextCompletion = true;
        await Assert.ThrowsAsync<SourceSnapshotChangedException>(() => scenario.RunAsync(first));
        if (!watcherFirst) { scenario.Overwrite(0, 66); await scenario.SignalAsync(subtree: true); }
        await using (var db = scenario.Factory.CreateDbContext())
        {
            var pending = await db.SourcePendingChanges.SingleAsync(x => x.RelativePath == "one.txt");
            Assert.Equal(SourcePendingReasons.ScopedNamespace, pending.ReasonFlags & SourcePendingReasons.ScopedNamespace);
            Assert.Equal(0, pending.LastUnjournaledGeneration);
            if (watcherFirst) Assert.True(pending.LastContentUsn > 0); // Real native content evidence is retained too.
            Assert.False((await db.SourceRootScanStates.SingleAsync()).NamespaceComplete);
        }
        scenario.Clock.Now += TimeSpan.FromMinutes(11); scenario.Restart();
        var retry = await scenario.ClaimAsync("baseline-order-retry");
        scenario.SourceBytes = scenario.ArtifactSourceBytes = 0;
        await scenario.RunAsync(retry);
        Assert.Equal(watcherFirst ? 0 : 64, scenario.SourceBytes);
        await using var verified = scenario.Factory.CreateDbContext();
        var current = await verified.SourceRevisions.OrderByDescending(x => x.Revision).FirstAsync(x => x.CanonicalPath == scenario.Path);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(scenario.Path))), current.ContentSha256);
    }

    [NativeSqlServerTheory]
    [InlineData("unavailable", 1L)]
    [InlineData("policy-mismatch", 1L)]
    [InlineData("not-in-progress", 1L)]
    [InlineData("unsealed", SourcePendingReasons.ScopedNamespace)]
    [InlineData("git-membership", SourcePendingReasons.ScopedNamespace)]
    public async Task Baseline_watcher_classification_keeps_degraded_unsealed_and_Git_work(string condition, long expectedReason)
    {
        var git = condition == "git-membership";
        await using var scenario = await Scenario.CreateAsync(fixture.ConnectionString, qualified: condition != "unsealed", gitTracked: git);
        var first = await FailBaselineAsync(scenario);
        await using (var db = scenario.Factory.CreateDbContext())
        {
            if (condition == "unavailable") (await db.SourceVolumeJournalCheckpoints.SingleAsync()).UnavailableSinceUtc = scenario.Clock.Now;
            var state = await db.SourceRootScanStates.SingleAsync();
            if (condition == "policy-mismatch") state.PolicyFingerprint = new string('f', 64);
            if (condition == "not-in-progress") state.RecoveryInProgress = false;
            await db.SaveChangesAsync();
        }
        await scenario.SignalAsync(subtree: true, membership: git);
        await using (var db = scenario.Factory.CreateDbContext())
        {
            var pending = await db.SourcePendingChanges.SingleAsync(x => x.RelativePath == "one.txt");
            Assert.Equal(expectedReason, pending.ReasonFlags);
            if (expectedReason == 1) Assert.True(pending.LastUnjournaledGeneration > 0);
            if (git) Assert.True(await db.SourcePendingChanges.AnyAsync(x => x.RelativePath == "." && (x.ReasonFlags & SourcePendingReasons.GitMembership) != 0));
        }
        if (condition == "unsealed")
        {
            scenario.Clock.Now += TimeSpan.FromMinutes(11); scenario.Restart();
            var retry = await scenario.ClaimAsync("unsealed-baseline-retry");
            scenario.SourceBytes = 0; await scenario.RunAsync(retry);
            Assert.Equal(64, scenario.SourceBytes);
        }
    }

    private static async Task<ClaimedSourceScan> FailBaselineAsync(Scenario scenario)
    {
        var first = await scenario.ClaimAsync("failed-baseline-fixture");
        scenario.FailNextCompletion = true;
        await Assert.ThrowsAsync<SourceSnapshotChangedException>(() => scenario.RunAsync(first));
        return first;
    }

    private static async Task SignalDegradedAsync(Scenario scenario)
    {
        // Journal admission is unavailable for this notification, then restored. A
        // healthy sealed-leaf hint would correctly be advisory and create no debt.
        var approved = scenario.Options.ApprovedVolumes;
        scenario.Options.ApprovedVolumes = [];
        try { await scenario.SignalAsync(); }
        finally { scenario.Options.ApprovedVolumes = approved; }
    }
}
