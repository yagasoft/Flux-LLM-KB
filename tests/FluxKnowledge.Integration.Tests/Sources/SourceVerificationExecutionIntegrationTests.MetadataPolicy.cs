using System.Text.Json;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using FluxKnowledge.Integrations.Files;
using FluxKnowledge.Integration.Tests.Support;
using FluxKnowledge.Cli.Commands;
using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Sources;

public sealed partial class SourceVerificationExecutionIntegrationTests
{
    [NativeSqlServerFact]
    public async Task Metadata_policy_feature_off_uses_the_current_reviewed_operator_without_loading_or_replacing_a_legacy_helper()
    {
        var start = new ProcessStartInfo("pwsh") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-File", Path.Combine(RetirementSourceRoot(), "tests/native/source-metadata-policy-operator.ps1"),
            "-SourceRoot", RetirementSourceRoot(), "-OperatorRoot", Path.GetDirectoryName(typeof(IndexProjectionCommand).Assembly.Location)! })
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try { await process.WaitForExitAsync(deadline.Token); }
        catch { if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); } throw; }
        Assert.True(process.ExitCode == 0, await error);
        using var receipt = JsonDocument.Parse(await output);
        Assert.Equal(4, receipt.RootElement.GetProperty("Passed").GetInt32());
        Assert.True(receipt.RootElement.GetProperty("ComposedRetirementBound").GetBoolean());
        Assert.Equal(0, receipt.RootElement.GetProperty("ProductionCalls").GetInt32());
    }

    [NativeSqlServerTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Metadata_policy_disabled_journal_bridge_transitions_old_enabled_and_paused_roots_without_enabling_journal(bool paused)
    {
        await using var scenario = await Scenario.CreateAsync(fixture.ConnectionString);
        await scenario.BaselineAsync();
        await using (var db = scenario.Factory.CreateDbContext())
        {
            var root = await db.SourceRootConfigurations.SingleAsync();
            root.State = (int)(paused ? SourceRootState.Paused : SourceRootState.Enabled);
            root.ReconciliationCadenceSeconds = 900;
            await db.SaveChangesAsync();
        }
        using var packet = await RetirementPacket.CreateAsync(scenario, fixture.ConnectionString);
        using (var capability = packet.Begin(() => { }, metadataPolicy: SourcePolicyDefaults.DetectionPolicy, requireFeatureOff: true))
        {
            using var result = JsonDocument.Parse(IncrementalSourceJournalRetirementBridge.Run(capability, 30));
            Assert.Equal(1, result.RootElement.GetProperty("Roots").GetInt32());
        }
        using var configuration = JsonDocument.Parse(await File.ReadAllTextAsync(packet.Configuration));
        Assert.False(configuration.RootElement.GetProperty("SourceJournal").GetProperty("Enabled").GetBoolean());
        await using var after = scenario.Factory.CreateDbContext();
        Assert.Equal(604800, (await after.SourceRootConfigurations.SingleAsync()).ReconciliationCadenceSeconds);
        Assert.Equal((int)(paused ? SourceRootState.Paused : SourceRootState.Enabled), (await after.SourceRootConfigurations.SingleAsync()).State);
        Assert.True(await after.AuditEvents.AnyAsync(x => x.EventType == "source.policy.events-weekly-metadata-v1"));
    }

    [NativeSqlServerFact]
    public async Task Metadata_policy_feature_off_binding_refuses_an_enabled_configuration_before_mutation()
    {
        await using var scenario = await Scenario.CreateAsync(fixture.ConnectionString);
        await scenario.BaselineAsync();
        using var packet = await RetirementPacket.CreateAsync(scenario, fixture.ConnectionString, metadataPolicy: true);
        var before = await RetirementSnapshotAsync(scenario);
        Assert.Equal("source-journal-retirement-feature-off-required", Assert.Throws<InvalidOperationException>(() =>
            packet.Begin(() => { }, metadataPolicy: SourcePolicyDefaults.DetectionPolicy, requireFeatureOff: true)).Message);
        Assert.Equal(before, await RetirementSnapshotAsync(scenario));
    }

    // This is historical database debt, not an operating-policy bypass. No read
    // worker is admitted; retirement tests must start from pre-release records.
    private static async Task SeedLegacyVerificationAsync(Scenario scenario)
    {
        await using var db = scenario.Factory.CreateDbContext();
        var run = Guid.NewGuid(); var request = Guid.NewGuid(); var now = DateTimeOffset.UtcNow;
        db.SourceVerificationRuns.Add(new() { Id = run, SourceRootId = scenario.RootId.Value, Active = true,
            Manual = true, State = "Pending", CreatedAtUtc = now, AuthorityJson = "{}", OriginalPolicyJson = "{\"Enabled\":true}" });
        db.SourceScanRequests.Add(new() { Id = request, SourceRootId = scenario.RootId.Value, RequestKind = 4,
            VerificationRunId = run, RequestedBy = "historical-policy-fixture", RequestedAtUtc = now,
            ReleasedAtUtc = now, IsReleased = true, State = 1 });
        db.SourceScanJobs.Add(new() { Id = Guid.NewGuid(), SourceScanRequestId = request, State = 0,
            DueAtUtc = now, CreatedAtUtc = now, UpdatedAtUtc = now });
        db.SourceScanOutbox.Add(new() { Id = Guid.NewGuid(), SourceScanRequestId = request, Operation = "source.scan",
            IdempotencyKey = "historical-verification:" + request.ToString("N"), DueAtUtc = now, CreatedAtUtc = now });
        foreach (var entry in await db.SourceInventoryEntries.Where(x => !x.IsDirectory && x.SourceRevisionId != null).ToArrayAsync())
            db.SourceVerificationItems.Add(new() { Id = Guid.NewGuid(), RunId = run, InventoryEntryId = entry.Id,
                ExpectedRevisionId = entry.SourceRevisionId, State = "Pending" });
        await db.SaveChangesAsync();
    }

    [NativeSqlServerFact]
    public async Task Metadata_policy_bridge_keeps_journal_enabled_and_current_stop_guards_run_on_owner_thread_before_commit()
    {
        await using var scenario = await Scenario.CreateAsync(fixture.ConnectionString);
        await scenario.BaselineAsync();
        using var packet = await RetirementPacket.CreateAsync(scenario, fixture.ConnectionString, metadataPolicy: true);
        var before = await RetirementSnapshotAsync(scenario);
        var owner = Environment.CurrentManagedThreadId;
        var checks = 0;
        using (var capability = packet.Begin(() =>
        {
            Assert.Equal(owner, Environment.CurrentManagedThreadId);
            if (++checks == 4) throw new IOException("fixture-new-writer-before-commit");
        }, metadataPolicy: SourcePolicyDefaults.DetectionPolicy))
            Assert.Equal("fixture-new-writer-before-commit", Assert.Throws<IOException>(() =>
                IncrementalSourceJournalRetirementBridge.Run(capability, 30)).Message);
        Assert.Equal(before, await RetirementSnapshotAsync(scenario));
        using (var capability = packet.Begin(() => { }, metadataPolicy: SourcePolicyDefaults.DetectionPolicy))
        {
            using var receipt = JsonDocument.Parse(IncrementalSourceJournalRetirementBridge.Run(capability, 30));
            Assert.Equal(SourcePolicyDefaults.DetectionPolicy, receipt.RootElement.GetProperty("Policy").GetString());
            Assert.Equal(1, receipt.RootElement.GetProperty("Roots").GetInt32());
        }
        using var configuration = JsonDocument.Parse(await File.ReadAllTextAsync(packet.Configuration));
        Assert.True(configuration.RootElement.GetProperty("SourceJournal").GetProperty("Enabled").GetBoolean());
    }

    [NativeSqlServerFact]
    public async Task Metadata_policy_transition_refuses_busy_native_slot_and_rolls_back_a_failed_final_stop_guard()
    {
        await using var scenario = await Scenario.CreateAsync(fixture.ConnectionString);
        await scenario.BaselineAsync();
        var slot = new WindowsSourceVerificationReaderSlot(scenario.Runtime);
        var before = await RetirementSnapshotAsync(scenario);
        using (var held = slot.TryAcquire())
            await Assert.ThrowsAsync<InvalidOperationException>(() => scenario.Verification.RetireForMetadataPolicyAsync(slot,
                _ => ValueTask.CompletedTask, default).AsTask());
        var guards = 0;
        await Assert.ThrowsAsync<IOException>(() => scenario.Verification.RetireForMetadataPolicyAsync(slot,
            _ => ++guards == 2 ? throw new IOException("fixture-writer-returned") : ValueTask.CompletedTask, default).AsTask());
        Assert.Equal(before, await RetirementSnapshotAsync(scenario));
        await using var db = scenario.Factory.CreateDbContext();
        Assert.False(await db.AuditEvents.AnyAsync(x => x.EventType == "source.policy.events-weekly-metadata-v1"));
    }

    [NativeSqlServerFact]
    public async Task Metadata_policy_transition_retires_only_verification_and_preserves_paused_event_debt_and_history()
    {
        await using var scenario = await Scenario.CreateAsync(fixture.ConnectionString);
        await scenario.BaselineAsync();
        var verifiedAt = DateTimeOffset.UtcNow.AddDays(-10);
        var runId = Guid.NewGuid(); var requestId = Guid.NewGuid(); var jobId = Guid.NewGuid();
        var itemId = Guid.NewGuid(); var filePendingId = Guid.NewGuid(); var rootPendingId = Guid.NewGuid();
        string eventDebt;
        await using (var seed = scenario.Factory.CreateDbContext())
        {
            var root = await seed.SourceRootConfigurations.SingleAsync();
            var state = await seed.SourceRootScanStates.SingleAsync();
            root.State = (int)SourceRootState.Paused;
            root.ReconciliationCadenceSeconds = 900;
            state.LastVerifiedAtUtc = verifiedAt;
            state.RecoveryRequired = true;
            state.Mode = "BaselineRequired";
            var inventory = await seed.SourceInventoryEntries.FirstAsync(x => !x.IsDirectory);
            seed.SourceVerificationRuns.Add(new() { Id = runId, SourceRootId = root.Id, CreatedAtUtc = verifiedAt,
                State = "Running", Active = true, Manual = true, BytesRead = 77, SourceBytesRead = 66,
                RetainedBytesRead = 11, AuthorityJson = "{}", OriginalPolicyJson = "{\"Enabled\":true}" });
            seed.SourceVerificationItems.Add(new() { Id = itemId, RunId = runId, InventoryEntryId = inventory.Id,
                ExpectedRevisionId = inventory.SourceRevisionId, State = "Verified", AttemptCount = 1,
                ReceiptJson = "{\"fixture\":\"historical-receipt\"}", ReceiptSha256 = new string('a', 64), ReceiptByteLength = 66 });
            seed.SourceScanRequests.Add(new() { Id = requestId, SourceRootId = root.Id, RequestKind = 4,
                VerificationRunId = runId, State = (int)SourceScanRequestState.Running, IsReleased = true,
                RequestedBy = "prior-policy-fixture", RequestedAtUtc = verifiedAt, ReleasedAtUtc = verifiedAt });
            seed.SourceScanJobs.Add(new() { Id = jobId, SourceScanRequestId = requestId, State = 1,
                LeaseOwner = "stopped-prior-writer", LeaseGeneration = 7, LeaseExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(10),
                DueAtUtc = verifiedAt, CreatedAtUtc = verifiedAt, UpdatedAtUtc = verifiedAt });
            var oldRoot = await seed.SourcePendingChanges.SingleOrDefaultAsync(x => x.RelativePath == ".");
            if (oldRoot is not null) seed.SourcePendingChanges.Remove(oldRoot);
            var oldFile = await seed.SourcePendingChanges.SingleOrDefaultAsync(x => x.RelativePath == "one.txt");
            if (oldFile is not null) seed.SourcePendingChanges.Remove(oldFile);
            await seed.SaveChangesAsync();
            seed.SourcePendingChanges.Add(new() { Id = rootPendingId, SourceRootId = root.Id, RelativePath = ".",
                PathFingerprint = SqlSourceChangeJournalStore.PathKey("."), IsSubtree = true, Generation = ++state.Generation,
                ReasonFlags = SourcePendingReasons.FullRecapture | SourcePendingReasons.NamespaceMetadata | SourcePendingReasons.RemovalPending,
                FirstObservedAtUtc = verifiedAt, NotBeforeUtc = verifiedAt });
            seed.SourcePendingChanges.Add(new() { Id = filePendingId, SourceRootId = root.Id, RelativePath = "one.txt",
                PathFingerprint = SqlSourceChangeJournalStore.PathKey("one.txt"), Generation = ++state.Generation, ReasonFlags = 1,
                FirstUsn = 10, LastUsn = 20, LastContentUsn = 20, LastUnjournaledGeneration = state.Generation,
                FirstObservedAtUtc = verifiedAt, NotBeforeUtc = verifiedAt });
            await seed.SaveChangesAsync();
            eventDebt = JsonSerializer.Serialize(await seed.SourcePendingChanges.AsNoTracking().SingleAsync(x => x.Id == filePendingId));
        }
        var stoppedChecks = 0;
        var result = await scenario.Verification.RetireForMetadataPolicyAsync(new WindowsSourceVerificationReaderSlot(scenario.Runtime),
            _ => { stoppedChecks++; return ValueTask.CompletedTask; }, default);
        Assert.True(stoppedChecks >= 2);
        Assert.True(scenario.Options.Enabled);
        Assert.Equal(SourcePolicyDefaults.DetectionPolicy, result.Policy);
        Assert.Equal(1, result.Roots);
        Assert.Equal(new(1, 1, 1), result.Verification);
        await using (var after = scenario.Factory.CreateDbContext())
        {
            var root = await after.SourceRootConfigurations.SingleAsync();
            Assert.Equal((int)SourceRootState.Paused, root.State);
            Assert.Equal(604800, root.ReconciliationCadenceSeconds);
            Assert.Equal(verifiedAt, (await after.SourceRootScanStates.SingleAsync()).LastVerifiedAtUtc);
            Assert.Equal(eventDebt, JsonSerializer.Serialize(await after.SourcePendingChanges.AsNoTracking().SingleAsync(x => x.Id == filePendingId)));
            Assert.Equal(0, (await after.SourcePendingChanges.SingleAsync(x => x.Id == rootPendingId)).ReasonFlags & SourcePendingReasons.FullRecapture);
            var run = await after.SourceVerificationRuns.SingleAsync(x => x.Id == runId);
            Assert.False(run.Active); Assert.Equal("Invalidated", run.State);
            Assert.Equal(SqlSourceVerificationStore.PolicyRetiredReason, run.Reason);
            Assert.Equal(77, run.BytesRead); Assert.Equal(66, run.SourceBytesRead); Assert.Equal(11, run.RetainedBytesRead);
            Assert.Equal("{\"fixture\":\"historical-receipt\"}", (await after.SourceVerificationItems.SingleAsync(x => x.Id == itemId)).ReceiptJson);
            var job = await after.SourceScanJobs.SingleAsync(x => x.Id == jobId);
            Assert.Equal((int)SourceScanJobState.Failed, job.State); Assert.Null(job.LeaseOwner); Assert.Null(job.LeaseExpiresAtUtc);
            Assert.True(job.LeaseGeneration > 7);
            Assert.True(await after.AuditEvents.AnyAsync(x => x.SourceRootId == root.Id && x.EventType == "source.policy.events-weekly-metadata-v1"));
        }
        var again = await scenario.Verification.RetireForMetadataPolicyAsync(new WindowsSourceVerificationReaderSlot(scenario.Runtime),
            _ => ValueTask.CompletedTask, default);
        Assert.Equal(0, again.Roots);
        Assert.Equal(new(0, 0, 0), again.Verification);
    }
}
