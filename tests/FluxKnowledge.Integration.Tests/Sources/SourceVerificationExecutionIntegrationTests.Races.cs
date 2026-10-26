using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Sources;
using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using FluxKnowledge.Integrations.Files;
using FluxKnowledge.Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Xunit;
using System.Text.Json;

namespace FluxKnowledge.Integration.Tests.Sources;

public sealed partial class SourceVerificationExecutionIntegrationTests
{
    [NativeSqlServerFact]
    public async Task New_journal_generation_on_a_parked_row_preempts_even_inside_debounce()
    {
        await using var scenario = await Scenario.CreateAsync(fixture.ConnectionString);
        await scenario.BaselineAsync();
        var store = scenario.Verification;
        await store.StartAsync(scenario.RootId, true, default);
        var claim = Assert.IsType<ClaimedSourceScan>(await scenario.Control.ClaimNextReleasedAsync("verification", DateTimeOffset.UtcNow, TimeSpan.FromMinutes(10), default));
        var batch = Assert.IsType<SourceVerificationBatch>(await store.PrepareAsync(claim.SourceRoot, claim.ScanRequest, default));
        string fileId; string parentId;
        long parkedGeneration;
        await using (var db = scenario.Factory.CreateDbContext())
        {
            var entry = await db.SourceInventoryEntries.SingleAsync(x => x.RelativePath == "one.txt");
            fileId = entry.FileId; parentId = entry.ParentId;
            var state = await db.SourceRootScanStates.SingleAsync();
            parkedGeneration = ++state.Generation;
            db.SourcePendingChanges.Add(new() { Id = Guid.NewGuid(), SourceRootId = scenario.RootId.Value, RelativePath = "one.txt",
                PathFingerprint = SqlSourceChangeJournalStore.PathKey("one.txt"), ReasonFlags = 1, Generation = parkedGeneration,
                RetryGeneration = parkedGeneration, LastContentUsn = 0, LastUnjournaledGeneration = 0,
                FirstObservedAtUtc = DateTimeOffset.UtcNow, NotBeforeUtc = DateTimeOffset.UtcNow.AddMinutes(15) });
            await db.SaveChangesAsync();
        }
        Assert.True(await store.MayReadAsync(batch, default));
        // Controlled SQL intake records test coalescing; this is not a native journal measurement.
        var clock = new WatchClock { Now = DateTimeOffset.UtcNow.AddMinutes(1) };
        var intake = new SqlSourceChangeJournalStore(scenario.Factory, clock);
        var journalClaim = Assert.IsType<JournalReadClaim>(await intake.ClaimVolumeAsync(scenario.Options.ApprovedVolumes[0], clock.Now, true, default));
        await intake.AcceptAsync(journalClaim, new(journalClaim.Cursor, journalClaim.Cursor with { NextUsn = journalClaim.Cursor.NextUsn + 1 },
            [new(journalClaim.Cursor.NextUsn, fileId, parentId, "one.txt", 1, false)]), default);
        await using var after = scenario.Factory.CreateDbContext();
        var pending = await after.SourcePendingChanges.SingleAsync(x => x.RelativePath == "one.txt");
        Assert.True(pending.Generation > parkedGeneration);
        Assert.Equal(parkedGeneration, pending.RetryGeneration);
        Assert.True(pending.NotBeforeUtc > DateTimeOffset.UtcNow);
        Assert.False(await store.MayReadAsync(batch, default));
    }

    [NativeSqlServerTheory]
    [InlineData("pause")]
    [InlineData("policy")]
    [InlineData("edit")]
    public async Task Changes_after_hashing_and_before_checkpoint_cannot_complete_the_item(string change)
    {
        await using var scenario = await Scenario.CreateAsync(fixture.ConnectionString);
        await scenario.BaselineAsync();
        var inner = scenario.Verification;
        await inner.StartAsync(scenario.RootId, true, default);
        var claim = Assert.IsType<ClaimedSourceScan>(await scenario.Control.ClaimNextReleasedAsync("verification", DateTimeOffset.UtcNow, TimeSpan.FromMinutes(10), default));
        var intercepted = false;
        var store = new CheckpointHook(inner, async (batch, item) =>
        {
            if (intercepted) return;
            intercepted = true;
            if (change == "edit")
            {
                var path = Path.Combine(batch.Root.CanonicalPath, item.Binding.RelativePath);
                var stamp = File.GetLastWriteTimeUtc(path);
                await File.WriteAllBytesAsync(path, Enumerable.Repeat((byte)'z', checked((int)item.Binding.ByteLength)).ToArray());
                File.SetLastWriteTimeUtc(path, stamp);
            }
            else
            {
                await using var db = scenario.Factory.CreateDbContext();
                var state = await db.SourceRootScanStates.SingleAsync();
                if (change == "pause") state.VerificationPaused = true;
                else state.VerificationPolicyRevision++;
                await db.SaveChangesAsync();
            }
        });
        var worker = new SourceVerificationWorker(store, new WindowsSourceVerificationReaderSlot(scenario.Runtime),
            new WindowsSourceVerificationContentReader(TimeProvider.System), scenario.Artifacts, scenario.Artifacts, TimeProvider.System);
        await scenario.Control.CompleteAsync(claim, await worker.ScanAsync(claim.SourceRoot, claim.ScanRequest, default), null, default);
        var status = Assert.IsType<SourceVerificationStatus>(await inner.ReadAsync(scenario.RootId, default));
        Assert.True(intercepted);
        Assert.Equal(0, status.Run!.Verified);
        Assert.Null(status.LastVerifiedAtUtc);
        Assert.Equal("Pending", status.Run.State);
        if (change == "edit")
        {
            await using var db = scenario.Factory.CreateDbContext();
            Assert.True(await db.SourcePendingChanges.AnyAsync(x => x.SourceRootId == scenario.RootId.Value && x.LastContentUsn > 0));
        }
    }

