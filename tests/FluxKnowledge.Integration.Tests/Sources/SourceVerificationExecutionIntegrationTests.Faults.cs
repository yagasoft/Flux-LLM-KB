using System.Text.Json;
using FluxKnowledge.Application.IntegrationV1.Corpus;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Sources;
using FluxKnowledge.Domain.Sources;
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
    public async Task Missing_or_corrupt_retained_content_is_preserved_as_debt_without_refreshing_acquisition_checksums(bool corrupt)
    {
        await using var scenario = await Scenario.CreateAsync(fixture.ConnectionString);
        await scenario.BaselineAsync();
        string blob;
        DateTimeOffset[] acquisitionTimes;
        await using (var before = scenario.Factory.CreateDbContext())
        {
            var artifact = await before.SourceArtifacts.SingleAsync(x => x.ByteLength == 64);
            blob = Path.Combine(scenario.Runtime, "..", "Retained", artifact.StoreRelativePath);
            acquisitionTimes = await before.SourceArtifacts.OrderBy(x => x.Id).Select(x => x.ChecksumVerifiedAtUtc).ToArrayAsync();
        }
        var broken = Enumerable.Repeat((byte)'z', 64).ToArray();
        if (corrupt) await File.WriteAllBytesAsync(blob, broken);
        else File.Delete(blob);
        await scenario.Verification.StartAsync(scenario.RootId, true, default);
        await RunVerificationAsync(scenario);
        var status = (await scenario.Verification.ReadAsync(scenario.RootId, default))!;
        Assert.Equal("CompletedWithIssues", status.Run!.State);
        Assert.Equal(1, status.Run.Verified);
        Assert.Equal(1, status.Run.Deferred);
        Assert.Null(status.LastVerifiedAtUtc);
        Assert.Equal(160, status.Run.SourceBytesRead);
        Assert.Equal(corrupt ? 160 : 96, status.Run.RetainedBytesRead);
        if (corrupt) Assert.Equal(broken, await File.ReadAllBytesAsync(blob));
        else Assert.False(File.Exists(blob));
        await using var after = scenario.Factory.CreateDbContext();
        Assert.Equal(acquisitionTimes, await after.SourceArtifacts.OrderBy(x => x.Id).Select(x => x.ChecksumVerifiedAtUtc).ToArrayAsync());
        Assert.Single(await after.SourceVerificationItems.Where(x => x.State == "Deferred" && x.ReceiptJson == null).ToArrayAsync());
    }

    [NativeSqlServerTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Unavailable_or_replaced_journal_cannot_start_reads_or_certify_the_cycle(bool lostHistory)
    {
        await using var scenario = await Scenario.CreateAsync(fixture.ConnectionString);
        await scenario.BaselineAsync();
        var clock = new OffsetClock();
        var journal = new VerificationFaultJournal { LostHistory = lostHistory };
        var coordinator = new SqlSourceIncrementalCoordinator(scenario.Factory, TimeProvider.System, journal,
            new WindowsSourceObservationReader(), scenario.Artifacts, scenario.Options);
        var store = new SqlSourceVerificationStore(scenario.Factory, clock, scenario.Options, coordinator);
        await store.StartAsync(scenario.RootId, true, default);
        var claim = Assert.IsType<ClaimedSourceScan>(await scenario.Control.ClaimNextReleasedAsync("journal-fault", clock.GetUtcNow(), TimeSpan.FromMinutes(10), default));
        long bytes = 0;
        var worker = new SourceVerificationWorker(store, new WindowsSourceVerificationReaderSlot(scenario.Runtime),
            new WindowsSourceVerificationContentReader(TimeProvider.System) { SourceBytesRead = n => bytes += n },
            scenario.Artifacts, scenario.Artifacts, clock);
        var failure = await Assert.ThrowsAnyAsync<IOException>(() => worker.ScanAsync(claim.SourceRoot, claim.ScanRequest, default).AsTask());
        if (lostHistory) Assert.IsType<SourceJournalGapException>(failure);
        Assert.Equal(0, bytes);
        await scenario.Control.CompleteAsync(claim, new(claim.SourceRoot.Id, claim.ScanRequest.Id, 0, 0, 0, 0) { IsCompleteInventory = false }, "fixture-journal-unavailable", default);
        var pending = (await store.ReadAsync(scenario.RootId, default))!;
        Assert.Null(pending.LastVerifiedAtUtc);
        Assert.Equal(0, pending.Run!.Verified);
        Assert.Equal(0, pending.Run.SourceBytesRead);
        Assert.Equal(0, pending.Run.RetainedBytesRead);
        if (lostHistory)
        {
            await using (var due = scenario.Factory.CreateDbContext())
                await due.SourceVolumeJournalCheckpoints.ExecuteUpdateAsync(set => set.SetProperty(x => x.DueAtUtc, (DateTimeOffset?)null));
            await coordinator.PumpAsync(default);
            await using var db = scenario.Factory.CreateDbContext();
            var state = await db.SourceRootScanStates.SingleAsync();
            Assert.True(state.RecoveryRequired);
            Assert.False(state.NamespaceComplete);
            Assert.True(await db.SourceScanRequests.AnyAsync(x => x.RequestKind != SqlSourceVerificationStore.RequestKind && x.State == (int)SourceScanRequestState.Released));
            Assert.False(await db.SourceVerificationItems.AnyAsync(x => x.ReceiptJson != null));
            await Assert.ThrowsAsync<InvalidOperationException>(() => store.StartAsync(scenario.RootId, true, default).AsTask());
        }
        else
        {
            journal.Fault = false;
            clock.Offset = TimeSpan.FromMinutes(2);
            var retry = Assert.IsType<ClaimedSourceScan>(await scenario.Control.ClaimNextReleasedAsync("journal-restored", clock.GetUtcNow(), TimeSpan.FromMinutes(10), default));
            Assert.Equal(claim.ScanRequest.Id, retry.ScanRequest.Id);
            Assert.True(retry.LeaseGeneration > claim.LeaseGeneration);
            await scenario.Control.CompleteAsync(retry, await worker.ScanAsync(retry.SourceRoot, retry.ScanRequest, default), null, default);
            var restored = (await store.ReadAsync(scenario.RootId, default))!;
            Assert.Equal(2, restored.Run!.Verified);
            Assert.Equal(160, restored.Run.SourceBytesRead);
            Assert.Equal(160, restored.Run.RetainedBytesRead);
        }
        using var free = new WindowsSourceVerificationReaderSlot(scenario.Runtime).TryAcquire();
        Assert.NotNull(free);
    }

    [NativeSqlServerTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Soft_time_and_byte_limits_finish_one_admitted_file_then_yield_before_the_next(bool largeFile)
    {
        await using var scenario = await Scenario.CreateAsync(fixture.ConnectionString);
        const int largeLength = 33 * 1024 * 1024;
        if (largeFile)
        {
            await using var setup = scenario.Factory.CreateDbContext();
            var root = await setup.SourceRootConfigurations.SingleAsync();
            root.MaximumFileBytes = 64 * 1024 * 1024;
            await setup.SaveChangesAsync();
            var payload = Enumerable.Repeat((byte)'a', largeLength).ToArray();
            await File.WriteAllBytesAsync(Path.Combine(scenario.Sources, "one.txt"), payload);
            await File.WriteAllBytesAsync(Path.Combine(scenario.Sources, "two.txt"), payload);
        }
        await scenario.BaselineAsync();
        await CommitNativeAsync(NativeCommands(scenario), new NativeCorpusMutation("source_verification_set", JsonSerializer.SerializeToElement(new
        { rootId = scenario.RootId.Value, policy = SourceVerificationPolicy.Default("UTC") with { MaximumBytesPerSecond = 128L * 1024 * 1024 } })));
        var store = scenario.Verification;
        await store.StartAsync(scenario.RootId, true, default);
        var claim = Assert.IsType<ClaimedSourceScan>(await scenario.Control.ClaimNextReleasedAsync("batch-limit", DateTimeOffset.UtcNow, TimeSpan.FromMinutes(10), default));
        var clock = new VerificationElapsedClock();
        var hooks = new CheckpointHook(store, (_, _) => Task.CompletedTask, afterCheckpoint: accepted =>
        { if (accepted && !largeFile) clock.Advance(TimeSpan.FromSeconds(31)); });
        var worker = new SourceVerificationWorker(hooks, new WindowsSourceVerificationReaderSlot(scenario.Runtime),
            new WindowsSourceVerificationContentReader(TimeProvider.System), scenario.Artifacts, scenario.Artifacts, clock);
        await scenario.Control.CompleteAsync(claim, await worker.ScanAsync(claim.SourceRoot, claim.ScanRequest, default), null, default);
        var status = (await store.ReadAsync(scenario.RootId, default))!;
        Assert.Equal("Pending", status.Run!.State);
        Assert.Equal(1, status.Run.Verified);
        Assert.Null(status.LastVerifiedAtUtc);
        Assert.Equal(status.Run.SourceBytesRead, status.Run.RetainedBytesRead);
        if (largeFile) Assert.Equal(largeLength, status.Run.SourceBytesRead);
        else Assert.Contains(status.Run.SourceBytesRead, new long[] { 64, 96 });
        Assert.True(status.Run.NextBatchAtUtc >= status.Run.CreatedAtUtc.AddSeconds(5));
        using var free = new WindowsSourceVerificationReaderSlot(scenario.Runtime).TryAcquire();
        Assert.NotNull(free);
    }

    private sealed class VerificationFaultJournal : ISourceChangeJournalReader
    {
        private readonly WindowsSourceChangeJournalReader _native = new();
        public bool Fault { get; set; } = true;
        public bool LostHistory { get; init; }
        public async ValueTask<JournalCapability> QueryAsync(string volume, CancellationToken ct)
        {
            if (Fault && !LostHistory) throw new IOException("Fixture journal helper unavailable");
            var actual = await _native.QueryAsync(volume, ct);
            return Fault ? actual with { JournalId = actual.JournalId ^ 1 } : actual;
        }
        public ValueTask<JournalPage> ReadAsync(JournalCursor cursor, int maximumBytes, CancellationToken ct) =>
            Fault ? throw new SourceJournalGapException("Fixture journal history replaced") : _native.ReadAsync(cursor, maximumBytes, ct);
    }

    private sealed class VerificationElapsedClock : TimeProvider
    {
        private long _offset;
        public void Advance(TimeSpan interval) => Interlocked.Add(ref _offset, (long)(interval.TotalSeconds * TimestampFrequency));
        public override long GetTimestamp() => base.GetTimestamp() + Volatile.Read(ref _offset);
    }
}
