using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Sources;
using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using FluxKnowledge.Infrastructure.SqlServer.Workers;
using FluxKnowledge.Integrations.Files;
using FluxKnowledge.Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Sources;

public sealed partial class DeletedSourceRecoveryIntegrationTests
{
    [NativeSqlServerFact]
    public async Task Intake_during_a_large_removal_pass_preserves_progress_without_acknowledging_newer_work()
    {
        await using var scenario = await Scenario.CreateAsync(fixture.ConnectionString);
        await using var db = scenario.Factory.CreateDbContext();
        db.SourceRevisions.AddRange(Enumerable.Range(0, 140).Select(index => Synthetic(scenario, $"traffic-{index}.txt")));
        await db.SaveChangesAsync();
        var target = await db.SourceRevisions.AsNoTracking().OrderByDescending(x => x.Id).FirstAsync();
        var pendingId = await scenario.PendingAsync(".", true);
        var observations = new ControlledObservations { Missing = Path.GetRelativePath(scenario.Files, target.CanonicalPath) };
        var removals = new SqlSourceRemovalReconciler(scenario.Factory, TimeProvider.System, observations);
        Assert.Equal(0, await removals.ReconcileAsync(default)); // Target sorts after the first page.
        await db.SourcePendingChanges.Where(x => x.Id == pendingId).ExecuteUpdateAsync(set => set.SetProperty(x => x.Generation, x => x.Generation + 1));
        Assert.Equal(1, await removals.ReconcileAsync(default));
        Assert.NotNull((await db.SourceRevisions.AsNoTracking().SingleAsync(x => x.Id == target.Id)).SuppressedAtUtc);
        Assert.NotEqual(0, (await db.SourcePendingChanges.AsNoTracking().SingleAsync(x => x.Id == pendingId)).ReasonFlags & SourcePendingReasons.RemovalPending);
        for (var turn = 0; turn < 4; turn++) await removals.ReconcileAsync(default);
        Assert.Equal(0, (await db.SourcePendingChanges.AsNoTracking().SingleAsync(x => x.Id == pendingId)).ReasonFlags & SourcePendingReasons.RemovalPending);
    }