    [NativeSqlServerTheory]
    [InlineData(SourcePendingReasons.NamespaceMetadata, false, false, true)]
    [InlineData(1L, true, false, true)]
    [InlineData(1L, false, false, false)]
    [InlineData(1L, true, true, false)]
    public async Task Parked_retry_debt_does_not_starve_healthy_roots_but_new_or_due_changes_preempt(
        long reasons, bool parked, bool due, bool shouldRead)
    {
        await using var scenario = await Scenario.CreateAsync(fixture.ConnectionString);
        await scenario.BaselineAsync();
        var store = scenario.Verification;
        await store.StartAsync(scenario.RootId, true, default);
        await using (var db = scenario.Factory.CreateDbContext())
        {
            var other = Guid.NewGuid(); var now = DateTimeOffset.UtcNow;
            db.SourceRootConfigurations.Add(new SourceRootConfigurationEntity { Id = other, CanonicalPath = $"I:\\verification-parked-fixture\\{other:N}",
                DisplayName = "Unrelated parked debt", State = (int)SourceRootState.Enabled, ConfigurationRevision = 1,
                LastScanCompletedAtUtc = now, ReconciliationCadenceSeconds = 900, CreatedAtUtc = now, UpdatedAtUtc = now });
            db.SourcePendingChanges.Add(new() { Id = Guid.NewGuid(), SourceRootId = other, RelativePath = "unavailable.txt",
                PathFingerprint = SqlSourceChangeJournalStore.PathKey("unavailable.txt"), ReasonFlags = reasons, Generation = 2,
                RetryGeneration = parked ? 2 : 1, LastContentUsn = 0, LastUnjournaledGeneration = 2,
                FirstObservedAtUtc = now, NotBeforeUtc = due ? now.AddSeconds(-1) : now.AddSeconds(20) });
            await db.SaveChangesAsync();
        }
        var claim = Assert.IsType<ClaimedSourceScan>(await scenario.Control.ClaimNextReleasedAsync("verification", DateTimeOffset.UtcNow, TimeSpan.FromMinutes(10), default));
        var worker = new SourceVerificationWorker(store, new WindowsSourceVerificationReaderSlot(scenario.Runtime),
            new WindowsSourceVerificationContentReader(TimeProvider.System), scenario.Artifacts, scenario.Artifacts, TimeProvider.System);
        await scenario.Control.CompleteAsync(claim, await worker.ScanAsync(claim.SourceRoot, claim.ScanRequest, default), null, default);
        var status = Assert.IsType<SourceVerificationStatus>(await store.ReadAsync(scenario.RootId, default));
        Assert.Equal(shouldRead ? 2 : 0, status.Run!.Verified);
        Assert.Equal(shouldRead ? 160 : 0, status.Run.SourceBytesRead);
        if (status.Run.State != (shouldRead ? "Verified" : "Pending"))
        {
            await using var diagnosis = scenario.Factory.CreateDbContext();
            var state = await diagnosis.SourceRootScanStates.AsNoTracking().SingleAsync(x => x.SourceRootId == scenario.RootId.Value);
            var pending = await diagnosis.SourcePendingChanges.AsNoTracking().Where(x => x.SourceRootId == scenario.RootId.Value)
                .Select(x => new { x.RelativePath, x.Generation, x.ReasonFlags, x.NotBeforeUtc, x.RetryGeneration }).ToArrayAsync();
            Assert.Fail(JsonSerializer.Serialize(new { Expected = shouldRead ? "Verified" : "Pending", status.Run, state.NamespaceComplete,
                state.NamespaceGeneration, state.DeferredEvidenceJson, state.Mode, pending }));
        }
    }

    private sealed class CheckpointHook(ISourceVerificationExecutionStore inner,
        Func<SourceVerificationBatch, SourceVerificationItem, Task> before, Func<Task>? beforeReadCheck = null,
        Action<bool>? afterCheckpoint = null, bool suppressFinish = false) : ISourceVerificationExecutionStore
    {
        public ValueTask<SourceVerificationBatch?> PrepareAsync(SourceRootConfiguration root, SourceScanRequest request, CancellationToken ct) => inner.PrepareAsync(root, request, ct);
        public async ValueTask<bool> MayReadAsync(SourceVerificationBatch batch, CancellationToken ct)
        {
            if (beforeReadCheck is not null) await beforeReadCheck();
            return await inner.MayReadAsync(batch, ct);
        }
        public async ValueTask<bool> CheckpointAsync(SourceVerificationBatch batch, SourceVerificationItem item, SourceVerificationDigest source,
            IRetainedFileObservationLease retained, DateTimeOffset retainedHashedAtUtc, long sourceBytesRead, long retainedBytesRead, CancellationToken ct)
        {
            await before(batch, item);
            var accepted = await inner.CheckpointAsync(batch, item, source, retained, retainedHashedAtUtc, sourceBytesRead, retainedBytesRead, ct);
            afterCheckpoint?.Invoke(accepted);
            return accepted;
        }
        public ValueTask DeferAsync(SourceVerificationBatch batch, SourceVerificationItem item, string reason, bool requestCapture, CancellationToken ct) =>
            inner.DeferAsync(batch, item, reason, requestCapture, ct);
        public ValueTask FinishBatchAsync(SourceVerificationBatch batch, long sourceBytesRead, long retainedBytesRead, string? reason, CancellationToken ct) =>
            suppressFinish ? ValueTask.CompletedTask : inner.FinishBatchAsync(batch, sourceBytesRead, retainedBytesRead, reason, ct);
    }
}
