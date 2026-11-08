using System.Text.Json;
using FluxKnowledge.Application.Contracts;
using FluxKnowledge.Application.IntegrationV1;
using FluxKnowledge.Application.IntegrationV1.Corpus;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Sources;
using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Domain.Jobs;
using FluxKnowledge.Domain.Pipeline;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using FluxKnowledge.Infrastructure.SqlServer.Visibility;
using FluxKnowledge.Integrations.Files;
using FluxKnowledge.Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Sources;

public sealed partial class DeletedSourceRecoveryIntegrationTests(NativeSqlServerFixture fixture) : IClassFixture<NativeSqlServerFixture>, IAsyncLifetime
{
    public Task InitializeAsync() => SqlTestData.ClearPhase3SourceDataAsync(fixture);
    public Task DisposeAsync() => Task.CompletedTask;

    [NativeSqlServerTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Removal_progresses_before_an_unrelated_scan_completes_and_preserves_other_pending_work(bool directory)
    {
        await using var scenario = await Scenario.CreateAsync(fixture.ConnectionString);
        if (directory) Directory.Delete(Path.Combine(scenario.Files, "removed"), true);
        else File.Delete(Path.Combine(scenario.Files, "removed", "one.txt"));
        await scenario.Scans.RecordEnumerationEvidenceAsync(scenario.Claim.ScanRequest.Id,
            [new SourceEnumerationEvidence("retention", "keeper.txt", "fixture-unrelated-capture-failure")], default);
        var pendingId = await scenario.PendingAsync(directory ? "removed" : Path.Combine("removed", "one.txt"), directory);
        var reconciler = new SqlSourceRemovalReconciler(scenario.Factory, TimeProvider.System, new WindowsSourceObservationReader());
        Assert.Equal(directory ? 2 : 1, await reconciler.ReconcileAsync(default));
        await using var db = scenario.Factory.CreateDbContext();
        Assert.Equal(directory ? 2 : 1, await db.SourceRevisions.CountAsync(x => x.SuppressedAtUtc != null));
        Assert.Null((await db.SourceRevisions.SingleAsync(x => x.CanonicalPath == Path.Combine(scenario.Files, "keeper.txt"))).SuppressedAtUtc);
        var request = await db.SourceScanRequests.SingleAsync(x => x.Id == scenario.Claim.ScanRequest.Id.Value);
        Assert.Equal((int)SourceScanRequestState.Running, request.State);
        Assert.Equal(1, request.ErrorFileCount);
        Assert.Equal(1, (await db.SourcePendingChanges.SingleAsync(x => x.Id == pendingId)).ReasonFlags);
        Assert.Equal(0, await reconciler.ReconcileAsync(default));
    }

    [NativeSqlServerFact]
    public async Task A_delayed_delete_event_does_not_suppress_a_recreated_path()
    {
        await using var scenario = await Scenario.CreateAsync(fixture.ConnectionString);
        var path = Path.Combine(scenario.Files, "removed", "one.txt");
        File.Delete(path);
        await File.WriteAllTextAsync(path, "replacement content");
        var pending = await scenario.PendingAsync(Path.Combine("removed", "one.txt"), false);
        var reconciler = new SqlSourceRemovalReconciler(scenario.Factory, TimeProvider.System, new WindowsSourceObservationReader());
        Assert.Equal(0, await reconciler.ReconcileAsync(default));
        await using var db = scenario.Factory.CreateDbContext();
        Assert.False(await db.SourceRevisions.AnyAsync(x => x.SuppressedAtUtc != null));
        Assert.Equal(1, (await db.SourcePendingChanges.SingleAsync(x => x.Id == pending)).ReasonFlags);
    }

    [NativeSqlServerFact]
    public async Task A_replaced_root_cannot_authorise_removal_of_its_retained_files()
    {
        await using var scenario = await Scenario.CreateAsync(fixture.ConnectionString);
        Directory.Move(scenario.Files, scenario.Files + "-previous");
        Directory.CreateDirectory(scenario.Files);
        var pending = await scenario.PendingAsync(".", true);
        var reconciler = new SqlSourceRemovalReconciler(scenario.Factory, TimeProvider.System, new WindowsSourceObservationReader());
        Assert.Equal(0, await reconciler.ReconcileAsync(default));
        await using var db = scenario.Factory.CreateDbContext();
        Assert.False(await db.SourceRevisions.AnyAsync(x => x.SuppressedAtUtc != null));
        Assert.NotEqual(0, (await db.SourcePendingChanges.SingleAsync(x => x.Id == pending)).ReasonFlags & SourcePendingReasons.RemovalPending);
    }

    [NativeSqlServerFact]
    public async Task Queued_work_is_held_during_removal_reconciliation_and_refused_after_suppression()
    {
        await using var scenario = await Scenario.CreateAsync(fixture.ConnectionString);
        Guid sourceId;
        await using (var db = scenario.Factory.CreateDbContext())
            sourceId = await db.SourceRevisions.Where(x => x.CanonicalPath == Path.Combine(scenario.Files, "removed", "one.txt")).Select(x => x.Id).SingleAsync();
        await scenario.QueueAsync(sourceId);
        File.Delete(Path.Combine(scenario.Files, "removed", "one.txt"));
        await scenario.PendingAsync(Path.Combine("removed", "one.txt"), false);
        var claims = new SqlJobClaimStore(scenario.Factory, sourceAdmission: scenario.Admission);
        Assert.Null(await claims.ClaimWorkerAsync("before-removal", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(1), default));
        await new SqlSourceRemovalReconciler(scenario.Factory, TimeProvider.System, new WindowsSourceObservationReader()).ReconcileAsync(default);
        Assert.Null(await claims.ClaimWorkerAsync("after-removal", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(1), default));
    }

    [NativeSqlServerFact]
    public async Task A_watcher_delete_routes_removal_without_waiting_for_the_running_scan()
    {
        await using var scenario = await Scenario.CreateAsync(fixture.ConnectionString);
        File.Delete(Path.Combine(scenario.Files, "removed", "one.txt"));
        var watch = new SqlSourceRootWatchStore(scenario.Factory, TimeProvider.System);
        await watch.RecordSignalAsync(new(scenario.Claim.SourceRoot.Id, SourceWatchSignalKind.Deleted, DateTimeOffset.UtcNow.AddMinutes(-1),
            [new(Path.Combine("removed", "one.txt"))]), default);
        var batch = Assert.IsType<ClaimedSourceWatchBatch>(await watch.ClaimDueBatchAsync(DateTimeOffset.UtcNow, "removal-watch", TimeSpan.FromMinutes(1), default));
        await watch.ReleaseScanAsync(batch, default);
        Assert.Equal(1, await new SqlSourceRemovalReconciler(scenario.Factory, TimeProvider.System, new WindowsSourceObservationReader()).ReconcileAsync(default));
        await using var db = scenario.Factory.CreateDbContext();
        Assert.Equal((int)SourceScanRequestState.Running, (await db.SourceScanRequests.SingleAsync(x => x.Id == scenario.Claim.ScanRequest.Id.Value)).State);
    }

    [NativeSqlServerFact]
    public async Task Retiring_never_admitted_obsolete_work_preserves_cleanup_and_dispatch_evidence()
    {
        await using var scenario = await Scenario.CreateAsync(fixture.ConnectionString);
        Guid sourceId;
        await using (var db = scenario.Factory.CreateDbContext()) sourceId = await db.SourceRevisions.Where(x => x.CanonicalPath == Path.Combine(scenario.Files, "keeper.txt")).Select(x => x.Id).SingleAsync();
        var jobId = await scenario.QueueAsync(sourceId);
        await using (var db = scenario.Factory.CreateDbContext())
        {
            (await db.SourceRevisions.SingleAsync(x => x.Id == sourceId)).SuppressedAtUtc = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
        }
        Assert.Equal(1, await new SqlSourceWorkRetirement(scenario.Factory, TimeProvider.System).RetireAsync(default));
        await using var verify = scenario.Factory.CreateDbContext();
        var job = await verify.Jobs.SingleAsync(x => x.Id == jobId);
        Assert.Equal((int)PublicJobState.Failed, job.PublicState);
        Assert.Equal("source-removed", job.Reason);
        Assert.Empty(await verify.GpuBatches.ToArrayAsync());
        Assert.Empty(await verify.EmbeddingGpuRequests.ToArrayAsync());
    }

    [NativeSqlServerFact]
    public async Task An_omitted_or_closed_startup_gate_refuses_source_work_until_that_configuration_is_admitted()
    {
        await using var scenario = await Scenario.CreateAsync(fixture.ConnectionString);
        Guid sourceId;
        await using (var db = scenario.Factory.CreateDbContext()) sourceId = await db.SourceRevisions.Where(x => x.CanonicalPath == Path.Combine(scenario.Files, "keeper.txt")).Select(x => x.Id).SingleAsync();
        var job = await scenario.QueueAsync(sourceId);
        Assert.Null(await new SqlJobClaimStore(scenario.Factory).ClaimWorkerAsync("missing-gate", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(1), default));
        var admission = new SourceWorkAdmission();
        var claims = new SqlJobClaimStore(scenario.Factory, sourceAdmission: admission);
        Assert.Null(await claims.ClaimWorkerAsync("closed-gate", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(1), default));
        admission.Admit(scenario.Claim.SourceRoot.Id.Value, scenario.Claim.SourceRoot.ConfigurationRevision - 1);
        Assert.Null(await claims.ClaimWorkerAsync("old-configuration", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(1), default));
        admission.Admit(scenario.Claim.SourceRoot.Id.Value, scenario.Claim.SourceRoot.ConfigurationRevision);
        Assert.Equal(job, (await claims.ClaimWorkerAsync("admitted", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(1), default))?.Id.Value);
    }

    [NativeSqlServerTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Startup_checks_offline_deletions_before_resuming_the_sources_saved_queue(bool journal)
    {
        await using var scenario = await Scenario.CreateAsync(fixture.ConnectionString);
        Guid removedId, keeperId;
        await using (var db = scenario.Factory.CreateDbContext())
        {
            removedId = await db.SourceRevisions.Where(x => x.CanonicalPath == Path.Combine(scenario.Files, "removed", "one.txt")).Select(x => x.Id).SingleAsync();
            keeperId = await db.SourceRevisions.Where(x => x.CanonicalPath == Path.Combine(scenario.Files, "keeper.txt")).Select(x => x.Id).SingleAsync();
        }
        await scenario.QueueAsync(removedId);
        var keeperJob = await scenario.QueueAsync(keeperId);
        File.Delete(Path.Combine(scenario.Files, "removed", "one.txt")); // No running-process notification.
        var volume = WindowsSourceChangeJournalReader.CanonicalVolume(scenario.Files);
        var reader = new StartupJournal(volume);
        var options = new SourceJournalOptions { Enabled = journal, ApprovedVolumes = [volume] };
        if (journal)
        {
            await using var db = scenario.Factory.CreateDbContext();
            var root = await db.SourceRootConfigurations.SingleAsync();
            db.SourceRootScanStates.Add(new() { SourceRootId = root.Id, VolumeIdentity = volume,
                Mode = "Journal", BaselineEpoch = 1, NamespaceComplete = true, CoveredUsn = 100, PolicyFingerprint = SqlRepositoryWorkRecovery.Policy(root) });
            db.SourceVolumeJournalCheckpoints.Add(new() { VolumeIdentity = volume, JournalId = "0000000000000001", NextUsn = 100 });
            db.SourceInventoryEntries.Add(new() { Id = Guid.NewGuid(), SourceRootId = root.Id, RelativePath = "removed", PathFingerprint = SqlSourceChangeJournalStore.PathKey("removed"),
                FileId = "0000000000000007", IsDirectory = true, BaselineEpoch = 1 });
            await db.SaveChangesAsync();
        }
        var admission = new SourceWorkAdmission();
        var observations = new WindowsSourceObservationReader();
        using var artifacts = new ContentAddressedSourceArtifactStore(Path.Combine(scenario.Files, "unused-retained"));
        var coordinator = new SqlSourceIncrementalCoordinator(scenario.Factory, TimeProvider.System, reader, observations, artifacts, options);
        var recovery = new SqlSourceWorkRecovery(scenario.Factory, admission,
            new SqlSourceRemovalReconciler(scenario.Factory, TimeProvider.System, observations), observations, TimeProvider.System);
        var claims = new SqlJobClaimStore(scenario.Factory, sourceAdmission: admission);
        Assert.Null(await claims.ClaimWorkerAsync("before-startup", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(1), default));
        await recovery.AdvanceAsync(default);
        Assert.True(admission.IsAdmitted(scenario.Claim.SourceRoot.Id.Value, scenario.Claim.SourceRoot.ConfigurationRevision));
        Assert.Equal(keeperJob, (await claims.ClaimWorkerAsync("after-startup", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(1), default))?.Id.Value);
        await using var verify = scenario.Factory.CreateDbContext();
        Assert.NotNull((await verify.SourceRevisions.SingleAsync(x => x.Id == removedId)).SuppressedAtUtc);
        Assert.Null((await verify.SourceRevisions.SingleAsync(x => x.Id == keeperId)).SuppressedAtUtc);
        // Startup authorises known paths using current root/absence guards. Journal intake
        // is independent and must not claim cursor progress on behalf of that check.
        if (journal) Assert.Equal(100, (await verify.SourceVolumeJournalCheckpoints.SingleAsync()).NextUsn);
    }

    [NativeSqlServerTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Derived_archive_work_obeys_each_owner_but_an_originals_parent_remains_history(bool historicalParent)
    {
        await using var scenario = await Scenario.CreateAsync(fixture.ConnectionString);
        Guid input;
        await using (var db = scenario.Factory.CreateDbContext())
        {
            var original = await db.SourceRevisions.SingleAsync(x => x.CanonicalPath == Path.Combine(scenario.Files, "keeper.txt"));
            var member = new SourceRevisionEntity { Id = Guid.NewGuid(), SourceRootId = original.SourceRootId, OriginKind = 1,
                ParentSourceRevisionId = original.Id, StableSourceIdentity = "archive:" + Guid.NewGuid(), Revision = 1,
                CanonicalPath = original.CanonicalPath + "!member.docx", ContentSha256 = new string('b', 64), ByteLength = 10,
                Classification = "AcceptedUtf8Text", Extension = ".docx", DiscoveredAtUtc = DateTimeOffset.UtcNow };
            var child = new SourceRevisionEntity { Id = Guid.NewGuid(), SourceRootId = original.SourceRootId, OriginKind = 2,
                ParentSourceRevisionId = member.Id, StableSourceIdentity = "document:" + Guid.NewGuid(), Revision = 1,
                CanonicalPath = member.CanonicalPath + "!text", ContentSha256 = new string('c', 64), ByteLength = 10,
                Classification = "AcceptedUtf8Text", Extension = ".txt", DiscoveredAtUtc = DateTimeOffset.UtcNow };
            if (historicalParent)
            {
                member.OriginKind = 0;
                member.ParentSourceRevisionId = null;
                child.OriginKind = 0;
                child.CanonicalPath = original.CanonicalPath;
                input = child.Id;
            }
            else input = child.Id;
            member.SuppressedAtUtc = DateTimeOffset.UtcNow;
            db.SourceRevisions.AddRange(member, child);
            await db.SaveChangesAsync();
        }
        var jobId = await scenario.QueueAsync(input);
        var claimed = await new SqlJobClaimStore(scenario.Factory, sourceAdmission: scenario.Admission).ClaimWorkerAsync("owner-check", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(1), default);
        if (historicalParent) Assert.Equal(jobId, claimed?.Id.Value);
        else Assert.Null(claimed);
    }

    internal sealed class Scenario : IAsyncDisposable
    {
        private readonly string _area = Path.Combine(@"I:\Codex Workspaces\Staging\FluxKnowledge", "Removal-" + Guid.NewGuid().ToString("N"));
        internal string Files => Path.Combine(_area, "Sources");
        internal SourceJournalStoreIntegrationTests.ContextFactory Factory { get; }
        internal SqlSourceScanStore Scans { get; }
        internal SourceWorkAdmission Admission { get; } = new();
        internal ClaimedSourceScan Claim { get; private set; } = null!;
        private ContentAddressedSourceArtifactStore? _artifacts;
        private Scenario(string connection) { Factory = new(connection); Scans = new(Factory, TimeProvider.System); }
        internal static async Task<Scenario> CreateAsync(string connection)
        {
            var result = new Scenario(connection);
            Directory.CreateDirectory(Path.Combine(result.Files, "removed"));
            await File.WriteAllTextAsync(Path.Combine(result.Files, "removed", "one.txt"), "first input");
            await File.WriteAllTextAsync(Path.Combine(result.Files, "removed", "two.txt"), "second input");
            await File.WriteAllTextAsync(Path.Combine(result.Files, "keeper.txt"), "retained sibling");
            var commands = new NativeCorpusCommandService(new NativeOperationService(new SqlNativeOperationStore(result.Factory, TimeProvider.System), []),
                new SqlNativeCorpusActionStore(result.Factory, new SourceRootPathPolicy(new LocalIngressOptions([result.Files])), new LocalPrivateContentDisclosure()));
            var mutation = new NativeCorpusMutation("root_create", JsonSerializer.SerializeToElement(new { path = result.Files, displayName = "Removal fixture", recursive = true, maximumFileBytes = 1024 }));
            var preview = await commands.PreviewAsync(mutation, "test", default);
            await commands.CommitAsync(mutation, preview.ConfirmationId, Guid.NewGuid().ToString("N"), "test", default);
            result.Claim = Assert.IsType<ClaimedSourceScan>(await result.Scans.ClaimNextReleasedAsync("removal", DateTimeOffset.UtcNow, TimeSpan.FromMinutes(10), default));
            result.Admission.Admit(result.Claim.SourceRoot.Id.Value, result.Claim.SourceRoot.ConfigurationRevision);
            result._artifacts = new(Path.Combine(result._area, "Retained"));
            await new SourceScanWorker(new LocalSourceEnumerator(), result.Scans, result._artifacts,
                new SqlSourceActivityStore(result.Factory, TimeProvider.System)).ScanAsync(result.Claim.SourceRoot, result.Claim.ScanRequest, default);
            return result;
        }
        internal async Task<Guid> PendingAsync(string path, bool subtree)
        {
            var id = Guid.NewGuid();
            await using var db = Factory.CreateDbContext();
            db.SourcePendingChanges.Add(new SourcePendingChangeEntity { Id = id, SourceRootId = Claim.SourceRoot.Id.Value,
                RelativePath = path, PathFingerprint = SqlSourceChangeJournalStore.PathKey(path), IsSubtree = subtree,
                Generation = 1, ReasonFlags = 1 | SourcePendingReasons.RemovalPending, LastContentUsn = 0, LastUnjournaledGeneration = 1 });
            await db.SaveChangesAsync();
            return id;
        }
        internal async Task<Guid> QueueAsync(Guid inputId)
        {
            await using var db = Factory.CreateDbContext();
            var input = await db.SourceRevisions.SingleAsync(x => x.Id == inputId);
            var identity = new SourceIdentityEntity { Id = Guid.NewGuid(), SourceKind = "retained local source", StableKey = "removal:" + Guid.NewGuid(), CreatedAtUtc = DateTimeOffset.UtcNow };
            var record = new PipelineRecordEntity { Id = Guid.NewGuid(), SourceIdentityId = identity.Id, SourceRevisionId = inputId,
                Revision = 1, ContentHash = input.ContentSha256, CurrentStage = (int)PipelineStage.Embed, RegisteredAtUtc = DateTimeOffset.UtcNow };
            record.RootLineageRecordId = record.Id;
            var job = new JobEntity { Id = Guid.NewGuid(), PipelineRecordId = record.Id, SourceRevision = 1, Stage = (int)PipelineStage.Embed,
                Operation = "embed", PublicState = (int)PublicJobState.WorkerQueued, DueAtUtc = DateTimeOffset.UtcNow };
            db.SourceIdentities.Add(identity); db.PipelineRecords.Add(record); db.Jobs.Add(job);
            await db.SaveChangesAsync();
            return job.Id;
        }
        public ValueTask DisposeAsync()
        {
            _artifacts?.Dispose();
            Directory.Delete(_area, true);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class StartupJournal(string volume) : ISourceChangeJournalReader
    {
        public ValueTask<JournalCapability> QueryAsync(string volumeIdentity, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new JournalCapability(volume, 1, 0, 200, 0, 1024 * 1024, 1024));
        public ValueTask<JournalPage> ReadAsync(JournalCursor cursor, int maximumBytes, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new JournalPage(cursor, cursor with { NextUsn = 200 },
                cursor.NextUsn < 200 ? [new JournalChange(150, "000000000000002A", "0000000000000007", "one.txt", 0x200, false)] : []));
    }
}