    [NativeSqlServerFact]
    public async Task Outbox_claim_holds_the_publication_fence_until_its_lease_is_committed()
    {
        await using var scenario = await Scenario.CreateAsync(fixture.ConnectionString);
        await using var db = scenario.Factory.CreateDbContext();
        var sourceId = await db.SourceRevisions.Where(x => x.CanonicalPath == Path.Combine(scenario.Files, "keeper.txt")).Select(x => x.Id).SingleAsync();
        var jobId = await scenario.QueueAsync(sourceId);
        var job = await db.Jobs.SingleAsync(x => x.Id == jobId);
        db.OutboxMessages.Add(new() { Id = Guid.NewGuid(), JobId = job.Id, PipelineRecordId = job.PipelineRecordId,
            SourceRevision = 1, Stage = job.Stage, Operation = job.Operation, IdempotencyKey = "outbox-removal:" + job.Id,
            DueAtUtc = job.DueAtUtc, CreatedAtUtc = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var outbox = new SqlOutboxStore(scenario.Factory, sourceAdmission: scenario.Admission)
        {
            AfterClaimRead = async ct => { entered.SetResult(); await release.Task.WaitAsync(ct); }
        };
        var claim = outbox.ClaimNextDueAsync("fenced-outbox", DateTimeOffset.UtcNow, TimeSpan.FromMinutes(1), ["embed"], default).AsTask();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var suppress = Task.Run(async () =>
        {
            await using var execution = scenario.Factory.CreateDbContext();
            await execution.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
            {
            await using var context = scenario.Factory.CreateDbContext();
            await using var transaction = await context.Database.BeginTransactionAsync();
            attempted.TrySetResult();
            await SqlPublishedPassageSelection.AcquireFenceAsync(context, default);
            (await context.SourceRevisions.SingleAsync(x => x.Id == sourceId)).SuppressedAtUtc = DateTimeOffset.UtcNow;
            await context.SaveChangesAsync();
            await transaction.CommitAsync();
            });
        });
        try
        {
            await attempted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            if (suppress.IsFaulted) await suppress;
            Assert.NotSame(suppress, await Task.WhenAny(suppress, Task.Delay(200)));
        }
        finally { release.TrySetResult(); }
        var dispatch = Assert.IsType<FluxKnowledge.Application.Workers.ClaimedDispatchMessage>(await claim);
        await suppress.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Null(await new SqlJobClaimStore(scenario.Factory, sourceAdmission: scenario.Admission).ClaimForDispatchAsync(
            dispatch, "after-suppression", DateTimeOffset.UtcNow, TimeSpan.FromMinutes(1), default));
    }

    [NativeSqlServerFact]
    public async Task Uncertain_scopes_do_not_starve_later_deleted_paths()
    {
        await using var scenario = await Scenario.CreateAsync(fixture.ConnectionString);
        await using var db = scenario.Factory.CreateDbContext();
        for (var index = 1; index <= 65; index++)
        {
            var path = $"uncertain-{index}.txt";
            db.SourceRevisions.Add(Synthetic(scenario, path));
            db.SourcePendingChanges.Add(new() { Id = Guid.Parse($"00000000-0000-0000-0000-{index:x12}"),
                SourceRootId = scenario.Claim.SourceRoot.Id.Value, RelativePath = path, PathFingerprint = SqlSourceChangeJournalStore.PathKey(path),
                Generation = 1, ReasonFlags = 1 | SourcePendingReasons.RemovalPending });
        }
        var missing = Synthetic(scenario, "deleted.txt");
        db.SourceRevisions.Add(missing);
        db.SourcePendingChanges.Add(new() { Id = Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff"),
            SourceRootId = scenario.Claim.SourceRoot.Id.Value, RelativePath = "deleted.txt", PathFingerprint = SqlSourceChangeJournalStore.PathKey("deleted.txt"),
            Generation = 1, ReasonFlags = 1 | SourcePendingReasons.RemovalPending });
        await db.SaveChangesAsync();
        var observations = new ControlledObservations { Missing = "deleted.txt", RefuseUncertain = true };
        var removals = new SqlSourceRemovalReconciler(scenario.Factory, TimeProvider.System, observations);
        for (var turn = 0; turn < 12 && !await db.SourceRevisions.AsNoTracking().AnyAsync(x => x.Id == missing.Id && x.SuppressedAtUtc != null); turn++)
            await removals.ReconcileAsync(default);
        Assert.NotNull((await db.SourceRevisions.AsNoTracking().SingleAsync(x => x.Id == missing.Id)).SuppressedAtUtc);
        Assert.Equal(65, await db.SourcePendingChanges.CountAsync(x => (x.ReasonFlags & SourcePendingReasons.RemovalPending) != 0));
    }

    [NativeSqlServerFact]
    public async Task Fresh_preparation_cannot_reuse_a_previous_partial_removal_pass()
    {
        await using var scenario = await Scenario.CreateAsync(fixture.ConnectionString);
        await using var db = scenario.Factory.CreateDbContext();
        db.SourceRevisions.AddRange(Enumerable.Range(0, 140).Select(index => Synthetic(scenario, $"present-{index}.txt")));
        await db.SaveChangesAsync();
        var pendingId = await scenario.PendingAsync(".", true);
        var target = await db.SourceRevisions.AsNoTracking().OrderBy(x => x.Id).FirstAsync();
        var observations = new ControlledObservations();
        var removals = new SqlSourceRemovalReconciler(scenario.Factory, TimeProvider.System, observations);
        await removals.ReconcileAsync(default); // First 128 paths were present; the pass is unfinished.
        var oldGeneration = await db.SourcePendingChanges.Where(x => x.Id == pendingId).Select(x => x.Generation).SingleAsync();
        observations.Missing = Path.GetRelativePath(scenario.Files, target.CanonicalPath);
        using var artifacts = new ContentAddressedSourceArtifactStore(Path.Combine(scenario.Files, "unused-retained"));
        var options = new SourceJournalOptions();
        var reader = new WindowsSourceChangeJournalReader();
        var admission = new SourceWorkAdmission();
        var recovery = new SqlSourceWorkRecovery(scenario.Factory, admission, removals, observations, TimeProvider.System);
        await recovery.AdvanceAsync(default);
        Assert.True(await db.SourcePendingChanges.Where(x => x.Id == pendingId).Select(x => x.Generation > oldGeneration).SingleAsync());
        for (var turn = 0; turn < 4 && !admission.IsAdmitted(scenario.Claim.SourceRoot.Id.Value, scenario.Claim.SourceRoot.ConfigurationRevision); turn++)
            await recovery.AdvanceAsync(default);
        Assert.NotNull((await db.SourceRevisions.AsNoTracking().SingleAsync(x => x.Id == target.Id)).SuppressedAtUtc);
        Assert.True(admission.IsAdmitted(scenario.Claim.SourceRoot.Id.Value, scenario.Claim.SourceRoot.ConfigurationRevision));
    }

    [NativeSqlServerFact]
    public async Task Standalone_command_prepares_only_its_requested_root_and_refuses_deleted_input()
    {
        await using var scenario = await Scenario.CreateAsync(fixture.ConnectionString);
        await using var db = scenario.Factory.CreateDbContext();
        var original = await db.SourceRevisions.SingleAsync(x => x.CanonicalPath == Path.Combine(scenario.Files, "keeper.txt"));
        var gate = await SourceCommandAdmission.PrepareAsync(scenario.Factory, new(original.Id), original.ContentSha256,
            Path.Combine(scenario.Files, "unused-retained"), default);
        Assert.True(gate.IsAdmitted(original.SourceRootId, scenario.Claim.SourceRoot.ConfigurationRevision));
        File.Delete(original.CanonicalPath);
        await Assert.ThrowsAsync<SourceWorkDeferredException>(() => SourceCommandAdmission.PrepareAsync(scenario.Factory, new(original.Id),
            original.ContentSha256, Path.Combine(scenario.Files, "unused-retained"), default).AsTask());
    }

    private static SourceRevisionEntity Synthetic(Scenario scenario, string path) => new()
    {
        Id = Guid.NewGuid(), SourceRootId = scenario.Claim.SourceRoot.Id.Value, OriginKind = 0,
        StableSourceIdentity = "synthetic:" + Guid.NewGuid(), Revision = 1, CanonicalPath = Path.Combine(scenario.Files, path),
        ContentSha256 = new string('a', 64), ByteLength = 1, Classification = "AcceptedUtf8Text", Extension = ".txt", DiscoveredAtUtc = DateTimeOffset.UtcNow
    };

    private sealed class ControlledObservations : ISourceObservationReader
    {
        private readonly WindowsSourceObservationReader _inner = new();
        internal string? Missing { get; set; }
        internal bool RefuseUncertain { get; init; }
        internal Exception? PresenceError { get; init; }
        public ISourceObservationLease Open(SourceRootConfiguration root, string path)
        {
            if (PresenceError is not null) throw PresenceError;
            return _inner.Open(root, path);
        }
        public IReadOnlyList<SourceNamespaceObservation> ReadRootAncestors(SourceRootConfiguration root) => _inner.ReadRootAncestors(root);
        public ISourceRootObservationLease OpenRoot(SourceRootConfiguration root) => _inner.OpenRoot(root);
        public ISourceAbsenceLease? OpenAbsence(SourceRootConfiguration root, string path)
        {
            if (RefuseUncertain && path.StartsWith("uncertain-", StringComparison.Ordinal)) throw new IOException("fixture-uncertain");
            return path == Missing ? new Absence() : null;
        }
        private sealed class Absence : ISourceAbsenceLease { public void ValidateCurrent() { } public void Dispose() { } }
    }
}
