using System.Diagnostics;
using System.Text.Json;
using FluxKnowledge.Application.Contracts;
using FluxKnowledge.Application.Gpu;
using FluxKnowledge.Application.Indexing;
using FluxKnowledge.Application.IntegrationV1;
using FluxKnowledge.Application.IntegrationV1.Code;
using FluxKnowledge.Application.IntegrationV1.Corpus;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Pipeline;
using FluxKnowledge.Application.Workers;
using FluxKnowledge.Application.Search;
using FluxKnowledge.Application.Sources;
using FluxKnowledge.Application.Visibility;
using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Domain.Pipeline;
using FluxKnowledge.Domain.Jobs;
using FluxKnowledge.Domain.Gpu;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using FluxKnowledge.Infrastructure.SqlServer.Search;
using FluxKnowledge.Infrastructure.SqlServer.Visibility;
using FluxKnowledge.Infrastructure.SqlServer.Workers;
using FluxKnowledge.Integrations.Windows;
using FluxKnowledge.Integrations.Files;
using FluxKnowledge.Integration.Tests.Indexing;
using FluxKnowledge.Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Sources;

[Collection("sql-full-text")]
public sealed class RepositoryWorkRecoveryIntegrationTests(NativeSqlServerFixture fixture) : IClassFixture<NativeSqlServerFixture>
{
    [NativeSqlServerTheory]
    [InlineData("identical")]
    [InlineData("revised-in-place")]
    [InlineData("pause-resume")]
    [InlineData("changed-content")]
    [InlineData("exclusion")]
    [InlineData("policy")]
    [InlineData("paused")]
    [InlineData("deletion")]
    [InlineData("retention")]
    [InlineData("epoch")]
    [InlineData("profile")]
    [InlineData("corrupt-vector")]
    [InlineData("prior-processor")]
    [InlineData("legacy-binding")]
    [InlineData("rediscovery-before-deferral")]
    public async Task Withdrawn_repository_checkpoint_is_deferred_without_losing_existing_work(string change)
    {
        await SqlTestData.ClearPhase3SourceDataAsync(fixture);
        var clock = new Clock();
        await using var environment = await SqlToUsearchRebuildTests.PipelineEnvironment.CreateAsync(fixture, "Baseline", embed: false, clock: clock);
        // Keep the unrelated unrooted baseline out of this exact-source claim.
        var baseline = await new SqlOutboxStore(environment.Factory).ClaimNextDueAsync("baseline", clock.Now, TimeSpan.FromMinutes(2), [PipelineOperations.Embed], default);
        await using (var setup = await environment.Factory.CreateDbContextAsync())
        {
            await setup.Jobs.Where(value => value.PipelineRecordId == baseline!.PipelineRecordId.Value).ExecuteUpdateAsync(set => set.SetProperty(value => value.PublicState, (int)PublicJobState.Failed));
        }
        using var repository = new Repository();
        repository.Write("Guide.md", string.Join("\n\n", Enumerable.Range(0, 12).Select(i => $"Recovery section {i}. " + new string('q', 900))));
        repository.Git("add", ".");
        var commands = Commands(repository.Path, environment.Factory, clock);
        await Commit(commands, Mutation("root_create", new { path = repository.Path, displayName = "Withdrawal", discoveryMode = "git-tracked", indexSourceText = true }));
        var scans = new SqlSourceScanStore(environment.Factory, clock);
        var rootId = await ScanAndPublish(environment, scans, clock);
        if (change == "revised-in-place")
        {
            var originalDispatch = Assert.IsType<ClaimedDispatchMessage>(await new SqlOutboxStore(environment.Factory)
                .ClaimNextDueAsync("original-revision", clock.Now, TimeSpan.FromMinutes(2), [PipelineOperations.Embed], default));
            var originalJob = Assert.IsType<ClaimedJob>(await new SqlJobClaimStore(environment.Factory)
                .ClaimForDispatchAsync(originalDispatch, "original-revision", clock.Now, TimeSpan.FromMinutes(2), default));
            var originalTransitions = new StageTransitionService(new SqlStageTransitionStore(environment.Factory, null, clock),
                new RecoveryStatusPublisher(), new ChannelOutboxWakeSignal(), clock);
            await new EmbedStageWorker(environment.Store, new RecoveryEmbeddingProvider(), originalTransitions, clock)
                .ExecuteAsync(new(originalDispatch, originalJob), default);
            await environment.PumpAsync();
            repository.Write("Guide.md", File.ReadAllText(Path.Combine(repository.Path, "Guide.md")) + "\n\nRevision two remains eligible.");
            await Commit(commands, Mutation("source_sync", new { rootId }));
            await ScanAndPublish(environment, scans, clock);
            await using var revised = await environment.Factory.CreateDbContextAsync();
            var current = await revised.SourceRevisions.SingleAsync(value => value.SourceRootId == rootId && value.SuppressedAtUtc == null);
            Assert.Equal(2, current.Revision);
            Assert.NotNull(current.ParentSourceRevisionId);
            Assert.NotNull((await revised.PipelineRecords.SingleAsync(value => value.SourceRevisionId == current.Id)).RepositoryRecoveryBindingJson);
        }
        if (change == "prior-processor")
        {
            // Simulate an upgrade: the original enrolment and its immutable activity
            // agree with one another, but use a prior unsupported processing contract.
            await using var previous = await environment.Factory.CreateDbContextAsync();
            var record = await previous.PipelineRecords.AsNoTracking().SingleAsync(value => value.SourceRevisionId != null);
            var activity = await previous.SourceActivities.AsNoTracking().SingleAsync(value => value.ResultingPipelineRecordId == record.Id);
            const string oldVersion = "prior-unsupported-text-contract";
            const string oldDescriptor = "prior-unsupported-descriptor";
            var hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new {
                activity.SourceRevisionId, activity.ActivityKind, activity.ExecutionClass, ProcessorVersion = oldVersion,
                activity.InputFingerprint, DescriptorFingerprint = oldDescriptor, activity.RequiredCapability })));
            var binding = System.Text.Json.Nodes.JsonNode.Parse(record.RepositoryRecoveryBindingJson!)!;
            binding["Activity"] = hash;
            await previous.Database.ExecuteSqlInterpolatedAsync($"UPDATE SourceActivities SET ProcessorVersion={oldVersion}, DescriptorFingerprint={oldDescriptor} WHERE Id={activity.Id}");
            await previous.Database.ExecuteSqlInterpolatedAsync($"UPDATE PipelineRecords SET RepositoryRecoveryBindingJson={binding.ToJsonString()} WHERE Id={record.Id}");
        }
        var dispatch = Assert.IsType<FluxKnowledge.Application.Workers.ClaimedDispatchMessage>(await new SqlOutboxStore(environment.Factory)
            .ClaimNextDueAsync("withdrawal", clock.Now, TimeSpan.FromMinutes(2), [PipelineOperations.Embed], default));
        var job = Assert.IsType<ClaimedJob>(await new SqlJobClaimStore(environment.Factory).ClaimForDispatchAsync(dispatch, "withdrawal", clock.Now, TimeSpan.FromMinutes(2), default));
        var work = new FluxKnowledge.Application.Workers.StageWorkItem(dispatch, job);
        var provider = new RecoveryEmbeddingProvider();
        var checkpoints = new SqlEmbeddingCheckpointStore(environment.Factory, clock);
        var batch = await checkpoints.ReadNextAsync(work, provider.Profile, default);
        var initialResults = await provider.CreateEmbeddingsAsync(batch.Chunks.Select(chunk => chunk.SearchText).ToArray(), default);
        await checkpoints.CommitAsync(work, batch, initialResults, default);
        byte[][] saved;
        await using (var before = await environment.Factory.CreateDbContextAsync())
            saved = await before.Vectors.Where(value => value.IndexGenerationId == batch.GenerationId).OrderBy(value => value.VectorId).Select(value => value.Values).ToArrayAsync();
        if (change == "legacy-binding")
        {
            await using var legacy = await environment.Factory.CreateDbContextAsync();
            await legacy.Database.ExecuteSqlInterpolatedAsync($"UPDATE PipelineRecords SET RepositoryRecoveryBindingJson=NULL WHERE Id={job.PipelineRecordId.Value}");
        }
        provider.Inputs.Clear();
        File.Move(Path.Combine(repository.Path, "Guide.md"), Path.Combine(repository.Path, "withdrawn.tmp"));
        await Commit(commands, Mutation("source_sync", new { rootId }));
        await ScanAndPublish(environment, scans, clock);
        await using (var suppressed = await environment.Factory.CreateDbContextAsync())
        {
            var retainedId = (await suppressed.PipelineRecords.SingleAsync(value => value.Id == job.PipelineRecordId.Value)).SourceRevisionId;
            Assert.NotNull((await suppressed.SourceRevisions.SingleAsync(value => value.Id == retainedId)).SuppressedAtUtc);
        }
        var transitions = new FluxKnowledge.Application.Pipeline.StageTransitionService(new SqlStageTransitionStore(environment.Factory, null, clock),
            new RecoveryStatusPublisher(), new FluxKnowledge.Infrastructure.SqlServer.Workers.ChannelOutboxWakeSignal(), clock);
        var refusal = await Assert.ThrowsAsync<RepositorySourceDeferredException>(() =>
            new FluxKnowledge.Application.Indexing.EmbedStageWorker(environment.Store, provider, transitions, clock, checkpoints).ExecuteAsync(work, default).AsTask());
        if (change == "rediscovery-before-deferral")
        {
            File.Move(Path.Combine(repository.Path, "withdrawn.tmp"), Path.Combine(repository.Path, "Guide.md"));
            await Commit(commands, Mutation("source_sync", new { rootId }));
            await ScanAndPublish(environment, scans, clock);
        }
        var retry = new StageRetryRequest(dispatch, job, clock.Now.AddMinutes(1), "repository-source-deferred", "test", refusal.Deferral);
        await transitions.RetryAsync(retry, default);
        await transitions.RetryAsync(retry, default);
        await using var verification = await environment.Factory.CreateDbContextAsync();
        Assert.Equal((int)PublicJobState.WorkerQueued, (await verification.Jobs.FindAsync(job.JobId.Value))!.PublicState);
        Assert.Equal(change == "rediscovery-before-deferral" ? "repository-source-resumed" : change == "legacy-binding" ? "repository-source-blocked" : "repository-source-deferred", (await verification.Jobs.FindAsync(job.JobId.Value))!.Reason);
        Assert.Equal(batch.Chunks.Count, await verification.Vectors.CountAsync(value => value.IndexGenerationId == batch.GenerationId));
        var attempts = (await verification.Jobs.FindAsync(job.JobId.Value))!.AttemptCount;
        if (change == "identical")
        {
            var previousDelay = 60;
            for (var wake = 0; wake < 6; wake++)
            {
                var waiting = await verification.Jobs.AsNoTracking().SingleAsync(value => value.Id == job.JobId.Value);
                clock.Advance(waiting.DueAtUtc - clock.Now);
                Assert.Null(await new SqlOutboxStore(environment.Factory).ClaimNextDueAsync("restarted-while-absent", clock.Now, TimeSpan.FromMinutes(2), [PipelineOperations.Embed], default));
                waiting = await verification.Jobs.AsNoTracking().SingleAsync(value => value.Id == job.JobId.Value);
                using var details = JsonDocument.Parse(waiting.ErrorDetails!);
                var delay = details.RootElement.GetProperty("CheckAfterSeconds").GetInt32();
                Assert.Equal(Math.Min(previousDelay * 2, 900), delay);
                Assert.Equal(clock.Now.AddSeconds(delay), waiting.DueAtUtc);
                Assert.Equal(attempts, waiting.AttemptCount);
                Assert.Equal("repository-source-deferred", waiting.Reason);
                Assert.Empty(provider.Inputs);
                previousDelay = delay;
            }
        }
        clock.Advance(TimeSpan.FromDays(1));
        if (change != "rediscovery-before-deferral")
        {
        Assert.Null(await new SqlOutboxStore(environment.Factory).ClaimNextDueAsync("still-absent", clock.Now, TimeSpan.FromMinutes(2), [PipelineOperations.Embed], default));
        Assert.Null(await new SqlJobClaimStore(environment.Factory).ClaimNextDueAsync("still-absent", clock.Now, TimeSpan.FromMinutes(2), default));
        Assert.Equal(attempts, (await verification.Jobs.AsNoTracking().SingleAsync(value => value.Id == job.JobId.Value)).AttemptCount);
        Assert.Empty(provider.Inputs);
        }

        // A new store and the real native scan path recover an interrupted process/wake.
        if (change != "rediscovery-before-deferral") File.Move(Path.Combine(repository.Path, "withdrawn.tmp"), Path.Combine(repository.Path, "Guide.md"));
        if (change == "changed-content") repository.Replace("Guide.md", "Replacement content has its own indexing work.");
        if (change is "pause-resume" or "paused") await Commit(commands, Mutation("root_pause", new { rootId }));
        if (change == "pause-resume") await Commit(commands, Mutation("root_resume", new { rootId }));
        if (change == "deletion") await Commit(commands, Mutation("root_delete", new { rootId }));
        if (change is "exclusion" or "policy" or "retention" or "epoch" or "profile" or "corrupt-vector")
        {
            await using var fault = await environment.Factory.CreateDbContextAsync();
            var root = await fault.SourceRootConfigurations.SingleAsync(value => value.Id == rootId);
            if (change == "exclusion") { root.ExcludePatternsJson = "[\"Guide.md\"]"; root.ConfigurationRevision++; }
            if (change == "policy") { root.MaximumFileBytes++; root.ConfigurationRevision++; }
            if (change == "retention") clock.Advance(TimeSpan.FromDays(31));
            if (change == "epoch") (await fault.IndexState.SingleAsync()).CorpusEpoch = Guid.NewGuid();
            if (change == "profile") await fault.Database.ExecuteSqlInterpolatedAsync($"UPDATE IndexGenerations SET ModelFingerprint='different-profile' WHERE Id={batch.GenerationId}");
            if (change == "corrupt-vector") await fault.Database.ExecuteSqlInterpolatedAsync($"UPDATE Vectors SET PayloadChecksum={new string('f',64)} WHERE IndexGenerationId={batch.GenerationId}");
            await fault.SaveChangesAsync();
        }
        if (change is "paused" or "deletion")
        {
            await new SqlOutboxStore(environment.Factory).ClaimNextDueAsync("ineligible", clock.Now, TimeSpan.FromMinutes(2), [PipelineOperations.Embed], default);
        }
        else
        {
        await Commit(commands, Mutation("source_sync", new { rootId }));
        await ScanAndPublish(environment, new SqlSourceScanStore(environment.Factory, clock), clock);
        }
        var resumed = await verification.Jobs.AsNoTracking().SingleAsync(value => value.Id == job.JobId.Value);
        if (change is not ("identical" or "revised-in-place" or "pause-resume" or "rediscovery-before-deferral"))
        {
            Assert.Contains(resumed.Reason, new[] { "repository-source-deferred", "repository-source-blocked" });
            Assert.Equal(attempts, resumed.AttemptCount);
            Assert.Empty(provider.Inputs);
            Assert.Equal(saved, await verification.Vectors.AsNoTracking().Where(value => value.IndexGenerationId == batch.GenerationId).OrderBy(value => value.VectorId).Select(value => value.Values).ToArrayAsync());
            if (change == "prior-processor") Assert.Contains("repository-current-processing-contract-incompatible", resumed.ErrorDetails);
            if (change == "legacy-binding") Assert.Contains("repository-original-binding-missing-explicit-recovery-required", resumed.ErrorDetails);
            if (change == "changed-content")
            {
                Assert.Equal(2, await verification.SourceRevisions.CountAsync(value => value.SourceRootId == rootId));
                var replacement = await verification.PipelineRecords.SingleAsync(value => value.SourceRevisionId != null && value.Id != job.PipelineRecordId.Value);
                Assert.NotNull(replacement.RepositoryRecoveryBindingJson);
                Assert.Single(await verification.Jobs.Where(value => value.PipelineRecordId == replacement.Id && value.Stage == (int)PipelineStage.Embed).ToListAsync());
            }
            return;
        }
        Assert.Equal("repository-source-resumed", resumed.Reason);
        // Duplicate scans cannot create a second job or reset the resumed delivery.
        await Commit(commands, Mutation("source_sync", new { rootId }));
        await ScanAndPublish(environment, new SqlSourceScanStore(environment.Factory, clock), clock);
        Assert.Single(await verification.Jobs.Where(value => value.PipelineRecordId == job.PipelineRecordId.Value && value.Stage == (int)PipelineStage.Embed).ToListAsync());
        Assert.Single(await verification.OutboxMessages.Where(value => value.JobId == job.JobId.Value).ToListAsync());
        while (true)
        {
            var nextDispatch = Assert.IsType<ClaimedDispatchMessage>(await new SqlOutboxStore(environment.Factory).ClaimNextDueAsync("returned", clock.Now, TimeSpan.FromMinutes(2), [PipelineOperations.Embed], default));
            var nextJob = Assert.IsType<ClaimedJob>(await new SqlJobClaimStore(environment.Factory).ClaimForDispatchAsync(nextDispatch, "returned", clock.Now, TimeSpan.FromMinutes(2), default));
            Assert.Equal(job.JobId, nextJob.JobId);
            Assert.Equal(dispatch.DispatchMessageId, nextDispatch.DispatchMessageId);
            await Assert.ThrowsAsync<InvalidOperationException>(() => transitions.RetryAsync(retry, default).AsTask());
            Assert.Equal("embedding-checkpoint-lease-lost", (await Assert.ThrowsAsync<InvalidOperationException>(() =>
                checkpoints.CommitAsync(work, batch, initialResults, default).AsTask())).Message);
            await new FluxKnowledge.Application.Indexing.EmbedStageWorker(environment.Store, provider, transitions, clock, new SqlEmbeddingCheckpointStore(environment.Factory, clock)).ExecuteAsync(new(nextDispatch, nextJob), default);
            if (await verification.Artifacts.AnyAsync(value => value.PipelineRecordId == job.PipelineRecordId.Value && value.Stage == (int)PipelineStage.Embed)) break;
        }
        var vectors = await verification.Vectors.AsNoTracking().Where(value => value.IndexGenerationId == batch.GenerationId).OrderBy(value => value.VectorId).ToArrayAsync();
        Assert.Equal(await verification.TextChunks.CountAsync(value => value.Artifact.PipelineRecordId == job.PipelineRecordId.Value) - saved.Length, provider.Inputs.Count);
        Assert.Equal(saved, vectors.Take(saved.Length).Select(value => value.Values).ToArray());
        await environment.PumpAsync();
        Assert.True((await verification.PipelineRecords.AsNoTracking().SingleAsync(value => value.Id == job.PipelineRecordId.Value)).CompletionCriteriaMet);
        var retrieval = new CorpusRetrievalService(new SqlCorpusRetrievalReader(environment.Factory), new EvidenceCodec(), new LocalPrivateContentDisclosure());
        var hits = (await retrieval.SearchAsync(new CorpusSearchRequest("Recovery section", 10, "root", rootId, null), default)).Results;
        Assert.NotEmpty(hits);
        var hit = hits[0];
        var read = await retrieval.ReadAsync(new CorpusReadRequest(hit.EvidenceRef, 0), default);
        Assert.Equal(hit.StartOffset, read.CitedStart);
        Assert.Contains("Recovery section", read.Text);
    }

    private sealed class RecoveryEmbeddingProvider : IBatchedEmbeddingProvider
    {
        public List<string> Inputs { get; } = [];
        public EmbeddingProfile Profile => new(FluxKnowledge.Infrastructure.Inference.DeterministicTokenHashEmbeddingProvider.Fingerprint, 256);
        public int MaximumBatchSize => 4;
        public ValueTask<EmbeddingResult> CreateEmbeddingAsync(string text, CancellationToken ct) => new FluxKnowledge.Infrastructure.Inference.DeterministicTokenHashEmbeddingProvider().CreateEmbeddingAsync(text, ct);
        public async ValueTask<IReadOnlyList<EmbeddingResult>> CreateEmbeddingsAsync(IReadOnlyList<string> texts, CancellationToken ct)
        {
            Inputs.AddRange(texts);
            var results = new List<EmbeddingResult>();
            foreach (var text in texts) results.Add(await CreateEmbeddingAsync(text, ct));
            return results;
        }
    }

    private sealed class RecoveryStatusPublisher : IStatusEventPublisher
    {
        public ValueTask PublishAsync(StatusChanged change, CancellationToken ct) => ValueTask.CompletedTask;
    }

    [NativeSqlServerTheory]
    [InlineData("known")]
    [InlineData("uncertain")]
    [InlineData("cleanup-unconfirmed")]
    [InlineData("lost-owner")]
    [InlineData("paused-uncertain")]
    [InlineData("rediscovery-before-deferral-known")]
    public async Task Withdrawn_repository_gpu_work_requires_a_known_result_and_confirmed_cleanup(string outcome)
    {
        await SqlTestData.ClearPhase3SourceDataAsync(fixture);
        var clock = new Clock();
        await using var environment = await SqlToUsearchRebuildTests.PipelineEnvironment.CreateAsync(fixture, "Baseline", embed: false, clock: clock);
        await DiscardBaselineAsync(environment, clock);
        using var repository = new Repository();
        repository.Write("Guide.md", string.Join("\n\n", Enumerable.Range(0, 12).Select(i => $"GPU recovery section {i}. " + new string('q', 900))));
        repository.Git("add", ".");
        var commands = Commands(repository.Path, environment.Factory, clock);
        await Commit(commands, Mutation("root_create", new { path = repository.Path, displayName = "GPU withdrawal", discoveryMode = "git-tracked", indexSourceText = true }));
        var provider = new RecoveryEmbeddingProvider();
        var runtime = new EmbeddingGpuRuntime("repository-recovery-synthetic", new string('b', 64), provider.Profile, 1024);
        var scans = new SqlSourceScanStore(environment.Factory, clock, runtime);
        var rootId = await ScanAndPublish(environment, scans, clock);
        var dispatch = Assert.IsType<ClaimedDispatchMessage>(await new SqlOutboxStore(environment.Factory).ClaimNextDueAsync("gpu-withdrawal", clock.Now, TimeSpan.FromMinutes(2), [PipelineOperations.Embed], default));
        var job = Assert.IsType<ClaimedJob>(await new SqlJobClaimStore(environment.Factory).ClaimForDispatchAsync(dispatch, "gpu-withdrawal", clock.Now, TimeSpan.FromMinutes(2), default));
        var worker = new StageWorkItem(dispatch, job);
        var checkpoints = new SqlEmbeddingCheckpointStore(environment.Factory, clock, runtime);
        var batch = await checkpoints.ReadNextAsync(worker, provider.Profile, default);
        await checkpoints.CommitAsync(worker, batch, await provider.CreateEmbeddingsAsync(batch.Chunks.Select(value => value.SearchText).ToArray(), default), default);
        var missing = await checkpoints.ReadNextAsync(worker, provider.Profile, default);
        var scheduler = new SqlGpuSchedulerStore(environment.Factory, timeProvider: clock);
        var requests = new SqlEmbeddingGpuRequestStore(environment.Factory, scheduler, new ChannelGpuSchedulerWakeSignal(), runtime, clock);
        await requests.QueueAsync(worker, missing, default);
        await using var context = await environment.Factory.CreateDbContextAsync();
        context.GpuCapacitySlots.Add(new GpuCapacitySlotEntity { SlotKey = "gpu-0", UpdatedAtUtc = clock.Now });
        await context.SaveChangesAsync();
        await scheduler.RunAdmissionRoundAsync(Guid.NewGuid(), GpuSchedulerWakeReason.WorkReady,
            new(4, 4096, TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(1)),
            (_, _) => ValueTask.FromResult(new GpuAdmissionDecision(GpuAdmissionDisposition.Admit, "gpu-0", "owner", null, EmbeddingGpuExecutor.Name)), default);
        var handle = Assert.Single(await scheduler.ReadPendingDispatchesAsync(default));
        Assert.True((await scheduler.AcknowledgeAsync(new(Guid.NewGuid(), handle), default)).Committed);
        var instance = Guid.NewGuid();
        var execution = Assert.IsType<EmbeddingGpuExecutionWork>(await requests.ClaimExecutionAsync(handle, instance, Guid.NewGuid(), new WindowsInteractiveGpuOwnerProbe().Current, default));
        if (outcome is not ("uncertain" or "paused-uncertain"))
            await requests.CommitAsync(handle, instance, execution, await provider.CreateEmbeddingsAsync(execution.Batch.Chunks.Select(value => value.SearchText).ToArray(), default), default);
        var saved = await context.Vectors.AsNoTracking().Where(value => value.IndexGenerationId == batch.GenerationId).OrderBy(value => value.VectorId).Select(value => value.Values).ToArrayAsync();
        provider.Inputs.Clear();
        if (outcome == "paused-uncertain") await Commit(commands, Mutation("root_pause", new { rootId }));
        else
        {
            File.Move(Path.Combine(repository.Path, "Guide.md"), Path.Combine(repository.Path, "withdrawn.tmp"));
            await Commit(commands, Mutation("source_sync", new { rootId }));
            await ScanAndPublish(environment, scans, clock);
        }
        if (outcome == "lost-owner")
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => requests.RecordNativeCleanupAsync(handle, Guid.NewGuid(), execution.ClaimOperationId, default).AsTask());
            Assert.False(await requests.RequeueSettledAsync(handle, execution.MiniTaskId, default));
        }
        else if (outcome == "cleanup-unconfirmed")
            Assert.False(await requests.RequeueSettledAsync(handle, execution.MiniTaskId, default));
        else
        {
            await requests.RecordNativeCleanupAsync(handle, instance, execution.ClaimOperationId, default);
            var completion = Assert.IsType<EmbeddingGpuCompletion>(await requests.ReadPendingCompletionAsync(handle, default));
            var known = outcome is "known" or "rediscovery-before-deferral-known";
            var disposition = known ? GpuMiniTaskBoundaryDisposition.Completed : GpuMiniTaskBoundaryDisposition.OutcomeUncertain;
            Assert.True((await scheduler.RecordReceiptAsync(new(Guid.NewGuid(), handle, execution.MiniTaskId, disposition, completion.ResultDigest,
                known ? GpuExecutorEvidenceClass.TaskOutcomeConfirmed : GpuExecutorEvidenceClass.TaskOutcomeUncertainConfirmed), default)).Committed);
            Assert.True((await scheduler.ApplyBatchCallbackAsync(Guid.NewGuid(), new(handle,
                known ? GpuBatchCallbackKind.Completed : GpuBatchCallbackKind.CapacityReleased, [new(execution.MiniTaskId, disposition)], CapacityReleased: true), default)).Committed);
            Assert.True(await requests.RequeueSettledAsync(handle, execution.MiniTaskId, default));
            Assert.True(await requests.RequeueSettledAsync(handle, execution.MiniTaskId, default));
        }
        if (outcome == "paused-uncertain") await Commit(commands, Mutation("root_resume", new { rootId }));
        else File.Move(Path.Combine(repository.Path, "withdrawn.tmp"), Path.Combine(repository.Path, "Guide.md"));
        await Commit(commands, Mutation("source_sync", new { rootId }));
        await ScanAndPublish(environment, new SqlSourceScanStore(environment.Factory, clock, runtime), clock);
        var settled = await context.Jobs.AsNoTracking().SingleAsync(value => value.Id == job.JobId.Value);
        if (outcome is "known" or "rediscovery-before-deferral-known")
        {
            Assert.Equal("repository-source-resumed", settled.Reason);
            var nextDispatch = Assert.IsType<ClaimedDispatchMessage>(await new SqlOutboxStore(environment.Factory, embeddingRuntime: runtime).ClaimNextDueAsync("returned", clock.Now, TimeSpan.FromMinutes(2), [PipelineOperations.Embed], default));
            var nextJob = Assert.IsType<ClaimedJob>(await new SqlJobClaimStore(environment.Factory).ClaimForDispatchAsync(nextDispatch, "returned", clock.Now, TimeSpan.FromMinutes(2), default));
            if (outcome == "rediscovery-before-deferral-known")
            {
                File.Move(Path.Combine(repository.Path, "Guide.md"), Path.Combine(repository.Path, "withdrawn.tmp"));
                await Commit(commands, Mutation("source_sync", new { rootId })); await ScanAndPublish(environment, scans, clock);
                var refusal = await Assert.ThrowsAsync<RepositorySourceDeferredException>(() => checkpoints.ReadNextAsync(new(nextDispatch, nextJob), provider.Profile, default).AsTask());
                File.Move(Path.Combine(repository.Path, "withdrawn.tmp"), Path.Combine(repository.Path, "Guide.md"));
                await Commit(commands, Mutation("source_sync", new { rootId })); await ScanAndPublish(environment, scans, clock);
                var services = new ServiceCollection();
                services.AddSingleton(environment.Factory); services.AddSingleton<TimeProvider>(clock); services.AddSingleton(runtime);
                services.AddFluxKnowledgeOutboxWorkers();
                await using var composition = services.BuildServiceProvider();
                using var scope = composition.CreateScope();
                await scope.ServiceProvider.GetRequiredService<IStageTransitionStore>().RetryAsync(new(nextDispatch, nextJob, clock.Now.AddMinutes(1), "repository-source-deferred", "test", refusal.Deferral), default);
                Assert.Equal("repository-source-resumed", (await context.Jobs.AsNoTracking().SingleAsync(value => value.Id == job.JobId.Value)).Reason);
                clock.Advance(TimeSpan.FromMinutes(1));
                nextDispatch = Assert.IsType<ClaimedDispatchMessage>(await new SqlOutboxStore(environment.Factory, embeddingRuntime: runtime).ClaimNextDueAsync("returned-again", clock.Now, TimeSpan.FromMinutes(2), [PipelineOperations.Embed], default));
                nextJob = Assert.IsType<ClaimedJob>(await new SqlJobClaimStore(environment.Factory).ClaimForDispatchAsync(nextDispatch, "returned-again", clock.Now, TimeSpan.FromMinutes(2), default));
            }
            var remaining = await new SqlEmbeddingCheckpointStore(environment.Factory, clock, runtime).ReadNextAsync(new(nextDispatch, nextJob), provider.Profile, default);
            Assert.DoesNotContain(remaining.Chunks, value => batch.Chunks.Concat(missing.Chunks).Any(savedChunk => savedChunk.Id == value.Id));
        }
        else
        {
            var uncertain = outcome is "uncertain" or "paused-uncertain";
            Assert.Equal(uncertain ? "repository-source-blocked" : null, settled.Reason);
            Assert.Null(await new SqlOutboxStore(environment.Factory, embeddingRuntime: runtime).ClaimNextDueAsync("blocked", clock.Now.AddDays(1), TimeSpan.FromMinutes(2), [PipelineOperations.Embed], default));
            Assert.Equal(uncertain ? (int)PublicJobState.WorkerQueued : (int)PublicJobState.GpuProcessing, settled.PublicState);
            if (uncertain) Assert.Contains("outcome-cleanup-or-binding-unconfirmed", settled.ErrorDetails!);
        }
        Assert.Equal(saved, await context.Vectors.AsNoTracking().Where(value => value.IndexGenerationId == batch.GenerationId).OrderBy(value => value.VectorId).Select(value => value.Values).ToArrayAsync());
        Assert.Empty(provider.Inputs);
        Assert.Single(await context.EmbeddingGpuRequests.ToListAsync());
    }

    private static async Task DiscardBaselineAsync(SqlToUsearchRebuildTests.PipelineEnvironment environment, Clock clock)
    {
        var dispatch = Assert.IsType<ClaimedDispatchMessage>(await new SqlOutboxStore(environment.Factory).ClaimNextDueAsync("baseline", clock.Now, TimeSpan.FromMinutes(2), [PipelineOperations.Embed], default));
        await using var setup = await environment.Factory.CreateDbContextAsync();
        await setup.Jobs.Where(value => value.PipelineRecordId == dispatch.PipelineRecordId.Value).ExecuteUpdateAsync(set => set.SetProperty(value => value.PublicState, (int)PublicJobState.Failed));
    }

    [NativeSqlServerFact]
    public async Task Rejected_identical_rediscovery_does_not_restore_previously_published_text_visibility()
    {
        await SqlTestData.ClearPhase3SourceDataAsync(fixture);
        var clock = new Clock();
        await using var environment = await SqlToUsearchRebuildTests.PipelineEnvironment.CreateAsync(fixture, "Baseline", clock: clock);
        using var repository = new Repository(); repository.Write("Guide.md", "Policy refusal sentinel remains private after withdrawal."); repository.Git("add", ".");
        var commands = Commands(repository.Path, environment.Factory, clock);
        await Commit(commands, Mutation("root_create", new { path = repository.Path, displayName = "Policy refusal", discoveryMode = "git-tracked", indexSourceText = true }));
        var scans = new SqlSourceScanStore(environment.Factory, clock);
        var rootId = await ScanAndPublish(environment, scans, clock);
        var retrieval = new CorpusRetrievalService(new SqlCorpusRetrievalReader(environment.Factory), new EvidenceCodec(), new LocalPrivateContentDisclosure());
        await AssertRepositoryPublishedAsync(environment);
        var original = Assert.Single((await retrieval.SearchAsync(new CorpusSearchRequest("Policy refusal sentinel", 10, "root", rootId, null), default)).Results);
        File.Move(Path.Combine(repository.Path, "Guide.md"), Path.Combine(repository.Path, "withdrawn.tmp"));
        await Commit(commands, Mutation("source_sync", new { rootId })); await ScanAndPublish(environment, scans, clock);
        File.Move(Path.Combine(repository.Path, "withdrawn.tmp"), Path.Combine(repository.Path, "Guide.md"));
        await using (var policy = await environment.Factory.CreateDbContextAsync())
        {
            var root = await policy.SourceRootConfigurations.SingleAsync(value => value.Id == rootId);
            root.AllowedClassificationsJson = "[\"application/pdf\"]"; root.ConfigurationRevision++;
            await policy.SaveChangesAsync();
        }
        await Commit(commands, Mutation("source_sync", new { rootId })); await ScanAndPublish(environment, scans, clock);
        Assert.Empty((await retrieval.SearchAsync(new CorpusSearchRequest("Policy refusal sentinel", 10, "root", rootId, null), default)).Results);
        await Assert.ThrowsAsync<NativeOperationException>(() => retrieval.ReadAsync(new CorpusReadRequest(original.EvidenceRef, 0), default).AsTask());
        await using var verify = await environment.Factory.CreateDbContextAsync();
        Assert.NotNull((await verify.SourceRevisions.SingleAsync(value => value.SourceRootId == rootId)).SuppressedAtUtc);
        Assert.NotEmpty(await verify.Vectors.ToListAsync());
    }

    [NativeSqlServerFact]
    public async Task Authorised_rebuild_of_a_paused_git_root_preserves_exact_job_lineage_through_publication()
    {
        await SqlTestData.ClearPhase3SourceDataAsync(fixture);
        var clock = new Clock();
        await using var environment = await SqlToUsearchRebuildTests.PipelineEnvironment.CreateAsync(fixture, "Rebuild baseline.", clock: clock);
        using var repository = new Repository(); repository.Write("Guide.md", "Paused rebuild sentinel has exact citations."); repository.Git("add", ".");
        var commands = Commands(repository.Path, environment.Factory, clock);
        await Commit(commands, Mutation("root_create", new { path = repository.Path, displayName = "Paused rebuild", discoveryMode = "git-tracked", indexSourceText = true }));
        var rootId = await ScanAndPublish(environment, new SqlSourceScanStore(environment.Factory, clock), clock);
        await AssertRepositoryPublishedAsync(environment);
        await Commit(commands, Mutation("root_pause", new { rootId }));
        await using (var context = await environment.Factory.CreateDbContextAsync())
        {
            context.GpuCapacitySlots.Add(new GpuCapacitySlotEntity { SlotKey = "slot-a", UpdatedAtUtc = clock.Now });
            await context.SaveChangesAsync();
        }
        var builder = new PassageBuilder(new RecoveryTokenizer());
        var rebuild = new SqlCorpusRebuildStore(environment.Factory, clock);
        var plan = await rebuild.ReadPlanAsync(Guid.NewGuid(), new RecoveryEmbeddingProvider().Profile, builder.PolicyFingerprint, default);
        await rebuild.CommitAsync(plan, "slot-a", default); environment.PermitRebuild(plan.OperationId);
        foreach (var input in plan.Inputs) await rebuild.PrepareAsync(plan.OperationId, input.PipelineRecordId, builder, default);
        await environment.PumpAsync();
        await using var verify = await environment.Factory.CreateDbContextAsync();
        var record = await verify.PipelineRecords.SingleAsync(value => value.SourceRevisionId != null);
        var jobs = await verify.Jobs.Where(value => value.PipelineRecordId == record.Id && value.Stage >= (int)PipelineStage.Embed).ToArrayAsync();
        Assert.Equal(4, jobs.Length);
        Assert.All(jobs, value => Assert.Equal((int)PublicJobState.Completed, value.PublicState));
        Assert.True(record.CompletionCriteriaMet);
        Assert.Equal((int)SourceRootState.Paused, (await verify.SourceRootConfigurations.SingleAsync(value => value.Id == rootId)).State);
        Assert.NotNull(await environment.Store.GetActiveGenerationIdAsync(default));
    }

    private sealed class RecoveryTokenizer : IPassageTokenizer
    {
        public string Fingerprint => "recovery-synthetic-tokenizer-v1";
        public int CountTokens(string text) => text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
    }

    private static async Task AssertRepositoryPublishedAsync(SqlToUsearchRebuildTests.PipelineEnvironment environment)
    {
        await using var context = await environment.Factory.CreateDbContextAsync();
        var record = await context.PipelineRecords.AsNoTracking().SingleAsync(value => value.SourceRevisionId != null);
        Assert.True(record.CompletionCriteriaMet, JsonSerializer.Serialize(await context.Jobs.AsNoTracking()
            .Where(value => value.PipelineRecordId == record.Id).Select(value => new { value.Stage, value.PublicState, value.Reason, value.ErrorDetails }).ToArrayAsync()));
    }

    [NativeSqlServerTheory]
    [InlineData(false, true, "identical")]
    [InlineData(true, true, "identical")]
    [InlineData(false, false, "identical")]
    [InlineData(true, false, "identical")]
    [InlineData(false, false, "seal-checksum")]
    [InlineData(false, false, "unbound-epoch")]
    [InlineData(false, false, "wrong-owner")]
    public async Task Source_withdrawn_after_publish_preflight_is_deferred_before_building_an_empty_snapshot(bool sqlRetries, bool checkpointed, string change)
    {
        await SqlTestData.ClearPhase3SourceDataAsync(fixture);
        var clock = new Clock();
        await using var environment = await SqlToUsearchRebuildTests.PipelineEnvironment.CreateAsync(fixture, "Baseline", embed: false, clock: clock);
        await DiscardBaselineAsync(environment, clock);
        using var repository = new Repository(); repository.Write("Guide.md", "Publish recovery has cited source text."); repository.Git("add", ".");
        var commands = Commands(repository.Path, environment.Factory, clock);
        await Commit(commands, Mutation("root_create", new { path = repository.Path, displayName = "Publish withdrawal", discoveryMode = "git-tracked", indexSourceText = true }));
        var scans = new SqlSourceScanStore(environment.Factory, clock);
        var rootId = await ScanAndPublish(environment, scans, clock);
        var dispatch = Assert.IsType<ClaimedDispatchMessage>(await new SqlOutboxStore(environment.Factory).ClaimNextDueAsync("publisher", clock.Now, TimeSpan.FromMinutes(2), [PipelineOperations.Embed], default));
        var job = Assert.IsType<ClaimedJob>(await new SqlJobClaimStore(environment.Factory).ClaimForDispatchAsync(dispatch, "publisher", clock.Now, TimeSpan.FromMinutes(2), default));
        var transitionFactory = sqlRetries
            ? new Microsoft.EntityFrameworkCore.Infrastructure.PooledDbContextFactory<FluxKnowledgeDbContext>(
                new DbContextOptionsBuilder<FluxKnowledgeDbContext>().UseSqlServer(fixture.ConnectionString, options => options.EnableRetryOnFailure()).Options)
            : environment.Factory;
        var transitions = new StageTransitionService(new SqlStageTransitionStore(transitionFactory, null, clock), new RecoveryStatusPublisher(), new ChannelOutboxWakeSignal(), clock);
        await new EmbedStageWorker(environment.Store, new RecoveryEmbeddingProvider(), transitions, clock,
            checkpointed ? new SqlEmbeddingCheckpointStore(environment.Factory, clock) : null).ExecuteAsync(new(dispatch, job), default);
        dispatch = Assert.IsType<ClaimedDispatchMessage>(await new SqlOutboxStore(environment.Factory).ClaimNextDueAsync("publisher", clock.Now, TimeSpan.FromMinutes(2), [PipelineOperations.Publish], default));
        job = Assert.IsType<ClaimedJob>(await new SqlJobClaimStore(environment.Factory).ClaimForDispatchAsync(dispatch, "publisher", clock.Now, TimeSpan.FromMinutes(2), default));
        await transitions.ValidateRepositorySourceAsync(new(dispatch, job), default);
        Guid generation;
        byte[][] saved;
        await using (var context = await environment.Factory.CreateDbContextAsync())
        {
            generation = Guid.Parse((await context.Artifacts.SingleAsync(value => value.PipelineRecordId == job.PipelineRecordId.Value &&
                value.SourceRevision == job.SourceRevision && value.Stage == (int)PipelineStage.Embed)).SearchText);
            Assert.Equal(checkpointed, (await context.IndexGenerations.SingleAsync(value => value.Id == generation)).EmbeddingJobId != null);
            saved = await context.Vectors.Where(value => value.IndexGenerationId == generation).OrderBy(value => value.VectorId).Select(value => value.Values).ToArrayAsync();
        }
        File.Move(Path.Combine(repository.Path, "Guide.md"), Path.Combine(repository.Path, "withdrawn.tmp"));
        await Commit(commands, Mutation("source_sync", new { rootId }));
        await ScanAndPublish(environment, scans, clock);
        var refusal = await Assert.ThrowsAsync<RepositorySourceDeferredException>(() => environment.Store.ReadPublicationSnapshotAsync(generation, default).AsTask());
        await transitions.RetryAsync(new(dispatch, job, clock.Now.AddMinutes(1), "repository-source-deferred", "test", refusal.Deferral), default);
        if (change != "identical")
        {
            await using var fault = await environment.Factory.CreateDbContextAsync();
            if (change == "seal-checksum") await fault.Database.ExecuteSqlInterpolatedAsync($"UPDATE Artifacts SET ContentHash={new string('f', 64)} WHERE PipelineRecordId={job.PipelineRecordId.Value} AND Stage={(int)PipelineStage.Embed}");
            if (change == "unbound-epoch") await fault.Database.ExecuteSqlInterpolatedAsync($"UPDATE IndexGenerations SET CorpusEpoch=NULL, CorpusVersion=NULL WHERE Id={generation}");
            if (change == "wrong-owner") await fault.Database.ExecuteSqlInterpolatedAsync($"UPDATE IndexGenerations SET EmbeddingJobId={job.JobId.Value} WHERE Id={generation}");
        }
        File.Move(Path.Combine(repository.Path, "withdrawn.tmp"), Path.Combine(repository.Path, "Guide.md"));
        await Commit(commands, Mutation("source_sync", new { rootId })); await ScanAndPublish(environment, scans, clock);
        await environment.PumpAsync();
        await using var verify = await environment.Factory.CreateDbContextAsync();
        Assert.Equal(saved, await verify.Vectors.Where(value => value.IndexGenerationId == generation).OrderBy(value => value.VectorId).Select(value => value.Values).ToArrayAsync());
        if (change != "identical")
        {
            var blocked = await verify.Jobs.SingleAsync(value => value.Id == job.JobId.Value);
            Assert.Equal("repository-source-blocked", blocked.Reason);
            Assert.Equal(job.AttemptCount, blocked.AttemptCount);
            var reason = change switch
            {
                "seal-checksum" => "repository-completed-embedding-checksum-invalid",
                "unbound-epoch" => "repository-checkpoint-profile-or-epoch-changed",
                _ => "repository-embedding-generation-owner-changed"
            };
            Assert.Contains(reason, blocked.ErrorDetails);
            Assert.False((await verify.PipelineRecords.SingleAsync(value => value.Id == job.PipelineRecordId.Value)).CompletionCriteriaMet);
            return;
        }
        Assert.True((await verify.PipelineRecords.SingleAsync(value => value.Id == job.PipelineRecordId.Value)).CompletionCriteriaMet);
        Assert.Single(await verify.Jobs.Where(value => value.PipelineRecordId == job.PipelineRecordId.Value && value.Stage == (int)PipelineStage.Publish).ToListAsync());
        var retrieval = new CorpusRetrievalService(new SqlCorpusRetrievalReader(environment.Factory), new EvidenceCodec(), new LocalPrivateContentDisclosure());
        var hit = Assert.Single((await retrieval.SearchAsync(new CorpusSearchRequest("Publish recovery", 10, "root", rootId, null), default)).Results);
        Assert.Contains("Publish recovery", (await retrieval.ReadAsync(new CorpusReadRequest(hit.EvidenceRef, 0), default)).Text);
    }

    private static async Task<Guid> ScanAndPublish(SqlToUsearchRebuildTests.PipelineEnvironment environment, SqlSourceScanStore store, Clock clock)
    {
        var claim = Assert.IsType<ClaimedSourceScan>(await store.ClaimNextReleasedAsync("coverage-worker", clock.Now, TimeSpan.FromMinutes(10), default));
        using var artifacts = new ContentAddressedSourceArtifactStore(environment.ArtifactRoot);
        var worker = new SourceScanWorker(new LocalSourceEnumerator(), store, artifacts, new SqlSourceActivityStore(environment.Factory, clock),
            new RetainedTextActivityPlanner(new SqlRetainedTextRegistrationStore(environment.Factory, clock)));
        var result = await worker.ScanAsync(claim.SourceRoot, claim.ScanRequest, default);
        await store.CompleteAsync(claim, result, null, default); await environment.PumpAsync(); return claim.SourceRoot.Id.Value;
    }

    private static NativeCorpusCommandService Commands(string path, IDbContextFactory<FluxKnowledgeDbContext> factory, Clock clock) => new(
        new NativeOperationService(new SqlNativeOperationStore(factory, clock), []),
        new SqlNativeCorpusActionStore(factory, new SourceRootPathPolicy(new LocalIngressOptions([path])), new LocalPrivateContentDisclosure()));
    private static NativeCorpusMutation Mutation(string action, object payload) => new(action, JsonSerializer.SerializeToElement(payload));
    private static async Task Commit(NativeCorpusCommandService service, NativeCorpusMutation mutation)
    {
        var preview = await service.PreviewAsync(mutation, "test", default);
        var key = $"coverage:{Guid.NewGuid():N}";
        var first = await service.CommitAsync(mutation, preview.ConfirmationId, key, "test", default);
        var replay = await service.CommitAsync(mutation, preview.ConfirmationId, key, "test", default);
        Assert.False(first.WasReplay); Assert.True(replay.WasReplay); Assert.Equal(first.OperationId, replay.OperationId);
    }
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; private set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
        public void Advance(TimeSpan duration) => Now += duration;
    }
    private sealed class EvidenceCodec : ICorpusEvidenceCodec
    {
        private readonly Dictionary<string, CorpusEvidenceBinding> bindings = [];
        public string Encode(CorpusEvidenceBinding binding) { var id = Guid.NewGuid().ToString(); bindings[id] = binding; return id; }
        public CorpusEvidenceBinding Decode(string reference) => bindings[reference];
    }
    private sealed class UnusedDetailReader : ILocalRetainedDetailReader
    {
        public ValueTask<LocalRetainedDetailProjection?> ReadAsync(Guid branchId, CancellationToken token) => throw new NotSupportedException();
        public ValueTask<LocalDisclosureResult> ReadExcerptAsync(Guid branchId, CancellationToken token) => throw new NotSupportedException();
    }
    private sealed class Repository : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"FluxCoverageRepository_{Guid.NewGuid():N}");
        public Repository() { Directory.CreateDirectory(Path); Git("init", "--quiet"); }
        public void Write(string name, string text)
        {
            var full = System.IO.Path.Combine(Path, name); Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!); File.WriteAllText(full, text);
        }
        public void Replace(string name, string text)
        {
            var full = System.IO.Path.Combine(Path, name); var replacement = full + ".replacement";
            File.WriteAllText(replacement, text); File.Move(replacement, full, overwrite: true);
        }
        public void Git(params string[] arguments)
        {
            var start = new ProcessStartInfo(@"C:\Program Files\Git\cmd\git.exe") { WorkingDirectory = Path, UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
            using var process = Process.Start(start)!; var errors = process.StandardError.ReadToEnd(); process.WaitForExit(); Assert.True(process.ExitCode == 0, errors);
        }
        public void Dispose() { foreach (var file in Directory.EnumerateFiles(Path, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal); Directory.Delete(Path, true); }
    }
}
