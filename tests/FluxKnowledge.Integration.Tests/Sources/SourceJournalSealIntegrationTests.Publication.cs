using FluxKnowledge.Application.Contracts;
using FluxKnowledge.Application.Indexing;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Pipeline;
using FluxKnowledge.Application.Search;
using FluxKnowledge.Application.Sources;
using FluxKnowledge.Application.Workers;
using FluxKnowledge.Domain.Jobs;
using FluxKnowledge.Domain.Pipeline;
using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.SqlServer.Search;
using FluxKnowledge.Infrastructure.SqlServer.Visibility;
using FluxKnowledge.Infrastructure.Usearch;
using FluxKnowledge.Integration.Tests.Indexing;
using FluxKnowledge.Integration.Tests.Support;
using FluxKnowledge.Integrations.Files;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Sources;

public sealed partial class SourceJournalSealIntegrationTests
{
    [NativeSqlServerTheory]
    [InlineData(false, false, false, false)]
    [InlineData(true, false, false, false)]
    [InlineData(false, true, false, false)]
    [InlineData(true, true, false, false)]
    [InlineData(false, false, true, false)]
    [InlineData(true, false, true, false)]
    [InlineData(false, false, false, true)]
    [InlineData(true, false, false, true)]
    public async Task Incremental_Git_edit_publishes_current_bytes_and_keeps_last_good_until_replacement(
        bool segmented, bool revert, bool replaceFile, bool emptySuccessor) =>
        await AssertGitPublicationAsync(segmented, revert, replaceFile, emptySuccessor);

    [NativeSqlServerTheory]
    [InlineData(false, "expired")]
    [InlineData(true, "expired")]
    [InlineData(false, "pending-revert")]
    [InlineData(true, "pending-revert")]
    [InlineData(false, "pending-candidate")]
    [InlineData(true, "pending-candidate")]
    [InlineData(false, "observed-peer")]
    [InlineData(true, "observed-peer")]
    [InlineData(false, "unrelated-pending")]
    [InlineData(true, "unrelated-pending")]
    [InlineData(true, "scope-change")]
    [InlineData(false, "terminal-peer")]
    [InlineData(true, "terminal-peer")]
    [InlineData(true, "paged-revert")]
    public async Task Git_publication_revalidates_retention_and_capture_authority_at_handover(bool segmented, string boundary) =>
        await AssertGitPublicationAsync(segmented, boundary is "expired" or "pending-revert" or "paged-revert", false, false, boundary);

    [NativeSqlServerTheory]
    [InlineData(false, "reordered-terminal-peer")]
    [InlineData(true, "reordered-terminal-peer")]
    [InlineData(false, "newer-terminal-peer")]
    [InlineData(true, "newer-terminal-peer")]
    [InlineData(false, "equal-terminal-peer")]
    [InlineData(true, "equal-terminal-peer")]
    public async Task Git_capture_order_uses_observed_authority_instead_of_request_order(bool segmented, string boundary) =>
        await AssertGitPublicationAsync(segmented, false, false, false, boundary);

