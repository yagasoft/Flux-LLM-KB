using FluxKnowledge.Application.Contracts;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Sources;

public sealed partial class SourceVerificationExecutionIntegrationTests
{
    [NativeSqlServerFact]
    public async Task Verification_claim_has_explicit_routing_and_preserves_ordinary_scan_start()
    {
        await using var scenario = await Scenario.CreateAsync(fixture.ConnectionString);
        await scenario.BaselineAsync();
        var run = await scenario.Verification.StartAsync(scenario.RootId, true, default);
        await using var before = scenario.Factory.CreateDbContext();
        var started = (await before.SourceRootConfigurations.AsNoTracking().SingleAsync()).LastScanStartedAtUtc;
        var claim = Assert.IsType<ClaimedSourceScan>(await scenario.Control.ClaimNextReleasedAsync("verification", DateTimeOffset.UtcNow, TimeSpan.FromMinutes(10), default));
        await using var after = scenario.Factory.CreateDbContext();
        Assert.Equal(started, (await after.SourceRootConfigurations.SingleAsync()).LastScanStartedAtUtc);
        Assert.True(claim.ScanRequest.IsVerification);
        Assert.Equal(run, claim.ScanRequest.VerificationRunId);
    }

    [NativeSqlServerFact]
    public async Task Verification_completion_preserves_scan_evidence_and_rejects_complete_inventory_flags()
    {
        await using var scenario = await Scenario.CreateAsync(fixture.ConnectionString);
        await scenario.BaselineAsync();
        await scenario.Verification.StartAsync(scenario.RootId, true, default);
        var claim = Assert.IsType<ClaimedSourceScan>(await scenario.Control.ClaimNextReleasedAsync("verification", DateTimeOffset.UtcNow, TimeSpan.FromMinutes(10), default));
        var accidentalInventory = new SourceScanResult(scenario.RootId, claim.ScanRequest.Id, 999, 999, 999, 999);
        await Assert.ThrowsAsync<InvalidOperationException>(() => scenario.Control.CompleteAsync(claim, accidentalInventory, null, default).AsTask());
        await using var before = scenario.Factory.CreateDbContext();
        var root = await before.SourceRootConfigurations.AsNoTracking().SingleAsync();
        await scenario.Control.CompleteAsync(claim, accidentalInventory with { IsCompleteInventory = false }, null, default);
        await using var after = scenario.Factory.CreateDbContext();
        var current = await after.SourceRootConfigurations.SingleAsync();
        Assert.Equal(root.LastScanStartedAtUtc, current.LastScanStartedAtUtc);
        Assert.Equal(root.LastScanCompletedAtUtc, current.LastScanCompletedAtUtc);
        Assert.Equal(root.LastScanEvidenceJson, current.LastScanEvidenceJson);
        Assert.Equal(root.UpdatedAtUtc, current.UpdatedAtUtc);
        var request = await after.SourceScanRequests.SingleAsync(x => x.Id == claim.ScanRequest.Id.Value);
        Assert.Equal(0, request.DiscoveredFileCount + request.IndexedFileCount + request.DeferredFileCount + request.BlockedFileCount);
    }

    [NativeSqlServerFact]
    public async Task Fallback_watcher_cannot_merge_changes_into_a_verification_request()
    {
        await using var scenario = await Scenario.CreateAsync(fixture.ConnectionString);
        await scenario.BaselineAsync();
        await scenario.Verification.StartAsync(scenario.RootId, true, default);
        var clock = new WatchClock();
        var watcher = new SqlSourceRootWatchStore(scenario.Factory, clock, new() { Enabled = false });
        await watcher.RecordSignalAsync(new(scenario.RootId, SourceWatchSignalKind.Changed, clock.Now, [new("one.txt")]), default);
        clock.Now += TimeSpan.FromSeconds(3);
        var batch = Assert.IsType<ClaimedSourceWatchBatch>(await watcher.ClaimDueBatchAsync(clock.Now, "verification-watch", TimeSpan.FromMinutes(1), default));
        await watcher.ReleaseScanAsync(batch, default);
        await using var db = scenario.Factory.CreateDbContext();
        Assert.True(await db.SourceScanRequests.AnyAsync(x => x.RequestKind == 2));
        Assert.Equal(1, await db.SourceScanRequests.CountAsync(x => x.RequestKind == SqlSourceVerificationStore.RequestKind));
    }

    [NativeSqlServerFact]
    public async Task Pending_verification_does_not_suppress_or_outrank_due_authority_reconciliation()
    {
        await using var scenario = await Scenario.CreateAsync(fixture.ConnectionString);
        await scenario.BaselineAsync();
        await scenario.Verification.StartAsync(scenario.RootId, true, default);
        await using (var db = scenario.Factory.CreateDbContext())
        {
            var root = await db.SourceRootConfigurations.SingleAsync();
            root.LastScanCompletedAtUtc = DateTimeOffset.UtcNow.AddHours(-1);
            await db.SaveChangesAsync();
        }
        var claim = Assert.IsType<ClaimedSourceScan>(await scenario.Control.ClaimNextReleasedAsync("foreground", DateTimeOffset.UtcNow, TimeSpan.FromMinutes(10), default));
        Assert.Equal("reconciliation", claim.ScanRequest.RequestedBy);
        Assert.False(claim.ScanRequest.IsVerification);
        Assert.Null(await scenario.Control.ClaimNextReleasedAsync("competing-verification", DateTimeOffset.UtcNow, TimeSpan.FromMinutes(10), default));
    }

    private sealed class WatchClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
