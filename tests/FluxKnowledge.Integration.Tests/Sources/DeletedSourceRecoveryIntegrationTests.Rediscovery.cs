using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Sources;
using FluxKnowledge.Application.Workers;
using FluxKnowledge.Domain.Jobs;
using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Integration.Tests.Support;
using FluxKnowledge.Integrations.Files;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Sources;

public sealed partial class DeletedSourceRecoveryIntegrationTests
{
    [NativeSqlServerTheory]
    [InlineData("never-started")]
    [InlineData("signal-after-removal-hold")]
    [InlineData("signal-after-content-hold")]
    [InlineData("fallback-missed-capture")]
    [InlineData("hint-after-retention-failure")]
    [InlineData("attempted")]
    [InlineData("active")]
    [InlineData("owned-outbox")]
    [InlineData("pending-content")]
    [InlineData("changed-after-capture")]
    public async Task Same_content_rediscovery_requeues_only_the_original_never_started_extraction(string history)
    {
        var progress = new SqlSourceWorkRecoveryProgress();
        var time = new RediscoveryClock(DateTimeOffset.UtcNow);
        var hinted = history != "fallback-missed-capture";
        var recovered = history is "never-started" or "signal-after-removal-hold" or "signal-after-content-hold" or "fallback-missed-capture" or "hint-after-retention-failure";
        await using var scenario = await Scenario.CreateAsync(fixture.ConnectionString, hinted ? progress : null);
        const string name = "returning.md";
        const string content = "# Returning content\n";
        var path = Path.Combine(scenario.Files, name);
        await File.WriteAllTextAsync(path, content);
        await scenario.ScanFilesystemAsync();
        await using var db = scenario.Factory.CreateDbContext();
        var initial = await db.SourceRevisions.SingleAsync(source => source.CanonicalPath == path);
        var activities = new SqlSourceActivityStore(scenario.Factory, TimeProvider.System);
        var extraction = await activities.FindOrCreateAsync(new(new SourceRevisionId(initial.Id),
            SourceActivityKind.TextExtraction, ExecutionClass.InProcess, SourceScanWorker.TextProcessorVersion,
            initial.ContentSha256, null, null), default);
        var registration = new SqlRetainedTextRegistrationStore(scenario.Factory, TimeProvider.System,
            scenario.Retained, sourceAdmission: scenario.Admission);
        Assert.True(await registration.RegisterAsync(extraction, default));
        var activity = await db.SourceActivities.SingleAsync(row => row.Id == extraction.Id.Value);
        var job = await db.Jobs.SingleAsync(row => row.PipelineRecordId == activity.ResultingPipelineRecordId);
        var delivery = await db.OutboxMessages.SingleAsync(row => row.JobId == job.Id);
        Assert.Equal(PipelineOperations.ExtractUtf8, job.Operation);

        var observations = new WindowsSourceObservationReader();
        var removals = new SqlSourceRemovalReconciler(scenario.Factory, TimeProvider.System, observations,
            recoveryProgress: history == "signal-after-removal-hold" ? progress : null);
        var retirement = new SqlSourceWorkRetirement(scenario.Factory, TimeProvider.System);
        var recovery = new SqlSourceWorkRecovery(scenario.Factory, scenario.Admission, removals, observations,
            time, retirement, recoveryProgress: progress);
        await recovery.AdvanceAsync(default); // Consume the startup sweep with the recording still present.

        var heldRecording = Path.Combine(Path.GetDirectoryName(scenario.Files)!, "returning-outside-source.md");
        if (history == "hint-after-retention-failure") File.Move(path, heldRecording);
        else File.Delete(path);
        await scenario.PendingAsync(name, false, removalOnly: true);

        Assert.Equal(1, await removals.ReconcileAsync(default));
        Assert.Equal(1, await retirement.RetireAsync(default));
        Assert.Equal("source-removed", await db.Jobs.AsNoTracking().Where(row => row.Id == job.Id).Select(row => row.Reason).SingleAsync());
        Assert.Equal((int)SourceActivityState.CancelledSuperseded, await db.SourceActivities.AsNoTracking().Where(row => row.Id == activity.Id)
            .Select(row => row.State).SingleAsync());

        if (history == "hint-after-retention-failure") File.Move(heldRecording, path);
        else await File.WriteAllTextAsync(path, content);
        var hold = history is "signal-after-removal-hold" or "signal-after-content-hold"
            ? await scenario.PendingAsync(name, false, removalOnly: history == "signal-after-removal-hold") : (Guid?)null;
        if (history == "signal-after-content-hold" && hold is { } combinedHold)
            await db.SourcePendingChanges.Where(row => row.Id == combinedHold).ExecuteUpdateAsync(setters =>
                setters.SetProperty(row => row.ReasonFlags, SourcePendingReasons.FileEvent | SourcePendingReasons.RemovalPending));
        if (history == "signal-after-removal-hold" && hold is { } existingHold)
            await db.SourcePendingChanges.Where(row => row.Id == existingHold).ExecuteUpdateAsync(setters =>
                setters.SetProperty(row => row.NotBeforeUtc, DateTimeOffset.UtcNow.AddMinutes(10)));
        if (history == "hint-after-retention-failure")
        {
            var returned = new List<SourceDiscoveredFile>();
            await foreach (var file in new LocalSourceEnumerator().EnumerateAsync(scenario.Claim.SourceRoot, default))
                if (file.CanonicalPath == path) returned.Add(file);
            var unretained = Assert.Single(returned) with
            {
                ScanOwnership = new(scenario.Claim.ScanRequest.Id, scenario.Claim.ScanRequest.Lease!)
            };
            await scenario.Scans.ConvergeBlockedRevisionAsync(scenario.Claim.SourceRoot, unretained, "synthetic-retention-error", default);
            Assert.Null(await db.SourceRevisions.AsNoTracking().Where(source => source.Id == initial.Id)
                .Select(source => source.SuppressedAtUtc).SingleAsync());
        }
        await scenario.ScanFilesystemAsync();
        var current = await db.SourceRevisions.AsNoTracking().SingleAsync(source => source.CanonicalPath == path);
        Assert.Equal(initial.Id, current.Id);
        Assert.Null(current.SuppressedAtUtc);
        Assert.Contains("verified-physical-capture", current.CurrentDiscoveryEvidenceJson);

        var jobs = db.Jobs.Where(row => row.Id == job.Id);
        if (history == "attempted")
            await jobs.ExecuteUpdateAsync(set => set.SetProperty(row => row.AttemptCount, 1));
        if (history == "active")
            await jobs.ExecuteUpdateAsync(set => set.SetProperty(row => row.PublicState, (int)PublicJobState.WorkerProcessing)
                .SetProperty(row => row.LeaseOwner, "still-running").SetProperty(row => row.LeaseGeneration, 1)
                .SetProperty(row => row.LeaseExpiresAtUtc, DateTimeOffset.UtcNow.AddMinutes(3)));
        if (history == "owned-outbox")
            await db.OutboxMessages.Where(row => row.Id == delivery.Id).ExecuteUpdateAsync(set => set
                .SetProperty(row => row.LeaseGeneration, 1));
        if (history == "pending-content") await scenario.PendingAsync(name, false);
        if (history == "changed-after-capture") await File.AppendAllTextAsync(path, "Later edit.");

        time.Now = DateTimeOffset.UtcNow.AddSeconds(1);
        await recovery.AdvanceAsync(default);
        if (history is "signal-after-removal-hold" or "signal-after-content-hold" or "fallback-missed-capture")
        {
            Assert.Equal("source-removed", (await jobs.AsNoTracking().SingleAsync()).Reason);
            if (history == "signal-after-content-hold")
            {
                Assert.Equal(SourcePendingReasons.FileEvent, await db.SourcePendingChanges.AsNoTracking()
                    .Where(row => row.Id == hold).Select(row => row.ReasonFlags).SingleAsync());
                using var artifacts = new ContentAddressedSourceArtifactStore(scenario.Retained);
                var incremental = new SqlSourceIncrementalCoordinator(scenario.Factory, time, new StartupJournal("I:"),
                    observations, artifacts, new SourceJournalOptions { Enabled = false }, progress);
                var root = scenario.Claim.SourceRoot;
                var request = scenario.Claim.ScanRequest;
                var plan = Assert.IsType<SourceIncrementalPlan>(await incremental.PrepareAsync(root, request, default));
                Assert.Contains(plan.Targets, row => row.RelativePath == name);
                var seen = (await db.SourceRevisions.AsNoTracking().Where(row => row.SourceRootId == root.Id.Value)
                    .Select(row => row.Id).ToArrayAsync()).Select(id => new SourceRevisionId(id)).ToHashSet();
                await incremental.CompleteAsync(root, request, plan, [], [], seen, null, default);
                Assert.False(await db.SourcePendingChanges.AnyAsync(row => row.Id == hold));
            }
            else if (hold is { } delayed)
            {
                await db.SourcePendingChanges.Where(row => row.Id == delayed).ExecuteUpdateAsync(setters =>
                    setters.SetProperty(row => row.NotBeforeUtc, DateTimeOffset.UtcNow.AddSeconds(-1)));
            }
            else time.Now = time.Now.AddMinutes(2);
            await recovery.AdvanceAsync(default);
        }
        await recovery.AdvanceAsync(default); // Existing rediscovery evidence may be replayed.
        Assert.Equal(recovered ? 1 : 0, await db.AuditEvents.CountAsync(row =>
            row.SourceRevisionId == initial.Id && row.EventType == "pipeline.rediscovered_extraction_queued"));
        var restoredJob = await jobs.AsNoTracking().SingleAsync();
        var restoredActivity = await db.SourceActivities.AsNoTracking().SingleAsync(row => row.Id == activity.Id);
        var restoredDelivery = await db.OutboxMessages.AsNoTracking().SingleAsync(row => row.JobId == job.Id);
        Assert.Equal(delivery.Id, restoredDelivery.Id);
        Assert.Equal(delivery.IdempotencyKey, restoredDelivery.IdempotencyKey);
        Assert.Equal(delivery.DispatchGeneration, restoredDelivery.DispatchGeneration);

        if (!recovered)
        {
            Assert.Equal(history == "active" ? (int)PublicJobState.WorkerProcessing : (int)PublicJobState.Failed, restoredJob.PublicState);
            Assert.Equal("source-removed", restoredJob.Reason);
            Assert.Equal((int)SourceActivityState.CancelledSuperseded, restoredActivity.State);
            return;
        }

        Assert.Equal((int)PublicJobState.WorkerQueued, restoredJob.PublicState);
        Assert.Null(restoredJob.Reason);
        Assert.Equal(0, restoredJob.AttemptCount);
        Assert.Equal(0, restoredJob.LeaseGeneration);
        Assert.Equal((int)SourceActivityState.Pending, restoredActivity.State);
        Assert.Equal(restoredJob.DueAtUtc, restoredDelivery.DueAtUtc);
        var claim = await new SqlOutboxStore(scenario.Factory, sourceAdmission: scenario.Admission)
            .ClaimNextDueAsync("rediscovery-test", time.Now, TimeSpan.FromMinutes(1), [PipelineOperations.ExtractUtf8], default);
        Assert.NotNull(claim);
        Assert.Equal(delivery.Id, claim.DispatchMessageId.Value);
        var claimed = await new SqlJobClaimStore(scenario.Factory, sourceAdmission: scenario.Admission)
            .ClaimForDispatchAsync(claim, "rediscovery-test", time.Now, TimeSpan.FromMinutes(1), default);
        Assert.NotNull(claimed);
        Assert.Equal(job.Id, claimed.JobId.Value);
        Assert.Equal(1, claimed.AttemptCount);
    }

    private sealed class RediscoveryClock(DateTimeOffset now) : TimeProvider
    {
        internal DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
