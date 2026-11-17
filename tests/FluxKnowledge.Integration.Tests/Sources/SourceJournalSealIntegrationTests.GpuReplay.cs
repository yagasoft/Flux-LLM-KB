using System.Text.Json;
using System.Security.Cryptography;
using FluxKnowledge.Application.Gpu;
using FluxKnowledge.Application.Indexing;
using FluxKnowledge.Application.IntegrationV1;
using FluxKnowledge.Application.IntegrationV1.Corpus;
using FluxKnowledge.Application.Pipeline;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Search;
using FluxKnowledge.Application.Workers;
using FluxKnowledge.Domain.Gpu;
using FluxKnowledge.Domain.Jobs;
using FluxKnowledge.Infrastructure.Inference.Search;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using FluxKnowledge.Infrastructure.SqlServer.Search;
using FluxKnowledge.Infrastructure.SqlServer.Visibility;
using FluxKnowledge.Infrastructure.SqlServer.Workers;
using FluxKnowledge.Integration.Tests.Indexing;
using FluxKnowledge.Integration.Tests.Models;
using FluxKnowledge.Integration.Tests.Support;
using FluxKnowledge.Integrations.Windows;
using FluxKnowledge.Integrations.Files;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Sources;

public sealed partial class SourceJournalSealIntegrationTests
{
    private static readonly EmbeddingGpuRuntime ReplayRuntime = new(BgeOfflineModels.GpuRuntimeKey,
        BgeOfflineModels.GpuSettingsFingerprint, new(BgeOfflineModels.EmbeddingFingerprint, 1024), 1024);

    [NativeSqlServerTheory]
    [InlineData(false, 1)]
    [InlineData(true, 1)]
    [InlineData(false, 2)]
    [InlineData(true, 2)]
    public Task Qualified_repository_replays_released_missing_group_and_publishes_without_rewriting_successful_siblings(bool segmented, int failedAttempts) =>
        AssertRepositoryGpuReplayAsync(segmented, failedAttempts);

    [NativeSqlServerTheory]
    [InlineData("cleanup")]
    [InlineData("owner")]
    [InlineData("dispatch")]
    [InlineData("capacity")]
    [InlineData("partial-result")]
    [InlineData("runtime")]
    [InlineData("retry-budget")]
    [InlineData("conflicting-groups")]
    [InlineData("older-success")]
    [InlineData("pending-source")]
    [InlineData("leased-delivery")]
    [InlineData("sibling-result")]
    public Task Repository_group_replay_refuses_unsettled_conflicting_or_exhausted_evidence(string boundary) =>
        AssertRepositoryGpuReplayAsync(false, boundary == "retry-budget" ? 3 : boundary == "conflicting-groups" ? 2 : 1, boundary);

    [NativeSqlServerTheory]
    [InlineData(false)]
    [InlineData(true)]
    public Task Repository_publication_requires_a_later_verified_success_for_every_failed_group(bool segmented) =>
        AssertRepositoryGpuReplayAsync(segmented, 1, "publication-result");

    [NativeSqlServerTheory]
    [InlineData(false)]
    [InlineData(true)]
    public Task Repository_publication_rechecks_group_success_after_candidate_placement(bool segmented) =>
        AssertRepositoryGpuReplayAsync(segmented, 1, "publication-race");

    [NativeSqlServerTheory]
    [InlineData(false, "after-requeue-authority")]
    [InlineData(true, "after-requeue-authority")]
    [InlineData(false, "after-checkpoint-authority")]
    [InlineData(true, "after-checkpoint-authority")]
    [InlineData(false, "publication-authority")]
    [InlineData(true, "publication-authority")]
    public Task Repository_replay_rechecks_exact_current_capture_authority(bool segmented, string boundary) =>
        AssertRepositoryGpuReplayAsync(segmented, 1, boundary);

    [NativeSqlServerTheory]
    [InlineData(false, null)]
    [InlineData(true, null)]
    [InlineData(false, "publication-result")]
    [InlineData(true, "publication-result")]
    [InlineData(false, "publication-race")]
    [InlineData(true, "publication-race")]
    [InlineData(false, "cleanup")]
    [InlineData(false, "capacity")]
    [InlineData(false, "older-success")]
    public Task Confirmed_non_Git_settled_recovery_preserves_gpu_proof_through_publication(bool segmented, string? boundary) =>
        AssertRepositoryGpuReplayAsync(segmented, 1, boundary, explicitNonGit: true);

    [NativeSqlServerTheory]
    [InlineData(false, "restore-checkpoint")]
    [InlineData(true, "restore-checkpoint")]
    [InlineData(false, "restore-after-replacement")]
    [InlineData(true, "restore-after-replacement")]
    [InlineData(false, "inventory-alias")]
    public Task Marked_filesystem_recovery_requires_exact_path_and_revisits_restored_pending_or_superseded_proof(bool segmented, string boundary) =>
        AssertRepositoryGpuReplayAsync(segmented, 1, boundary, explicitNonGit: true, nativePending: boundary == "restore-checkpoint");