    private async Task AssertGitPublicationAsync(bool segmented, bool revert, bool replaceFile, bool emptySuccessor,
        string? boundary = null)
    {
        await using var scenario = await Scenario.CreateAsync(fixture.ConnectionString, qualified: true, gitTracked: true);
        Func<Task>? afterPlacement = null;
        var placements = 0;
        await using var pipeline = await SqlToUsearchRebuildTests.PipelineEnvironment.CreateAsync(
            fixture, "Unrelated published baseline.", clock: scenario.Clock, segmented: segmented,
            configureServices: services =>
            {
                services.AddScoped<IRetainedSourceReader>(_ => new SqlRetainedSourceReader(scenario.Factory, scenario.RetainedRoot));
                if (!segmented) services.AddSingleton<IDerivedIndexRecoveryStore, SqlDerivedIndexRecoveryStore>();
                var publisher = services.Last(x => x.ServiceType == typeof(IIndexGenerationPublisher));
                services.AddScoped<IIndexGenerationPublisher>(provider => new GitPublicationBoundaryPublisher(
                    (IIndexGenerationPublisher)publisher.ImplementationFactory!(provider), async () =>
                    {
                        if (afterPlacement is null) return;
                        placements++;
                        var action = afterPlacement;
                        afterPlacement = null;
                        await action();
                    }));
            });
        var registration = new SqlRetainedTextRegistrationStore(scenario.Factory, scenario.Clock,
            sourceAdmission: new FixtureSourceAdmission(scenario.Factory));
        var root = await scenario.ScanAsync("git-publication-baseline");
        await scenario.DrainAsync();
        await new SqlSourceRemovalReconciler(scenario.Factory, scenario.Clock, new WindowsSourceObservationReader()).ReconcileAsync(default);
        Assert.Equal(1, await registration.OfferUnlinkedInProcessActivitiesAsync(default));
        await pipeline.PumpAsync();
        Guid originalId;
        string originalIdentity;
        await using (var db = scenario.Factory.CreateDbContext())
        {
            var original = await db.SourceRevisions.SingleAsync(x => x.CanonicalPath == scenario.Path);
            originalId = original.Id;
            originalIdentity = original.StableSourceIdentity;
            Assert.True((await db.PipelineRecords.SingleAsync(x => x.SourceRevisionId == originalId)).CompletionCriteriaMet);
        }
        var retrieval = new CorpusRetrievalService(new SqlCorpusRetrievalReader(scenario.Factory),
            new GitPublicationEvidenceCodec(), new LocalPrivateContentDisclosure());
        Assert.Single((await retrieval.SearchAsync(new(new string('x', 64), 10, "root", root.Id.Value, null, "literal"), default)).Results);

        await using var beforeEdit = scenario.Factory.CreateDbContext();
        var stampBeforeEdit = (await beforeEdit.IndexState.AsNoTracking().SingleAsync()).CorpusVersion;
        var successorText = emptySuccessor ? string.Empty : new string('y', 64);
        if (replaceFile)
        {
            await File.WriteAllTextAsync(scenario.Path + ".tmp", successorText);
            File.Move(scenario.Path + ".tmp", scenario.Path, overwrite: true);
        }
        else await File.WriteAllTextAsync(scenario.Path, successorText);
        await scenario.IntakeAsync();
        await scenario.SignalAsync();
        await scenario.DrainAsync();
        await new SqlSourceRemovalReconciler(scenario.Factory, scenario.Clock, new WindowsSourceObservationReader()).ReconcileAsync(default);
        var offered = await registration.OfferUnlinkedInProcessActivitiesAsync(default);
        await using (var captured = scenario.Factory.CreateDbContext())
        {
            var revisions = await captured.SourceRevisions.Select(x => new { x.Id, x.ContentSha256, x.SuppressedAtUtc }).ToArrayAsync();
            var activities = await captured.SourceActivities.Select(x => new { x.SourceRevisionId, x.State, x.ProcessorVersion, x.ResultingPipelineRecordId }).ToArrayAsync();
            Assert.True(offered == 1, System.Text.Json.JsonSerializer.Serialize(new { offered, revisions, activities }));
        }
        // The last successful publication remains available while its successor is queued.
        Assert.Single((await retrieval.SearchAsync(new(new string('x', 64), 10, "root", root.Id.Value, null, "literal"), default)).Results);
        if (boundary is "pending-candidate" or "observed-peer" or "terminal-peer" or "unrelated-pending" or "scope-change" or
            "reordered-terminal-peer" or "newer-terminal-peer" or "equal-terminal-peer")
            afterPlacement = async () =>
            {
                await using var db = scenario.Factory.CreateDbContext();
                if (boundary is "pending-candidate" or "unrelated-pending")
                    await AddPendingPublicationChangeAsync(db, root.Id.Value,
                        boundary == "unrelated-pending" ? "unrelated.txt" : "one.txt", scenario.Clock.Now);
                else
                {
                    var source = await db.SourceRevisions.SingleAsync(x => x.CanonicalPath == scenario.Path && x.Id != originalId);
                    var peer = (Infrastructure.SqlServer.Persistence.Entities.SourceRevisionEntity)db.Entry(source).CurrentValues.ToObject();
                    peer.Id = Guid.NewGuid();
                    peer.Revision++;
                    peer.ContentSha256 = new string('c', 64);
                    peer.RowVersion = [];
                    if (boundary == "observed-peer" || boundary.EndsWith("terminal-peer", StringComparison.Ordinal))
                    {
                        var observation = System.Text.Json.Nodes.JsonNode.Parse(source.CurrentDiscoveryEvidenceJson!)!;
                        observation["Observed"] = observation["Current"]!.DeepClone();
                        observation["Observed"]!["Authoritative"] = false;
                        observation["Observed"]!["SourceId"] = peer.Id.ToString();
                        var proof = SqlRepositoryWorkRecovery.ReadDiscovery(source.CurrentDiscoveryEvidenceJson)!;
                        var observedOffset = boundary is "observed-peer" or "newer-terminal-peer" ? 1 : boundary == "equal-terminal-peer" ? 0 : -1;
                        observation["Observed"]!["ObservedAtUtc"] = proof.ObservedAtUtc.AddSeconds(observedOffset).ToString("O");
                        var request = await db.SourceScanRequests.SingleAsync(x => x.Id ==
                            SqlRepositoryWorkRecovery.ReadDiscovery(source.CurrentDiscoveryEvidenceJson)!.ScanRequestId);
                        var unfinished = (Infrastructure.SqlServer.Persistence.Entities.SourceScanRequestEntity)db.Entry(request).CurrentValues.ToObject();
                        unfinished.Id = Guid.NewGuid();
                        unfinished.State = (int)(boundary == "observed-peer" ? SourceScanRequestState.Running : SourceScanRequestState.Failed);
                        unfinished.RequestedAtUtc = request.RequestedAtUtc.AddSeconds(boundary is "observed-peer" or "reordered-terminal-peer" ? 1 : -1);
                        unfinished.RowVersion = [];
                        db.SourceScanRequests.Add(unfinished);
                        observation["Observed"]!["ScanRequestId"] = unfinished.Id.ToString();
                        observation["Current"] = null;
                        peer.CurrentDiscoveryEvidenceJson = observation.ToJsonString();
                    }
                    else peer.CurrentDiscoveryEvidenceJson = null;
                    db.SourceRevisions.Add(peer);
                    await db.SaveChangesAsync();
                }
            };
        await pipeline.PumpAsync();

        if (boundary is "pending-candidate" or "observed-peer" or "newer-terminal-peer" or "equal-terminal-peer")
        {
            Assert.Equal(1, placements);
            await using var held = scenario.Factory.CreateDbContext();
            Assert.False(await held.PipelineRecords.Where(x => x.SourceRevisionId != originalId && x.SourceRevisionId != null)
                .AnyAsync(x => x.CompletionCriteriaMet));
            Assert.Null((await held.SourceRevisions.SingleAsync(x => x.Id == originalId)).SuppressedAtUtc);
            Assert.Single((await retrieval.SearchAsync(new(new string('x', 64), 10, "root", root.Id.Value, null, "literal"), default)).Results);
            Assert.Empty((await retrieval.SearchAsync(new(new string('y', 64), 10, "root", root.Id.Value, null, "literal"), default)).Results);
            return;
        }

        await using var verify = scenario.Factory.CreateDbContext();
        var successorHash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(successorText)));
        var successor = await verify.SourceRevisions.SingleAsync(x => x.CanonicalPath == scenario.Path && x.ContentSha256 == successorHash);
        var current = await verify.PipelineRecords.SingleAsync(x => x.SourceRevisionId == successor.Id);
        var job = await verify.Jobs.SingleAsync(x => x.PipelineRecordId == current.Id && x.Stage == current.CurrentStage);
        Assert.True(current.CompletionCriteriaMet,
            $"Current Git bytes did not reach publication: stage={current.CurrentStage}, state={job.PublicState}, reason={job.Reason}, details={job.ErrorDetails}");
        Assert.Empty((await retrieval.SearchAsync(new(new string('x', 64), 10, "root", root.Id.Value, null, "literal"), default)).Results);
        if (replaceFile) Assert.NotEqual(originalIdentity, successor.StableSourceIdentity);
        var hits = (await retrieval.SearchAsync(new(new string('y', 64), 10, "root", root.Id.Value, null, "literal"), default)).Results;
        if (emptySuccessor) Assert.Empty(hits);
        else Assert.Equal(successor.Id, Assert.Single(hits).OwnerSourceRevisionId);
        Assert.True((await verify.IndexState.AsNoTracking().SingleAsync()).CorpusVersion > stampBeforeEdit);

        Guid?[] visibleOwners;
        if (segmented)
            visibleOwners = await verify.CurrentIndexMembers.Where(x => x.SourceRootId == root.Id.Value)
                .Select(x => x.OwnerSourceRevisionId).Distinct().ToArrayAsync();
        else
        {
            var active = (await verify.IndexState.SingleAsync()).ActiveIndexGenerationId;
            visibleOwners = await (from membership in verify.IndexGenerationVectors
                join vector in verify.Vectors on membership.VectorId equals vector.VectorId
                join chunk in verify.TextChunks on vector.TextChunkId equals chunk.Id
                join artifact in verify.Artifacts on chunk.ArtifactId equals artifact.Id
                join record in verify.PipelineRecords on artifact.PipelineRecordId equals record.Id
                where membership.GenerationId == active && record.SourceRevisionId != null
                select record.SourceRevisionId).Distinct().ToArrayAsync();
        }
        if (emptySuccessor) Assert.DoesNotContain(successor.Id, visibleOwners);
        else Assert.Contains(successor.Id, visibleOwners);
        Assert.DoesNotContain(originalId, visibleOwners);
        if (boundary == "scope-change")
        {
            Assert.True(await verify.IndexPublicationCandidates.CountAsync(x => x.State == 3) > 0,
                "A sealed candidate was activated after its predecessor scope changed.");
        }
        if (!revert) return;

        if (boundary == "expired")
        {
            var original = await verify.SourceRevisions.SingleAsync(x => x.Id == originalId);
            original.RetainUntilUtc = scenario.Clock.Now.AddSeconds(-1);
            await verify.SaveChangesAsync();
        }
        var vectorCount = await verify.Vectors.CountAsync();
        var stampBeforeRevert = (await verify.IndexState.AsNoTracking().SingleAsync()).CorpusVersion;
        await File.WriteAllTextAsync(scenario.Path, new string('x', 64));
        await scenario.IntakeAsync();
        await scenario.SignalAsync();
        await scenario.DrainAsync();
        await new SqlSourceRemovalReconciler(scenario.Factory, scenario.Clock, new WindowsSourceObservationReader()).ReconcileAsync(default);
        await registration.OfferUnlinkedInProcessActivitiesAsync(default);
        await pipeline.PumpAsync();
        if (boundary == "pending-revert")
        {
            await using var pending = scenario.Factory.CreateDbContext();
            await AddPendingPublicationChangeAsync(pending, root.Id.Value, "one.txt", scenario.Clock.Now);
        }
        if (boundary == "paged-revert")
        {
            await using var history = scenario.Factory.CreateDbContext();
            var original = await history.SourceRevisions.SingleAsync(x => x.Id == originalId);
            var record = await history.PipelineRecords.SingleAsync(x => x.SourceRevisionId == originalId);
            var entry = await history.SourceInventoryEntries.SingleAsync(x => x.SourceRevisionId == originalId);
            for (var i = 1; i <= 65; i++)
            {
                var removed = (Infrastructure.SqlServer.Persistence.Entities.SourceRevisionEntity)history.Entry(original).CurrentValues.ToObject();
                removed.Id = Guid.Parse($"00000000-0000-0000-0000-{i:000000000000}");
                removed.CanonicalPath = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(scenario.Path)!, $"removed-{i}.txt");
                removed.StableSourceIdentity = $"removed-{i}";
                removed.CurrentDiscoveryEvidenceJson = SqlRepositoryWorkRecovery.Withdraw();
                removed.RowVersion = [];
                history.SourceRevisions.Add(removed);
                var completion = (Infrastructure.SqlServer.Persistence.Entities.PipelineRecordEntity)history.Entry(record).CurrentValues.ToObject();
                completion.Id = Guid.NewGuid();
                completion.SourceRevisionId = removed.Id;
                completion.Revision += 1000 + i;
                completion.RowVersion = [];
                history.PipelineRecords.Add(completion);
                var retainedEntry = (Infrastructure.SqlServer.Persistence.Entities.SourceInventoryEntryEntity)history.Entry(entry).CurrentValues.ToObject();
                retainedEntry.Id = Guid.NewGuid();
                retainedEntry.RowVersion = [];
                retainedEntry.RelativePath = $"removed-{i}.txt";
                retainedEntry.PathFingerprint = SqlSourceChangeJournalStore.PathKey(retainedEntry.RelativePath);
                retainedEntry.SourceRevisionId = removed.Id;
                retainedEntry.SourceArtifactId = null;
                history.SourceInventoryEntries.Add(retainedEntry);
            }
            await history.SaveChangesAsync();
        }
        var recovery = new SqlSourceWorkRecovery(scenario.Factory, new SourceWorkAdmission(),
            new SqlSourceRemovalReconciler(scenario.Factory, scenario.Clock, new WindowsSourceObservationReader()),
            new WindowsSourceObservationReader(), scenario.Clock);
        for (var pass = 0; pass < (boundary == "paged-revert" ? 3 : 1); pass++) await recovery.AdvanceAsync(default);
        if (boundary is "expired" or "pending-revert")
        {
            Assert.Empty((await retrieval.SearchAsync(new(new string('x', 64), 10, "root", root.Id.Value, null, "literal"), default)).Results);
            Assert.Equal(successor.Id, Assert.Single((await retrieval.SearchAsync(new(new string('y', 64), 10, "root", root.Id.Value, null, "literal"), default)).Results).OwnerSourceRevisionId);
            await using var held = scenario.Factory.CreateDbContext();
            Assert.NotNull((await held.SourceRevisions.SingleAsync(x => x.Id == originalId)).SuppressedAtUtc);
            Assert.Equal(stampBeforeRevert, (await held.IndexState.SingleAsync()).CorpusVersion);
            return;
        }
        var restored = Assert.Single((await retrieval.SearchAsync(new(new string('x', 64), 10, "root", root.Id.Value, null, "literal"), default)).Results);
        Assert.Equal(originalId, restored.OwnerSourceRevisionId);
        Assert.Empty((await retrieval.SearchAsync(new(new string('y', 64), 10, "root", root.Id.Value, null, "literal"), default)).Results);
        Assert.Equal(vectorCount, await verify.Vectors.CountAsync());
        Assert.True((await verify.IndexState.AsNoTracking().SingleAsync()).CorpusVersion > stampBeforeRevert);
        var coordinator = pipeline.Services.GetRequiredService<DerivedIndexRecoveryCoordinator>();
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        await coordinator.RunOnceAsync(budget.Token);
        Assert.True(coordinator.Snapshot.State == DerivedIndexRecoveryState.Healthy,
            System.Text.Json.JsonSerializer.Serialize(coordinator.Snapshot));
        await using var refreshed = scenario.Factory.CreateDbContext();
        var stateAfterRevert = await refreshed.IndexState.SingleAsync();
        var activeAfterRevert = await refreshed.IndexGenerations.SingleAsync(x => x.Id == stateAfterRevert.ActiveIndexGenerationId);
        Assert.Equal(stateAfterRevert.CorpusVersion, activeAfterRevert.CorpusVersion);
        if (segmented)
        {
            Assert.True(await refreshed.CurrentIndexMembers.AnyAsync(x => x.OwnerSourceRevisionId == originalId));
            Assert.False(await refreshed.CurrentIndexMembers.AnyAsync(x => x.OwnerSourceRevisionId == successor.Id));
        }
    }

    private static async Task AddPendingPublicationChangeAsync(FluxKnowledgeDbContext db, Guid rootId, string path, DateTimeOffset now)
    {
        var state = await db.SourceRootScanStates.SingleAsync(x => x.SourceRootId == rootId);
        db.SourcePendingChanges.Add(new()
        {
            Id = Guid.NewGuid(), SourceRootId = rootId, RelativePath = path,
            PathFingerprint = SqlSourceChangeJournalStore.PathKey(path), Generation = ++state.Generation,
            ReasonFlags = SourcePendingReasons.FileEvent, LastContentUsn = 0, LastUnjournaledGeneration = state.Generation,
            FirstObservedAtUtc = now, NotBeforeUtc = now.AddMinutes(1)
        });
        await db.SaveChangesAsync();
    }

    private sealed class GitPublicationBoundaryPublisher(IIndexGenerationPublisher inner, Func<Task> afterPlacement) : IIndexGenerationPublisher
    {
        public async ValueTask<IndexGenerationCandidateSnapshot> BuildAndPlaceAsync(Guid id, CancellationToken ct)
        {
            var candidate = await inner.BuildAndPlaceAsync(id, ct);
            await afterPlacement();
            return candidate;
        }
        public ValueTask<IndexGenerationDescriptor> RebuildFromSqlAsync(Guid id, CancellationToken ct) => inner.RebuildFromSqlAsync(id, ct);
        public ValueTask AbandonAsync(IndexGenerationCandidateSnapshot candidate, CancellationToken ct) => inner.AbandonAsync(candidate, ct);
    }

    [NativeSqlServerFact]
    public async Task A_proven_current_replacement_hold_recovers_without_another_file_change_or_scan()
    {
        await using var scenario = await Scenario.CreateAsync(fixture.ConnectionString, qualified: true, gitTracked: true);
        await scenario.ScanAsync("quiet-held-file");
        await scenario.DrainAsync();
        await new SqlSourceRemovalReconciler(scenario.Factory, scenario.Clock, new WindowsSourceObservationReader()).ReconcileAsync(default);
        var registration = new SqlRetainedTextRegistrationStore(scenario.Factory, scenario.Clock,
            sourceAdmission: new FixtureSourceAdmission(scenario.Factory));
        Assert.Equal(1, await registration.OfferUnlinkedInProcessActivitiesAsync(default));
        Guid jobId;
        int scanCount;
        await using (var setup = scenario.Factory.CreateDbContext())
        {
            var job = await setup.Jobs.SingleAsync();
            var dispatch = await setup.OutboxMessages.SingleAsync(x => x.JobId == job.Id);
            SqlRepositoryWorkRecovery.SetWaiting(setup, job, dispatch,
                new("repository-source-replaced", true), scenario.Clock.Now);
            await setup.SaveChangesAsync();
            jobId = job.Id;
            scanCount = await setup.SourceScanRequests.CountAsync();
        }
        scenario.Clock.Now += TimeSpan.FromMinutes(2);
        await SqlRepositoryWorkRecovery.ReconcileDueAsync(scenario.Factory, scenario.Clock.Now, null, default);
        await using var verify = scenario.Factory.CreateDbContext();
        var resumed = await verify.Jobs.SingleAsync(x => x.Id == jobId);
        Assert.Equal("repository-source-resumed", resumed.Reason);
        Assert.Null(resumed.ErrorDetails);
        Assert.True(resumed.DueAtUtc <= scenario.Clock.Now);
        Assert.Equal(scanCount, await verify.SourceScanRequests.CountAsync());
    }

    [NativeSqlServerFact]
    public async Task A_stale_replacement_refusal_cannot_reblock_freshly_authorised_Extract_work()
    {
        await using var scenario = await Scenario.CreateAsync(fixture.ConnectionString, qualified: true, gitTracked: true);
        await scenario.ScanAsync("stale-worker-refusal");
        await scenario.DrainAsync();
        await new SqlSourceRemovalReconciler(scenario.Factory, scenario.Clock, new WindowsSourceObservationReader()).ReconcileAsync(default);
        var admission = new FixtureSourceAdmission(scenario.Factory);
        var registration = new SqlRetainedTextRegistrationStore(scenario.Factory, scenario.Clock, sourceAdmission: admission);
        Assert.Equal(1, await registration.OfferUnlinkedInProcessActivitiesAsync(default));
        var dispatch = Assert.IsType<FluxKnowledge.Application.Workers.ClaimedDispatchMessage>(await new SqlOutboxStore(scenario.Factory, sourceAdmission: admission)
            .ClaimNextDueAsync("old-refusal-worker", scenario.Clock.Now, TimeSpan.FromMinutes(2), [PipelineOperations.ExtractUtf8], default));
        var job = Assert.IsType<ClaimedJob>(await new SqlJobClaimStore(scenario.Factory, sourceAdmission: admission)
            .ClaimForDispatchAsync(dispatch, "old-refusal-worker", scenario.Clock.Now, TimeSpan.FromMinutes(2), default));
        // The worker's earlier decision crosses a later successful discovery. Retry must use its locked fresh validation.
        await new SqlStageTransitionStore(scenario.Factory, null, scenario.Clock, sourceAdmission: admission).RetryAsync(
            new StageRetryRequest(dispatch, job, scenario.Clock.Now.AddMinutes(1), "repository-source-deferred", "test",
                new RepositorySourceDeferral("repository-source-replaced", true)), default);
        await using var verify = scenario.Factory.CreateDbContext();
        var current = await verify.Jobs.SingleAsync(x => x.Id == job.JobId.Value);
        Assert.Equal((int)PublicJobState.WorkerQueued, current.PublicState);
        Assert.Equal("repository-source-resumed", current.Reason);
        Assert.Null(current.ErrorDetails);
    }

    [NativeSqlServerTheory]
    [InlineData("pending-observation")]
    [InlineData("artifact-binding")]
    [InlineData("incomplete-scan")]
    [InlineData("configuration")]
    [InlineData("gpu-hold")]
    [InlineData("owned-delivery")]
    public async Task Replacement_recovery_preserves_unproved_inputs_and_unrelated_ownership_holds(string boundary)
    {
        await using var scenario = await Scenario.CreateAsync(fixture.ConnectionString, qualified: true, gitTracked: true);
        await scenario.ScanAsync("held-proof-boundary");
        await scenario.DrainAsync();
        await new SqlSourceRemovalReconciler(scenario.Factory, scenario.Clock, new WindowsSourceObservationReader()).ReconcileAsync(default);
        var registration = new SqlRetainedTextRegistrationStore(scenario.Factory, scenario.Clock,
            sourceAdmission: new FixtureSourceAdmission(scenario.Factory));
        Assert.Equal(1, await registration.OfferUnlinkedInProcessActivitiesAsync(default));
        Guid jobId;
        DateTimeOffset due;
        await using (var setup = scenario.Factory.CreateDbContext())
        {
            var job = await setup.Jobs.SingleAsync();
            var dispatch = await setup.OutboxMessages.SingleAsync(x => x.JobId == job.Id);
            SqlRepositoryWorkRecovery.SetWaiting(setup, job, dispatch, new("repository-source-replaced", true), scenario.Clock.Now);
            var source = await setup.SourceRevisions.SingleAsync();
            switch (boundary)
            {
                case "pending-observation":
                    var proof = System.Text.Json.Nodes.JsonNode.Parse(source.CurrentDiscoveryEvidenceJson!)!;
                    proof["Observed"] = proof["Current"]!.DeepClone();
                    proof["Observed"]!["Authoritative"] = false;
                    source.CurrentDiscoveryEvidenceJson = proof.ToJsonString();
                    break;
                case "artifact-binding":
                    (await setup.SourceInventoryEntries.SingleAsync(x => x.SourceRevisionId == source.Id)).SourceArtifactId = null;
                    break;
                case "incomplete-scan":
                    (await setup.SourceScanRequests.OrderByDescending(x => x.RequestedAtUtc).FirstAsync()).State = (int)SourceScanRequestState.Running;
                    break;
                case "configuration":
                    (await setup.SourceRootConfigurations.SingleAsync()).ConfigurationRevision++;
                    break;
                case "gpu-hold":
                    job.Stage = dispatch.Stage = (int)PipelineStage.Embed;
                    job.Operation = dispatch.Operation = PipelineOperations.Embed;
                    (await setup.PipelineRecords.SingleAsync()).CurrentStage = (int)PipelineStage.Embed;
                    job.ErrorDetails = "{\"Reason\":\"repository-gpu-outcome-cleanup-or-binding-unconfirmed\",\"CheckAfterSeconds\":60}";
                    break;
                case "owned-delivery":
                    dispatch.LeaseOwner = "another-current-worker";
                    dispatch.LeaseExpiresAtUtc = scenario.Clock.Now.AddMinutes(5);
                    break;
            }
            await setup.SaveChangesAsync();
            jobId = job.Id;
            due = job.DueAtUtc;
        }
        scenario.Clock.Now += TimeSpan.FromMinutes(2);
        await SqlRepositoryWorkRecovery.ReconcileDueAsync(scenario.Factory, scenario.Clock.Now, null, default);
        await using var verify = scenario.Factory.CreateDbContext();
        var held = await verify.Jobs.SingleAsync(x => x.Id == jobId);
        Assert.Equal("repository-source-blocked", held.Reason);
        Assert.Equal(due, held.DueAtUtc);
        Assert.Equal((int)PublicJobState.WorkerQueued, held.PublicState);
        if (boundary == "owned-delivery")
            Assert.Equal("another-current-worker", (await verify.OutboxMessages.SingleAsync(x => x.JobId == jobId)).LeaseOwner);
    }

    [NativeSqlServerFact]
    public async Task A_clean_Git_capture_keeps_its_proof_retry_after_an_unrelated_enumeration_failure()
    {
        await using var scenario = await Scenario.CreateAsync(fixture.ConnectionString, qualified: true, gitTracked: true);
        var sibling = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(scenario.Path)!, "sibling.txt");
        await File.WriteAllTextAsync(sibling, new string('s', 64));
        scenario.Git("add", ".");
        var root = await scenario.ScanAsync("partial-git-baseline");
        await scenario.DrainAsync();
        await File.WriteAllTextAsync(scenario.Path, new string('y', 64));
        await File.WriteAllTextAsync(sibling, new string('t', 64));
        await scenario.IntakeAsync();
        var watcher = new SqlSourceRootWatchStore(scenario.Factory, scenario.Clock, scenario.Options);
        await watcher.RecordSignalAsync(new(root.Id, SourceWatchSignalKind.Changed, scenario.Clock.Now,
            [new("one.txt"), new("sibling.txt")]), default);
        scenario.Clock.Now += TimeSpan.FromSeconds(3);
        var batch = Assert.IsType<ClaimedSourceWatchBatch>(await watcher.ClaimDueBatchAsync(scenario.Clock.Now,
            "partial-git-watch", TimeSpan.FromMinutes(1), default));
        await watcher.ReleaseScanAsync(batch, default);
        var hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(new string('y', 64))));
        // Model a transient sibling read failure that has cleared by the closing inventory read.
        scenario.CompletionEvidence = [new("io", "sibling.txt", "SourceInUse") { NativeErrorCode = 32 }];
        {
            await scenario.DrainAsync();
            await new SqlSourceRemovalReconciler(scenario.Factory, scenario.Clock, new WindowsSourceObservationReader()).ReconcileAsync(default);
            await using var pending = scenario.Factory.CreateDbContext();
            var captured = await pending.SourceRevisions.SingleAsync(x => x.CanonicalPath == scenario.Path && x.ContentSha256 == hash);
            Assert.Null(SqlRepositoryWorkRecovery.ReadDiscovery(captured.CurrentDiscoveryEvidenceJson));
            Assert.True(await pending.SourcePendingChanges.AnyAsync(x => x.SourceRootId == root.Id.Value && x.RelativePath == "one.txt"),
                "A clean capture lost its only pending retry before its Git proof was authorised.");
        }
        scenario.CompletionEvidence = [];
        scenario.Clock.Now += TimeSpan.FromMinutes(2);
        await scenario.DrainAsync();
        await using var verify = scenario.Factory.CreateDbContext();
        var current = await verify.SourceRevisions.SingleAsync(x => x.CanonicalPath == scenario.Path && x.ContentSha256 == hash);
        Assert.True(SqlRepositoryWorkRecovery.ReadDiscovery(current.CurrentDiscoveryEvidenceJson)?.Authoritative);
    }

    private sealed class GitPublicationEvidenceCodec : ICorpusEvidenceCodec
    {
        private readonly Dictionary<string, CorpusEvidenceBinding> _bindings = [];
        public string Encode(CorpusEvidenceBinding binding)
        {
            var reference = Guid.NewGuid().ToString("N");
            _bindings.Add(reference, binding);
            return reference;
        }
        public CorpusEvidenceBinding Decode(string reference) => _bindings[reference];
    }
}
