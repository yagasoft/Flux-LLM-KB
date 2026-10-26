using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Sources;
using FluxKnowledge.Integrations.Files;
using FluxKnowledge.Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Sources;

public sealed partial class SourceVerificationExecutionIntegrationTests
{
    [NativeSqlServerFact]
    public async Task Manual_two_file_cycle_hashes_source_and_retained_bytes_and_checkpoints_real_results()
    {
        await using var scenario = await Scenario.CreateAsync(fixture.ConnectionString);
        await scenario.BaselineAsync();
        var store = scenario.Verification;
        var run = await store.StartAsync(scenario.RootId, true, default);
        await using var before = scenario.Factory.CreateDbContext();
        var ordinary = await before.SourceRootConfigurations.AsNoTracking().SingleAsync();
        var revisions = await before.SourceRevisions.OrderBy(x => x.Id).Select(x => x.Id).ToArrayAsync();
        var artifactTimes = await before.SourceArtifacts.OrderBy(x => x.Id).Select(x => x.ChecksumVerifiedAtUtc).ToArrayAsync();
        var claim = Assert.IsType<ClaimedSourceScan>(await scenario.Control.ClaimNextReleasedAsync("verification", DateTimeOffset.UtcNow, TimeSpan.FromMinutes(10), default));
        var worker = new SourceVerificationWorker(store, new WindowsSourceVerificationReaderSlot(scenario.Runtime),
            new WindowsSourceVerificationContentReader(TimeProvider.System), scenario.Artifacts, scenario.Artifacts, TimeProvider.System);
        var result = await worker.ScanAsync(claim.SourceRoot, claim.ScanRequest, default);
        await scenario.Control.CompleteAsync(claim, result, null, default);
        var status = Assert.IsType<SourceVerificationStatus>(await store.ReadAsync(scenario.RootId, default));
        Assert.Equal(run, status.Run!.Id);
        Assert.Equal("Verified", status.Run.State);
        Assert.Equal(2, status.Run.Verified);
        Assert.Equal(160, status.Run.SourceBytesRead);
        Assert.Equal(160, status.Run.RetainedBytesRead);
        Assert.NotNull(status.LastVerifiedAtUtc);
        await using var after = scenario.Factory.CreateDbContext();
        Assert.Equal(revisions, await after.SourceRevisions.OrderBy(x => x.Id).Select(x => x.Id).ToArrayAsync());
        Assert.Equal(artifactTimes, await after.SourceArtifacts.OrderBy(x => x.Id).Select(x => x.ChecksumVerifiedAtUtc).ToArrayAsync());
        var root = await after.SourceRootConfigurations.SingleAsync();
        Assert.Equal(ordinary.LastScanStartedAtUtc, root.LastScanStartedAtUtc);
        Assert.Equal(ordinary.LastScanCompletedAtUtc, root.LastScanCompletedAtUtc);
        Assert.Equal(ordinary.LastScanEvidenceJson, root.LastScanEvidenceJson);
        Assert.All(await after.SourceVerificationItems.Where(x => x.RunId == run).ToArrayAsync(), item =>
        { Assert.Equal("Verified", item.State); Assert.NotNull(item.ReceiptJson); });
        using var sourceWriter = File.Open(Path.Combine(scenario.Sources, "one.txt"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        using var freeSlot = new WindowsSourceVerificationReaderSlot(scenario.Runtime).TryAcquire();
        Assert.NotNull(freeSlot);
    }

    [NativeSqlServerFact]
    public async Task Busy_physical_slot_requeues_the_batch_without_starting_any_content_read()
    {
        await using var scenario = await Scenario.CreateAsync(fixture.ConnectionString);
        await scenario.BaselineAsync();
        var store = scenario.Verification;
        await store.StartAsync(scenario.RootId, true, default);
        var claim = Assert.IsType<ClaimedSourceScan>(await scenario.Control.ClaimNextReleasedAsync("verification", DateTimeOffset.UtcNow, TimeSpan.FromMinutes(10), default));
        using var busy = new WindowsSourceVerificationReaderSlot(scenario.Runtime).TryAcquire();
        Assert.NotNull(busy);
        var bytes = 0;
        var worker = new SourceVerificationWorker(store, new WindowsSourceVerificationReaderSlot(scenario.Runtime),
            new WindowsSourceVerificationContentReader(TimeProvider.System) { SourceBytesRead = n => bytes += n },
            scenario.Artifacts, scenario.Artifacts, TimeProvider.System);
        await scenario.Control.CompleteAsync(claim, await worker.ScanAsync(claim.SourceRoot, claim.ScanRequest, default), null, default);
        var status = Assert.IsType<SourceVerificationStatus>(await store.ReadAsync(scenario.RootId, default));
        Assert.Equal(0, bytes);
        Assert.Equal(0, status.Run!.Verified);
        Assert.Equal("reader-busy", status.Run.Reason);
        Assert.True(status.Run.NextBatchAtUtc >= status.Run.CreatedAtUtc.AddSeconds(5));
    }
}