    [NativeSqlServerTheory]
    [InlineData(false, "terminal-during-handoff")]
    [InlineData(true, "terminal-during-handoff")]
    [InlineData(false, "terminal-before-native")]
    [InlineData(true, "terminal-before-native")]
    public Task Confirmed_Git_settled_history_without_unstarted_request_keeps_capture_after_checkpoint(bool segmented, string boundary) =>
        AssertRepositoryGpuReplayAsync(segmented, 1, boundary, explicitTerminalGit: true);

    private async Task AssertRepositoryGpuReplayAsync(bool segmented, int failedAttempts, string? boundary = null, bool explicitNonGit = false,
        bool nativePending = false, bool explicitTerminalGit = false)
    {
        var refusesBeforeResume = boundary is not null and not "publication-result" and not "publication-race" and
            not "publication-authority" and not "after-requeue-authority" and not "after-checkpoint-authority" and
            not "restore-checkpoint" and not "restore-after-replacement" and not "terminal-during-handoff" and not "terminal-before-native";
        await using var scenario = await Scenario.CreateAsync(fixture.ConnectionString, qualified: true, gitTracked: true);
        await File.WriteAllTextAsync(scenario.Path, string.Join("\n\n", Enumerable.Range(0, 32)
            .Select(i => $"Repository replay section {i}. " + new string('x', 900))));
        Func<Task>? afterPlacement = null;
        await using var pipeline = await SqlToUsearchRebuildTests.PipelineEnvironment.CreateAsync(fixture, "Disposable bootstrap.",
            embed: false, clock: scenario.Clock, segmented: segmented, configureServices: services =>
            {
                services.AddSingleton(ReplayRuntime);
                services.AddScoped<IRetainedSourceReader>(_ => new SqlRetainedSourceReader(scenario.Factory, scenario.RetainedRoot));
                var publisher = services.Last(x => x.ServiceType == typeof(IIndexGenerationPublisher));
                services.AddScoped<IIndexGenerationPublisher>(provider => new GitPublicationBoundaryPublisher(
                    (IIndexGenerationPublisher)publisher.ImplementationFactory!(provider), async () =>
                    {
                        var action = afterPlacement;
                        afterPlacement = null;
                        if (action is not null) await action();
                    }));
            });
        // No unrelated embedding profile participates in this model-free GPU fixture.
        await SqlTestData.ClearPipelineAsync(fixture);
        await using (var format = scenario.Factory.CreateDbContext())
        {
            (await format.SourceRootConfigurations.SingleAsync()).MaximumFileBytes = 128 * 1024;
            var state = await format.IndexState.SingleAsync();
            state.RequiredIndexFormat = segmented ? 2 : 1;
            state.ProjectionChangeFloorVersion = state.CorpusVersion;
            await format.SaveChangesAsync();
        }
        var root = await scenario.ScanAsync("repository-replay-baseline");
        await scenario.DrainAsync();
        await new SqlSourceRemovalReconciler(scenario.Factory, scenario.Clock, new WindowsSourceObservationReader()).ReconcileAsync(default);
        Assert.Equal(1, await new SqlRetainedTextRegistrationStore(scenario.Factory, scenario.Clock,
            sourceAdmission: new FixtureSourceAdmission(scenario.Factory)).OfferUnlinkedInProcessActivitiesAsync(default));
        await pipeline.PumpAsync();

        var admission = new FixtureSourceAdmission(scenario.Factory);
        var scheduler = new SqlGpuSchedulerStore(scenario.Factory, timeProvider: scenario.Clock, sourceAdmission: admission);
        var wake = new ChannelGpuSchedulerWakeSignal();
        var requests = new SqlEmbeddingGpuRequestStore(scenario.Factory, scheduler, wake, ReplayRuntime, scenario.Clock, sourceAdmission: admission);
        var checkpoint = new SqlEmbeddingCheckpointStore(scenario.Factory, scenario.Clock, ReplayRuntime, admission);
        await using (var setup = scenario.Factory.CreateDbContext())
        {
            setup.GpuCapacitySlots.Add(new() { SlotKey = "repository-replay-gpu", UpdatedAtUtc = scenario.Clock.Now });
            await setup.SaveChangesAsync();
        }
        for (var i = 0; i < 2; i++)
        {
            var work = await ClaimRepositoryEmbedAsync(scenario);
            var batch = await checkpoint.ReadNextAsync(work, ReplayRuntime.Profile, default);
            Assert.True(batch.Chunks.Count >= 4);
            await requests.QueueAsync(work, batch with { Chunks = batch.Chunks.Take(4).ToArray() }, default);
            var handle = await AdmitRepositoryGpuAsync(scheduler);
            await DeliverRepositoryGpuAsync(requests, scheduler, wake, scenario.Clock, handle);
        }
        await using var before = scenario.Factory.CreateDbContext();
        var savedSiblings = await before.Vectors.AsNoTracking().OrderBy(x => x.VectorId)
            .Select(x => new { x.VectorId, x.PayloadChecksum, Version = Convert.ToBase64String(x.RowVersion) }).ToArrayAsync();
        Assert.Equal(8, savedSiblings.Length);

        StageWorkItem stranded = null!;
        EmbeddingWorkBatch missing = null!;
        EmbeddingGpuExecutionWork execution = null!;
        GpuExecutorBatchHandle failedHandle = null!;
        for (var attempt = 0; attempt < failedAttempts; attempt++)
        {
            stranded = await ClaimRepositoryEmbedAsync(scenario);
            missing = await checkpoint.ReadNextAsync(stranded, ReplayRuntime.Profile, default);
            missing = missing with { Chunks = missing.Chunks.Take(4).ToArray() };
            Assert.NotEmpty(missing.Chunks);
            await requests.QueueAsync(stranded, missing, default);
            failedHandle = await AdmitRepositoryGpuAsync(scheduler);
            execution = await FailRepositoryGroupAsync(scenario, requests, scheduler, failedHandle);
        }
        string failedEvidence;
        await using (var held = scenario.Factory.CreateDbContext())
        {
            Assert.False(await held.GpuCapacitySlots.AnyAsync(x => x.ActiveBatchId == failedHandle.BatchId));
            // Older releases represented the same reconciled capacity as Released.
            // Retain the real cleanup and reconciliation receipts behind that shape.
            (await held.GpuBatches.SingleAsync(x => x.Id == failedHandle.BatchId)).State = (int)GpuBatchState.Released;
            var job = await held.Jobs.SingleAsync(x => x.Id == stranded.Job.JobId.Value);
            var dispatch = await held.OutboxMessages.SingleAsync(x => x.JobId == job.Id);
            await SqlRepositoryWorkRecovery.MarkRecoveryRequiredAsync(held, job, default);
            SqlRepositoryWorkRecovery.SetWaiting(held, job, dispatch,
                new("repository-gpu-outcome-cleanup-or-binding-unconfirmed", true), scenario.Clock.Now);
            await held.SaveChangesAsync();
            failedEvidence = JsonSerializer.Serialize(await held.EmbeddingGpuRequests.AsNoTracking().SingleAsync(x => x.MiniTaskId == execution.MiniTaskId));
            Assert.Equal((int)GpuMiniTaskExecutionState.OutcomeUncertain, (await held.GpuMiniTasks.SingleAsync(x => x.Id == execution.MiniTaskId)).ExecutionState);
            Assert.Equal((int)GpuBatchState.Released, (await held.GpuBatches.SingleAsync(x => x.Id == failedHandle.BatchId)).State);
        }
        if (refusesBeforeResume)
        {
            await using var fault = scenario.Factory.CreateDbContext();
            var request = await fault.EmbeddingGpuRequests.SingleAsync(x => x.MiniTaskId == execution.MiniTaskId);
            var dispatch = await fault.OutboxMessages.SingleAsync(x => x.JobId == stranded.Job.JobId.Value);
            switch (boundary)
            {
                case "cleanup": request.State = 0; request.NativeCleanupConfirmed = false; request.CleanupConfirmedAtUtc = null; break;
                case "owner":
                    request.ExecutorInstanceId = null; request.ClaimOperationId = null; request.OwnerProcessId = null;
                    request.OwnerStartedAtUtc = null; request.OwnerMachineFingerprint = null; request.DispatchId = null;
                    break;
                case "dispatch": request.DispatchId = Guid.NewGuid(); break;
                case "capacity": (await fault.GpuCapacitySlots.SingleAsync()).ActiveBatchId = failedHandle.BatchId; break;
                case "runtime": (await fault.GpuMiniTasks.SingleAsync(x => x.Id == request.MiniTaskId)).SettingsFingerprint = "changed-settings"; break;
                case "leased-delivery": dispatch.LeaseOwner = "another-worker"; dispatch.LeaseExpiresAtUtc = scenario.Clock.Now.AddHours(1); break;
                case "pending-source": await AddPendingPublicationChangeAsync(fault, root.Id.Value, "one.txt", scenario.Clock.Now); break;
                case "partial-result":
                    var sibling = await fault.Vectors.FirstAsync();
                    var partial = (VectorEntity)fault.Entry(sibling).CurrentValues.ToObject();
                    partial.VectorId = 0; partial.RowVersion = []; partial.TextChunkId = missing.Chunks[0].Id;
                    partial.TextChunkContentHash = missing.Chunks[0].ContentHash; partial.SearchInputHash = missing.Chunks[0].SearchInputHash;
                    fault.Vectors.Add(partial);
                    (await fault.IndexGenerations.SingleAsync(x => x.Id == missing.GenerationId)).VectorCount++;
                    break;
                case "older-success":
                    var oldSuccess = await fault.EmbeddingGpuRequests.FirstAsync(x => x.ResultDigest != null);
                    request.InputsJson = oldSuccess.InputsJson; request.InputDigest = oldSuccess.InputDigest;
                    break;
                case "conflicting-groups":
                    var earlier = await fault.EmbeddingGpuRequests.FirstAsync(x => x.ResultDigest == null && x.MiniTaskId != request.MiniTaskId);
                    earlier.InputsJson = JsonSerializer.Serialize(missing.Chunks.Reverse().Select(x => new { x.Id, Hash = x.SearchInputHash }));
                    earlier.InputDigest = Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(earlier.InputsJson)));
                    break;
                case "sibling-result":
                    (await fault.EmbeddingGpuRequests.FirstAsync(x => x.ResultDigest != null)).ResultDigest = new byte[32];
                    break;
            }
            await fault.SaveChangesAsync();
        }
        scenario.Clock.Now += TimeSpan.FromMinutes(2);
        if (explicitNonGit || explicitTerminalGit)
        {
            await using (var legacy = scenario.Factory.CreateDbContext())
            {
                if (explicitNonGit)
                {
                    await legacy.SourceRootConfigurations.Where(x => x.Id == root.Id.Value).ExecuteUpdateAsync(setters => setters.SetProperty(x => x.CrawlMode, 0));
                    await legacy.PipelineRecords.Where(x => x.Id == stranded.Job.PipelineRecordId.Value).ExecuteUpdateAsync(setters => setters.SetProperty(x => x.RepositoryRecoveryBindingJson, (string?)null));
                }
                else
                    await legacy.Database.ExecuteSqlInterpolatedAsync($"UPDATE SourceRevisions SET CurrentDiscoveryEvidenceJson=JSON_MODIFY(CurrentDiscoveryEvidenceJson,'$.RecoveryRequired',CAST(0 AS bit)) WHERE Id={(await legacy.PipelineRecords.SingleAsync(x => x.Id == stranded.Job.PipelineRecordId.Value)).SourceRevisionId}");
                await legacy.Jobs.Where(x => x.Id == stranded.Job.JobId.Value).ExecuteUpdateAsync(setters => setters.SetProperty(x => x.PublicState, 5).SetProperty(x => x.Reason, "stage worker failed non-retryably"));
                await legacy.OutboxMessages.Where(x => x.JobId == stranded.Job.JobId.Value).ExecuteUpdateAsync(setters => setters.SetProperty(x => x.DispatchedAtUtc, scenario.Clock.Now));
                await legacy.SourceActivities.Where(x => x.ResultingPipelineRecordId == stranded.Job.PipelineRecordId.Value).ExecuteUpdateAsync(setters => setters.SetProperty(x => x.State, 6));
                if (nativePending)
                {
                    var pending = (EmbeddingGpuRequestEntity)legacy.Entry(await legacy.EmbeddingGpuRequests.SingleAsync(x => x.MiniTaskId == execution.MiniTaskId)).CurrentValues.ToObject();
                    pending.MiniTaskId = Guid.NewGuid(); pending.RowVersion = []; pending.State = 0;
                    pending.ExecutorInstanceId = null; pending.ClaimOperationId = null; pending.DispatchId = null;
                    pending.OwnerProcessId = null; pending.OwnerStartedAtUtc = null; pending.OwnerMachineFingerprint = null;
                    pending.NativeCleanupConfirmed = false; pending.CleanupConfirmedAtUtc = null;
                    pending.CreatedAtUtc = pending.UpdatedAtUtc = scenario.Clock.Now;
                    legacy.EmbeddingGpuRequests.Add(pending); await legacy.SaveChangesAsync();
                }
                if (boundary == "inventory-alias")
                {
                    var canonicalEntry = await legacy.SourceInventoryEntries.SingleAsync(x => x.RelativePath == "one.txt");
                    var alias = (SourceInventoryEntryEntity)legacy.Entry(canonicalEntry).CurrentValues.ToObject();
                    alias.Id = Guid.NewGuid(); alias.RelativePath = "old-alias.txt"; alias.PathFingerprint = SqlSourceChangeJournalStore.PathKey(alias.RelativePath);
                    legacy.SourceInventoryEntries.Add(alias);
                    var oldSource = await legacy.SourceRevisions.SingleAsync(x => x.Id == canonicalEntry.SourceRevisionId);
                    var successor = (SourceRevisionEntity)legacy.Entry(oldSource).CurrentValues.ToObject();
                    successor.Id = Guid.NewGuid(); successor.RowVersion = []; successor.StableSourceIdentity += ":new-physical-file";
                    successor.ContentSha256 = new string('b', 64); successor.ParentSourceRevisionId = null; successor.Revision = 1;
                    legacy.SourceRevisions.Add(successor);
                    var retained = (SourceArtifactEntity)legacy.Entry(await legacy.SourceArtifacts.SingleAsync(x => x.SourceRevisionId == oldSource.Id)).CurrentValues.ToObject();
                    retained.Id = Guid.NewGuid(); retained.RowVersion = []; retained.SourceRevisionId = successor.Id; retained.ContentSha256 = successor.ContentSha256;
                    legacy.SourceArtifacts.Add(retained); canonicalEntry.SourceRevisionId = successor.Id; canonicalEntry.SourceArtifactId = retained.Id;
                    await legacy.SaveChangesAsync();
                }
            }
            var service = new NativeCorpusCommandService(new SqlNativeOperationStore(scenario.Factory, scenario.Clock, embeddingRuntime: ReplayRuntime),
                new SqlNativeCorpusActionStore(scenario.Factory, new SourceRootPathPolicy(new LocalIngressOptions([Path.GetDirectoryName(scenario.Path)!])),
                    new LocalPrivateContentDisclosure(), ReplayRuntime));
            var command = new NativeCorpusMutation("embedding_retry", JsonSerializer.SerializeToElement(new { jobId = stranded.Job.JobId.Value }));
            if (refusesBeforeResume)
            {
                await Assert.ThrowsAsync<NativeOperationException>(() => service.PreviewAsync(command, "test", default).AsTask());
                return;
            }
            var preview = await service.PreviewAsync(command, "test", default);
            await service.CommitAsync(command, preview.ConfirmationId, "non-git-settled-" + stranded.Job.JobId.Value.ToString("N"), "test", default);
        }
        else
            await Task.WhenAll(SqlRepositoryWorkRecovery.ReconcileDueAsync(scenario.Factory, scenario.Clock.Now, ReplayRuntime, default),
                SqlRepositoryWorkRecovery.ReconcileDueAsync(scenario.Factory, scenario.Clock.Now, ReplayRuntime, default));
        await using (var resumed = scenario.Factory.CreateDbContext())
        {
            var job = await resumed.Jobs.SingleAsync(x => x.Id == stranded.Job.JobId.Value);
            if (refusesBeforeResume)
            {
                Assert.NotEqual("repository-source-resumed", job.Reason);
                Assert.False((await resumed.PipelineRecords.SingleAsync(x => x.Id == job.PipelineRecordId)).CompletionCriteriaMet);
                Assert.Equal(failedAttempts + 2, await resumed.EmbeddingGpuRequests.CountAsync());
                Assert.Null((await resumed.IndexState.SingleAsync()).ActiveIndexGenerationId);
                return;
            }
            Assert.Equal(explicitNonGit || explicitTerminalGit ? "explicit-embedding-recovery" : "repository-source-resumed", job.Reason);
            // The operator's explicit terminal retry API retains its strict contract.
            await Assert.ThrowsAsync<FluxKnowledge.Application.IntegrationV1.NativeOperationException>(() =>
                SqlEmbeddingRetry.ReadSettledGpuBindingAsync(resumed, job,
                    resumed.IndexGenerations.Single(x => x.Id == missing.GenerationId), ReplayRuntime, default));
        }
        async Task InvalidateCaptureAsync()
        {
            await using var changed = scenario.Factory.CreateDbContext();
            (await changed.SourceInventoryEntries.SingleAsync(x => x.RelativePath == "one.txt")).BaselineEpoch++;
            await changed.SaveChangesAsync();
        }
        async Task<StageWorkItem> RequalifyCaptureAsync(StageWorkItem work, RepositorySourceDeferral deferral)
        {
            Assert.False(deferral.Blocked);
            await new SqlStageTransitionStore(scenario.Factory, null, scenario.Clock, ReplayRuntime, new FixtureSourceAdmission(scenario.Factory))
                .RetryAsync(new(work.DispatchMessage, work.Job, scenario.Clock.Now.AddMinutes(1), SqlRepositoryWorkRecovery.Waiting,
                    "replay-fixture", deferral), default);
            await using (var changed = scenario.Factory.CreateDbContext())
            {
                Assert.Equal(SqlRepositoryWorkRecovery.Waiting, (await changed.Jobs.SingleAsync(x => x.Id == work.Job.JobId.Value)).Reason);
                (await changed.SourceInventoryEntries.SingleAsync(x => x.RelativePath == "one.txt")).BaselineEpoch--;
                await changed.SaveChangesAsync();
            }
            scenario.Clock.Now += TimeSpan.FromMinutes(2);
            await SqlRepositoryWorkRecovery.ReconcileDueAsync(scenario.Factory, scenario.Clock.Now, ReplayRuntime, default);
            return await ClaimRepositoryEmbedAsync(scenario);
        }
        var replacement = await ClaimRepositoryEmbedAsync(scenario);
        async Task<StageWorkItem> RestoreBlockedProofAsync(StageWorkItem owned, bool leaveUnstarted)
        {
            if (leaveUnstarted)
            {
                var next = await checkpoint.ReadNextAsync(owned, ReplayRuntime.Profile, default);
                var interrupted = new SqlEmbeddingGpuRequestStore(scenario.Factory,
                    new SqlGpuSchedulerStore(scenario.Factory, timeProvider: scenario.Clock, sourceAdmission: admission,
                        beforeIdempotencyRead: _ => throw new InvalidOperationException("injected-unstarted-after-success")),
                    wake, ReplayRuntime, scenario.Clock, sourceAdmission: admission);
                await Assert.ThrowsAsync<InvalidOperationException>(() => interrupted.QueueAsync(owned, next, default).AsTask());
            }
            Guid siblingId; byte[] originalResult;
            await using (var lost = scenario.Factory.CreateDbContext())
            {
                var sibling = await lost.EmbeddingGpuRequests.FirstAsync(x => x.ResultDigest != null);
                siblingId = sibling.MiniTaskId; originalResult = sibling.ResultDigest!.ToArray(); sibling.ResultDigest = new byte[32]; await lost.SaveChangesAsync();
            }
            var refusal = await Assert.ThrowsAsync<RepositorySourceDeferredException>(() => checkpoint.ReadNextAsync(owned, ReplayRuntime.Profile, default).AsTask());
            Assert.True(refusal.Deferral.Blocked);
            await new SqlStageTransitionStore(scenario.Factory, null, scenario.Clock, ReplayRuntime, admission)
                .RetryAsync(new(owned.DispatchMessage, owned.Job, scenario.Clock.Now.AddMinutes(1), SqlRepositoryWorkRecovery.Waiting, "test", refusal.Deferral), default);
            await using (var restore = scenario.Factory.CreateDbContext())
            {
                Assert.Equal(SqlRepositoryWorkRecovery.Blocked, (await restore.Jobs.SingleAsync(x => x.Id == owned.Job.JobId.Value)).Reason);
                Assert.Null((await restore.PipelineRecords.SingleAsync(x => x.Id == owned.Job.PipelineRecordId.Value)).RepositoryRecoveryBindingJson);
                (await restore.EmbeddingGpuRequests.SingleAsync(x => x.MiniTaskId == siblingId)).ResultDigest = originalResult; await restore.SaveChangesAsync();
            }
            scenario.Clock.Now += TimeSpan.FromMinutes(2);
            await SqlRepositoryWorkRecovery.ReconcileDueAsync(scenario.Factory, scenario.Clock.Now, ReplayRuntime, default);
            await using (var reconciled = scenario.Factory.CreateDbContext())
                Assert.Equal("repository-source-resumed", (await reconciled.Jobs.SingleAsync(x => x.Id == owned.Job.JobId.Value)).Reason);
            return await ClaimRepositoryEmbedAsync(scenario);
        }
        if (boundary == "restore-checkpoint") replacement = await RestoreBlockedProofAsync(replacement, leaveUnstarted: false);
        if (boundary == "after-requeue-authority")
        {
            await InvalidateCaptureAsync();
            var refusal = await Assert.ThrowsAsync<RepositorySourceDeferredException>(async () => await checkpoint.ReadNextAsync(replacement, ReplayRuntime.Profile, default));
            replacement = await RequalifyCaptureAsync(replacement, refusal.Deferral);
        }
        var replay = await checkpoint.ReadNextAsync(replacement, ReplayRuntime.Profile, default);
        Assert.Equal(missing.Chunks, replay.Chunks);
        if (explicitTerminalGit)
        {
            await using (var check = scenario.Factory.CreateDbContext())
            {
                var source = await check.SourceRevisions.SingleAsync(x => x.Id == check.PipelineRecords.Where(p => p.Id == replacement.Job.PipelineRecordId.Value).Select(p => p.SourceRevisionId).Single());
                Assert.False(SqlRepositoryWorkRecovery.RequiresRecovery(source));
                Assert.Empty(await check.EmbeddingGpuRequests.Where(x => x.State < 2).ToArrayAsync());
            }
            if (boundary == "terminal-during-handoff")
            {
                var guardedScheduler = new SqlGpuSchedulerStore(scenario.Factory, timeProvider: scenario.Clock, sourceAdmission: admission,
                    beforeIdempotencyRead: async _ => await InvalidateCaptureAsync());
                var guarded = new SqlEmbeddingGpuRequestStore(scenario.Factory, guardedScheduler, wake, ReplayRuntime, scenario.Clock, sourceAdmission: admission);
                await Assert.ThrowsAsync<RepositorySourceDeferredException>(() => guarded.QueueAsync(replacement, replay, default).AsTask());
                await using var stopped = scenario.Factory.CreateDbContext();
                Assert.Equal(failedAttempts + 2, await stopped.GpuMiniTasks.CountAsync());
            }
            else
            {
                await requests.QueueAsync(replacement, replay, default); var handle = await AdmitRepositoryGpuAsync(scheduler);
                await InvalidateCaptureAsync(); var models = new BgeGpuInferenceTests.RecordingModels();
                await new EmbeddingGpuExecutor(requests, new RepositoryReplayLifecycle(scheduler), scheduler,
                    new BgeGpuInferenceSession(models), new WindowsInteractiveGpuOwnerProbe(), ReplayRuntime,
                    new ChannelOutboxWakeSignal(), wake, scenario.Clock).DeliverAsync(handle, default);
                Assert.Empty(models.Events);
            }
            return;
        }
        if (boundary == "after-checkpoint-authority")
        {
            await InvalidateCaptureAsync();
            var refusal = await Assert.ThrowsAsync<RepositorySourceDeferredException>(async () => await requests.QueueAsync(replacement, replay, default));
            await using var unchanged = scenario.Factory.CreateDbContext();
            Assert.Equal(failedAttempts + 2, await unchanged.EmbeddingGpuRequests.CountAsync());
            replacement = await RequalifyCaptureAsync(replacement, refusal.Deferral);
            replay = await checkpoint.ReadNextAsync(replacement, ReplayRuntime.Profile, default);
            Assert.Equal(missing.Chunks, replay.Chunks);
        }
        await requests.QueueAsync(replacement, replay, default);
        await using (var queued = scenario.Factory.CreateDbContext())
            Assert.Equal(failedAttempts + 1, await queued.EmbeddingGpuRequests.CountAsync(x => x.ParentJobId == stranded.Job.JobId.Value &&
                x.InputDigest == queued.EmbeddingGpuRequests.Where(y => y.MiniTaskId == execution.MiniTaskId).Select(y => y.InputDigest).Single()));
        await DeliverRepositoryGpuAsync(requests, scheduler, wake, scenario.Clock, await AdmitRepositoryGpuAsync(scheduler));
        StageWorkItem? restoredWork = boundary == "restore-after-replacement"
            ? await RestoreBlockedProofAsync(await ClaimRepositoryEmbedAsync(scenario), leaveUnstarted: true) : null;
        for (var turn = 0; turn < 16; turn++)
        {
            var work = restoredWork ?? await ClaimRepositoryEmbedAsync(scenario); restoredWork = null;
            var batch = await checkpoint.ReadNextAsync(work, ReplayRuntime.Profile, default);
            if (batch.Chunks.Count == 0)
            {
                await new EmbedStageWorker(pipeline.Store, new NoRepositoryDirectInference(),
                    pipeline.Services.GetRequiredService<StageTransitionService>(), scenario.Clock, checkpoint, requests).ExecuteAsync(work, default);
                break;
            }
            await requests.QueueAsync(work, batch, default);
            await DeliverRepositoryGpuAsync(requests, scheduler, wake, scenario.Clock, await AdmitRepositoryGpuAsync(scheduler));
        }
        async Task RemoveReplayResultAsync()
        {
            await using var broken = scenario.Factory.CreateDbContext();
            var request = await broken.EmbeddingGpuRequests.SingleAsync(x => x.MiniTaskId != execution.MiniTaskId && x.InputDigest ==
                broken.EmbeddingGpuRequests.Where(y => y.MiniTaskId == execution.MiniTaskId).Select(y => y.InputDigest).Single());
            request.ResultDigest = null;
            (await broken.GpuMiniTasks.SingleAsync(x => x.Id == request.MiniTaskId)).ExecutionState = (int)GpuMiniTaskExecutionState.OutcomeUncertain;
            var task = await broken.GpuMiniTasks.SingleAsync(x => x.Id == request.MiniTaskId);
            (await broken.GpuBatches.SingleAsync(x => x.Id == task.BatchId)).State = (int)GpuBatchState.Released;
            await broken.SaveChangesAsync();
        }
        if (boundary is "publication-result" or "publication-race" or "publication-authority")
        {
            if (boundary == "publication-authority") afterPlacement = InvalidateCaptureAsync;
            else if (boundary == "publication-race") afterPlacement = RemoveReplayResultAsync;
            else await RemoveReplayResultAsync();
            await using var broken = scenario.Factory.CreateDbContext();
            var publish = await broken.Jobs.SingleAsync(x => x.PipelineRecordId == stranded.Job.PipelineRecordId.Value && x.Stage == 5);
            if (boundary == "publication-result")
                Assert.Equal(new("repository-gpu-outcome-cleanup-or-binding-unconfirmed", true),
                    await SqlRepositoryWorkRecovery.CheckCheckpointAsync(broken, publish, ReplayRuntime, default));
            await pipeline.PumpAsync();
            Assert.False((await broken.PipelineRecords.AsNoTracking().SingleAsync(x => x.Id == publish.PipelineRecordId)).CompletionCriteriaMet);
            Assert.Null((await broken.IndexState.AsNoTracking().SingleAsync()).ActiveIndexGenerationId);
            return;
        }
        await pipeline.PumpAsync();
        await using var verify = scenario.Factory.CreateDbContext();
        var record = await verify.PipelineRecords.SingleAsync(x => x.Id == stranded.Job.PipelineRecordId.Value);
        Assert.True(record.CompletionCriteriaMet, "The exact-group replacement did not reach publication.");
        Assert.Equal(failedEvidence, JsonSerializer.Serialize(await verify.EmbeddingGpuRequests.AsNoTracking().SingleAsync(x => x.MiniTaskId == execution.MiniTaskId)));
        var afterSiblings = await verify.Vectors.AsNoTracking().Where(x => savedSiblings.Select(y => y.VectorId).Contains(x.VectorId))
            .OrderBy(x => x.VectorId).Select(x => new { x.VectorId, x.PayloadChecksum, Version = Convert.ToBase64String(x.RowVersion) }).ToArrayAsync();
        Assert.Equal(savedSiblings, afterSiblings);
        Assert.Equal(await verify.TextChunks.CountAsync(), await verify.Vectors.CountAsync());
        var retrieval = new CorpusRetrievalService(new SqlCorpusRetrievalReader(scenario.Factory),
            new GitPublicationEvidenceCodec(), new LocalPrivateContentDisclosure());
        Assert.Single((await retrieval.SearchAsync(new("Repository replay section 0.", 5, "root", root.Id.Value, null, "literal"), default)).Results);
    }

    private static async Task<EmbeddingGpuExecutionWork> FailRepositoryGroupAsync(Scenario scenario, SqlEmbeddingGpuRequestStore requests,
        SqlGpuSchedulerStore scheduler, GpuExecutorBatchHandle handle)
    {
        Assert.True((await scheduler.AcknowledgeAsync(new(Guid.NewGuid(), handle), default)).Committed);
        var instance = Guid.NewGuid();
        var execution = Assert.IsType<EmbeddingGpuExecutionWork>(await requests.ClaimExecutionAsync(handle, instance,
            Guid.NewGuid(), new WindowsInteractiveGpuOwnerProbe().Current, default));
        // No native model is allocated. Generate genuine cleanup and settlement receipts.
        await requests.RecordNativeCleanupAsync(handle, instance, execution.ClaimOperationId, default);
        var reservation = Assert.Single(await scheduler.ReadStaleCapacityReservationsAsync(scenario.Clock.Now.AddMinutes(5), default));
        Assert.True((await scheduler.MarkCapacityUncertainAsync(Guid.NewGuid(), reservation, default)).Committed);
        var outcome = new GpuExecutorTrustedEvidence(Guid.NewGuid(), handle, "synthetic-confirmed-cleanup", scenario.Clock.Now,
            GpuExecutorEvidenceClass.TaskOutcomeUncertainConfirmed);
        var capacity = outcome with { OperationId = Guid.NewGuid(), EvidenceClass = GpuExecutorEvidenceClass.CapacityReleaseConfirmed };
        Assert.True((await scheduler.RecordTrustedEvidenceAsync(outcome, default)).Committed);
        Assert.True((await scheduler.RecordTrustedEvidenceAsync(capacity, default)).Committed);
        Assert.True((await scheduler.ReconcileTaskOutcomeAsync(Guid.NewGuid(), new(handle, outcome.OperationId, execution.MiniTaskId), default)).Committed);
        Assert.True((await scheduler.ReconcileCapacityAsync(Guid.NewGuid(), new(handle, capacity.OperationId), default)).Committed);
        Assert.True(await requests.RequeueSettledAsync(handle, execution.MiniTaskId, default));
        scenario.Clock.Now += TimeSpan.FromSeconds(6);
        return execution;
    }

    private static async Task<StageWorkItem> ClaimRepositoryEmbedAsync(Scenario scenario)
    {
        var admission = new FixtureSourceAdmission(scenario.Factory);
        var delivery = Assert.IsType<ClaimedDispatchMessage>(await new SqlOutboxStore(scenario.Factory, embeddingRuntime: ReplayRuntime, sourceAdmission: admission)
            .ClaimNextDueAsync("repository-replay-dispatch", scenario.Clock.Now, TimeSpan.FromMinutes(2), [PipelineOperations.Embed], default));
        var job = Assert.IsType<ClaimedJob>(await new SqlJobClaimStore(scenario.Factory, sourceAdmission: admission)
            .ClaimForDispatchAsync(delivery, "repository-replay-worker", scenario.Clock.Now, TimeSpan.FromMinutes(2), default));
        return new(delivery, job);
    }

    private static async Task<GpuExecutorBatchHandle> AdmitRepositoryGpuAsync(SqlGpuSchedulerStore scheduler)
    {
        await scheduler.RunAdmissionRoundAsync(Guid.NewGuid(), GpuSchedulerWakeReason.WorkReady,
            new(4, 4096, TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(1)),
            (_, _) => ValueTask.FromResult(new GpuAdmissionDecision(GpuAdmissionDisposition.Admit,
                "repository-replay-gpu", "repository-replay-owner", null, EmbeddingGpuExecutor.Name)), default);
        return Assert.Single(await scheduler.ReadPendingDispatchesAsync(default));
    }

    private static ValueTask DeliverRepositoryGpuAsync(SqlEmbeddingGpuRequestStore requests, SqlGpuSchedulerStore scheduler,
        ChannelGpuSchedulerWakeSignal wake, TimeProvider clock, GpuExecutorBatchHandle handle) =>
        new EmbeddingGpuExecutor(requests, new RepositoryReplayLifecycle(scheduler), scheduler,
            new BgeGpuInferenceSession(new BgeGpuInferenceTests.RecordingModels()), new WindowsInteractiveGpuOwnerProbe(), ReplayRuntime,
            new ChannelOutboxWakeSignal(), wake, clock).DeliverAsync(handle, default);

    private sealed class NoRepositoryDirectInference : IEmbeddingProvider
    {
        public ValueTask<EmbeddingResult> CreateEmbeddingAsync(string text, CancellationToken ct) =>
            throw new InvalidOperationException("The fixture must use its durable GPU handoff.");
    }

    private sealed class RepositoryReplayLifecycle(SqlGpuSchedulerStore scheduler) : IGpuExecutorLifecycleSink
    {
        public ValueTask<GpuExecutorDispatchMutationResult> AcknowledgeAsync(GpuExecutorAcknowledgement value, CancellationToken ct) => scheduler.AcknowledgeAsync(value, ct);
        public ValueTask<GpuExecutorDispatchMutationResult> MarkDeliveryUncertainAsync(GpuExecutorDeliveryUncertainty value, CancellationToken ct) => scheduler.MarkDeliveryUncertainAsync(value, ct);
        public ValueTask<GpuExecutorDispatchMutationResult> RecordReceiptAsync(GpuExecutorResultReceipt value, CancellationToken ct) => scheduler.RecordReceiptAsync(value, ct);
        public ValueTask<GpuExecutorDispatchMutationResult> RecordTrustedEvidenceAsync(GpuExecutorTrustedEvidence value, CancellationToken ct) => scheduler.RecordTrustedEvidenceAsync(value, ct);
        public ValueTask<GpuBatchCallbackResult> HandleCallbackAsync(Guid operation, GpuBatchCallback value, CancellationToken ct) => scheduler.ApplyBatchCallbackAsync(operation, value, ct);
    }
}
