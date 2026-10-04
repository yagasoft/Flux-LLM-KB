using System.Text.Json;
using FluxKnowledge.Application.Contracts;
using FluxKnowledge.Application.Gpu;
using FluxKnowledge.Application.Indexing;
using FluxKnowledge.Application.IntegrationV1;
using FluxKnowledge.Application.IntegrationV1.Corpus;
using FluxKnowledge.Application.Pipeline;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Sources;
using FluxKnowledge.Application.Search;
using FluxKnowledge.Application.Workers;
using FluxKnowledge.Application.Visibility;
using FluxKnowledge.Domain.Jobs;
using FluxKnowledge.Domain.Gpu;
using FluxKnowledge.Domain.Pipeline;
using FluxKnowledge.Infrastructure.Inference;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.SqlServer.Visibility;
using FluxKnowledge.Infrastructure.SqlServer.Workers;
using FluxKnowledge.Infrastructure.SqlServer.Search;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using FluxKnowledge.Integrations.Windows;
using FluxKnowledge.Integration.Tests.Indexing;
using FluxKnowledge.Integration.Tests.Support;
using FluxKnowledge.Integration.Tests.Search;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FluxKnowledge.Integration.Tests.IntegrationV1;

[Collection("sql-full-text")]
public sealed class EmbeddingRetryTests(NativeSqlServerFixture fixture) : IClassFixture<NativeSqlServerFixture>
{
    private static readonly EmbeddingProfile Profile = new(DeterministicTokenHashEmbeddingProvider.Fingerprint, 256);
    private static readonly EmbeddingGpuRuntime Runtime = new("synthetic-embed", new string('a', 64), Profile, 1024);

    [NativeSqlServerTheory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(int.MaxValue)]
    public async Task Confirmed_terminal_retry_reuses_checkpoint_and_resumes_through_published_citation(int savedBatches)
    {
        await using var environment = await SeedAsync();
        var failed = await FailAsync(environment, savedBatches);
        var checkpoints = new SqlEmbeddingCheckpointStore(environment.Factory, TimeProvider.System);
        var command = Mutation(failed.Work.Job.JobId.Value);
        var service = Service(environment);
        var preview = await service.PreviewAsync(command, "test", CancellationToken.None);
        var key = "embedding-recovery-" + savedBatches;
        var receipt = await service.CommitAsync(command, preview.ConfirmationId, key, "test", CancellationToken.None);
        var replay = await service.CommitAsync(command, preview.ConfirmationId, key, "test", CancellationToken.None);
        Assert.True(replay.WasReplay);
        Assert.Equal(receipt.OperationId, replay.OperationId);
        await Assert.ThrowsAsync<InvalidOperationException>(() => checkpoints.ReadNextAsync(failed.Work, Profile, CancellationToken.None).AsTask());
        await using (var db = await environment.Factory.CreateDbContextAsync())
        {
            var job = await db.Jobs.SingleAsync(value => value.Id == failed.Work.Job.JobId.Value);
            var dispatch = await db.OutboxMessages.SingleAsync(value => value.JobId == job.Id);
            Assert.Equal((int)PublicJobState.WorkerQueued, job.PublicState);
            Assert.Equal(failed.Work.Job.LeaseGeneration + 1, job.LeaseGeneration);
            Assert.Equal(failed.Work.DispatchMessage.LeaseGeneration + 1, dispatch.LeaseGeneration);
            Assert.Equal(failed.Work.DispatchMessage.DispatchMessageId.Value, dispatch.Id);
            Assert.Equal(failed.Work.DispatchMessage.DispatchGeneration, dispatch.DispatchGeneration);
            Assert.Equal(failed.Generation, (await db.IndexGenerations.SingleAsync(value => value.EmbeddingJobId == job.Id)).Id);
            Assert.Equal(failed.Vectors, await db.Vectors.Where(value => value.IndexGenerationId == failed.Generation)
                .OrderBy(value => value.VectorId).Select(value => value.VectorId).ToArrayAsync());
            Assert.Single(await db.AuditEvents.Where(value => value.EventType == "embedding recovery queued").ToArrayAsync());
        }
        var work = await ClaimAsync(environment);
        var next = await checkpoints.ReadNextAsync(work, Profile, CancellationToken.None);
        Assert.Equal(failed.Generation, next.GenerationId);
        Assert.DoesNotContain(next.Chunks, chunk => failed.ChunkIds.Contains(chunk.Id));
        var provider = new SyntheticBatchedProvider(environment.Embeddings);
        var worker = new EmbedStageWorker(environment.Store, provider,
            new StageTransitionService(new SqlStageTransitionStore(environment.Factory), new NoEvents(),
                new ChannelOutboxWakeSignal(), TimeProvider.System), TimeProvider.System, checkpoints);
        await using var verification = await environment.Factory.CreateDbContextAsync();
        var expectedCount = await verification.TextChunks.CountAsync(value => value.Artifact.PipelineRecordId == failed.Work.Job.PipelineRecordId.Value);
        while (true)
        {
            await worker.ExecuteAsync(work, CancellationToken.None);
            if (await verification.Jobs.AsNoTracking().AnyAsync(value => value.Id == work.Job.JobId.Value &&
                value.PublicState == (int)PublicJobState.Completed)) break;
            work = await ClaimAsync(environment);
        }
        Assert.Equal(expectedCount - failed.ChunkIds.Length, provider.EmbeddedCount);
        await environment.PumpAsync();
        Assert.True((await verification.PipelineRecords.AsNoTracking().SingleAsync(value => value.Id == work.Job.PipelineRecordId.Value)).CompletionCriteriaMet);
        var root = await verification.SourceRevisions.Where(value => value.Id ==
            verification.PipelineRecords.Where(record => record.Id == work.Job.PipelineRecordId.Value).Select(record => record.SourceRevisionId).Single())
            .Select(value => value.SourceRootId).SingleAsync();
        var retrieval = new CorpusRetrievalService(new SqlCorpusRetrievalReader(environment.Factory),
            new ScopedCorpusRetrievalTests.TestEvidenceCodec(), new LocalPrivateContentDisclosure());
        var hit = Assert.Single((await retrieval.SearchAsync(new("Section 0", 1, "root", root, null), CancellationToken.None)).Results);
        var read = await retrieval.ReadAsync(new(hit.EvidenceRef, 0), CancellationToken.None);
        Assert.Equal(hit.Passage, read.Text);
        Assert.Equal(hit.StartOffset, read.CitedStart);
    }

    [NativeSqlServerFact]
    public async Task Concurrent_confirmations_queue_one_recovery_and_hold_refuses_both_operations()
    {
        await using var environment = await SeedAsync();
        var failed = await FailAsync(environment);
        var command = Mutation(failed.Work.Job.JobId.Value);
        var service = Service(environment);
        var previews = await Task.WhenAll(service.PreviewAsync(command, "test", CancellationToken.None).AsTask(),
            service.PreviewAsync(command, "test", CancellationToken.None).AsTask());
        var hold = new Hold { IsHeld = true };
        var held = Service(environment, hold: hold);
        Assert.Equal("deployment-validation-held", (await Assert.ThrowsAsync<NativeOperationException>(() => held.PreviewAsync(command, "test", CancellationToken.None).AsTask())).ReasonCode);
        Assert.Equal("deployment-validation-held", (await Assert.ThrowsAsync<NativeOperationException>(() => held.CommitAsync(command, previews[0].ConfirmationId, "held-retry", "test", CancellationToken.None).AsTask())).ReasonCode);
        var outcomes = await Task.WhenAll(previews.Select(async (preview, index) =>
        {
            try { await service.CommitAsync(command, preview.ConfirmationId, "concurrent-retry-" + index, "test", CancellationToken.None); return true; }
            catch (NativeOperationException) { return false; }
        }));
        Assert.Single(outcomes, value => value);
        await using var db = await environment.Factory.CreateDbContextAsync();
        Assert.Equal(failed.Work.Job.LeaseGeneration + 1, (await db.Jobs.SingleAsync(value => value.Id == failed.Work.Job.JobId.Value)).LeaseGeneration);
        Assert.Single(await db.AuditEvents.Where(value => value.EventType == "embedding recovery queued").ToArrayAsync());
    }

    [NativeSqlServerTheory]
    [InlineData("before-save")]
    [InlineData("after-save")]
    [InlineData("cancel-after-save")]
    [InlineData("lost-response")]
    public async Task Failure_and_cancellation_are_atomic_and_lost_commit_response_replays(string fault)
    {
        await using var environment = await SeedAsync();
        var failed = await FailAsync(environment);
        using var cancellation = new CancellationTokenSource();
        var store = new SqlNativeOperationStore(environment.Factory, TimeProvider.System,
            afterCommitFailureInjector: fault == "lost-response" ? () => throw new IOException("lost response") : null,
            beforeCommitInjector: fault == "before-save" ? () => throw new IOException("before save") : null,
            afterSaveBeforeCommitInjector: fault == "after-save" ? () => throw new IOException("before commit") :
                fault == "cancel-after-save" ? cancellation.Cancel : null, embeddingRuntime: Runtime);
        var command = Mutation(failed.Work.Job.JobId.Value);
        var service = Service(environment, store);
        var preview = await service.PreviewAsync(command, "test", CancellationToken.None);
        var key = "recovery-fault-" + fault;
        if (fault == "before-save") await Assert.ThrowsAsync<IOException>(() => service.CommitAsync(command, preview.ConfirmationId, key, "test", cancellation.Token).AsTask());
        else await Assert.ThrowsAsync<NativeOperationCommitUncertainException>(() => service.CommitAsync(command, preview.ConfirmationId, key, "test", cancellation.Token).AsTask());
        await using (var db = await environment.Factory.CreateDbContextAsync())
        {
            var committed = fault == "lost-response";
            var job = await db.Jobs.SingleAsync(value => value.Id == failed.Work.Job.JobId.Value);
            Assert.Equal((int)(committed ? PublicJobState.WorkerQueued : PublicJobState.Failed), job.PublicState);
            Assert.Equal(failed.Work.Job.LeaseGeneration + (committed ? 1 : 0), job.LeaseGeneration);
            Assert.Equal(committed ? 1 : 0, await db.NativeOperationReceipts.CountAsync(value => value.IdempotencyKey == key));
            Assert.Equal(committed ? 1 : 0, await db.AuditEvents.CountAsync(value => value.EventType == "embedding recovery queued"));
            Assert.Equal(failed.Vectors, await db.Vectors.Where(value => value.IndexGenerationId == failed.Generation).OrderBy(value => value.VectorId).Select(value => value.VectorId).ToArrayAsync());
        }
        var receipt = await Service(environment).CommitAsync(command, preview.ConfirmationId, key, "test", CancellationToken.None);
        Assert.Equal(fault == "lost-response", receipt.WasReplay);
    }

    [NativeSqlServerTheory]
    [InlineData("none")]
    [InlineData("unsettled")]
    [InlineData("input-digest")]
    [InlineData("input-binding")]
    [InlineData("result-digest")]
    [InlineData("result-corruption")]
    [InlineData("settings")]
    [InlineData("dispatch")]
    [InlineData("active-slot")]
    public async Task Settled_GPU_history_is_bound_and_old_delivery_cannot_replay_inference(string change)
    {
        await using var environment = await SeedAsync();
        var work = await ClaimAsync(environment);
        var checkpoint = new SqlEmbeddingCheckpointStore(environment.Factory, TimeProvider.System);
        var batch = await checkpoint.ReadNextAsync(work, Profile, CancellationToken.None);
        var scheduler = new SqlGpuSchedulerStore(environment.Factory);
        var requests = new SqlEmbeddingGpuRequestStore(environment.Factory, scheduler, new ChannelGpuSchedulerWakeSignal(), Runtime, TimeProvider.System);
        await requests.QueueAsync(work, batch, CancellationToken.None);
        await using (var db = await environment.Factory.CreateDbContextAsync())
        {
            db.GpuCapacitySlots.Add(new GpuCapacitySlotEntity { SlotKey = "synthetic-slot", UpdatedAtUtc = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        }
        await scheduler.RunAdmissionRoundAsync(Guid.NewGuid(), GpuSchedulerWakeReason.WorkReady,
            new(4, 4096, TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(1)),
            (_, _) => ValueTask.FromResult(new GpuAdmissionDecision(GpuAdmissionDisposition.Admit, "synthetic-slot", "test-owner", null, EmbeddingGpuExecutor.Name)), CancellationToken.None);
        var handle = Assert.Single(await scheduler.ReadPendingDispatchesAsync(CancellationToken.None));
        var inference = new SyntheticGpuInference(environment.Embeddings);
        EmbeddingGpuExecutor Executor() => new(requests, new Lifecycle(scheduler), scheduler, inference,
            new WindowsInteractiveGpuOwnerProbe(), Runtime, new ChannelOutboxWakeSignal(), new ChannelGpuSchedulerWakeSignal(), TimeProvider.System);
        await Executor().DeliverAsync(handle, CancellationToken.None);
        Assert.Equal(1, inference.Calls);
        work = await ClaimAsync(environment);
        await new SqlStageTransitionStore(environment.Factory).FailAsync(new(work.DispatchMessage, work.Job,
            "stage worker failed non-retryably", "embedding-checkpoint-source-unavailable", "test"), CancellationToken.None);
        var service = Service(environment);
        var command = Mutation(work.Job.JobId.Value);
        var preview = await service.PreviewAsync(command, "test", CancellationToken.None);
        if (change != "none")
        {
            await using var db = await environment.Factory.CreateDbContextAsync();
            var request = await db.EmbeddingGpuRequests.SingleAsync();
            switch (change)
            {
                case "unsettled": request.State = 1; request.NativeCleanupConfirmed = false; request.CleanupConfirmedAtUtc = null; break;
                case "input-digest": request.InputDigest = new string('b', 64); break;
                case "input-binding": request.InputsJson = "[{\"Id\":1,\"Hash\":\"" + new string('b', 64) + "\"}]"; request.InputDigest = CodeDisclosureIntegrity.Hash(request.InputsJson); break;
                case "result-digest": request.ResultDigest = null; break;
                case "result-corruption": request.ResultDigest = new byte[32]; break;
                case "settings": (await db.GpuMiniTasks.SingleAsync()).SettingsFingerprint = new string('c', 64); break;
                case "dispatch": (await db.GpuExecutorDispatches.SingleAsync()).ExecutorKey = "other-executor"; break;
                case "active-slot": (await db.GpuCapacitySlots.SingleAsync()).ActiveBatchId = handle.BatchId; break;
            }
            await db.SaveChangesAsync();
            await Assert.ThrowsAsync<NativeOperationException>(() => service.PreviewAsync(command, "test", CancellationToken.None).AsTask());
            await Assert.ThrowsAsync<NativeOperationException>(() => service.CommitAsync(command, preview.ConfirmationId, "gpu-refusal-" + change, "test", CancellationToken.None).AsTask());
            return;
        }
        await service.CommitAsync(command, preview.ConfirmationId, "gpu-recovery", "test", CancellationToken.None);
        var resumed = await ClaimAsync(environment);
        await Executor().DeliverAsync(handle, CancellationToken.None);
        Assert.Equal(1, inference.Calls);
        await using var verification = await environment.Factory.CreateDbContextAsync();
        var taskId = (await verification.EmbeddingGpuRequests.SingleAsync()).MiniTaskId;
        _ = await requests.RequeueSettledAsync(handle, taskId, CancellationToken.None);
        Assert.Equal((int)PublicJobState.WorkerProcessing, (await verification.Jobs.SingleAsync(value => value.Id == work.Job.JobId.Value)).PublicState);
        Assert.Equal(resumed.Job.LeaseGeneration, (await verification.Jobs.SingleAsync(value => value.Id == work.Job.JobId.Value)).LeaseGeneration);
        Assert.Equal(resumed.Job.LeaseOwner, (await verification.Jobs.SingleAsync(value => value.Id == work.Job.JobId.Value)).LeaseOwner);
        Assert.Equal(4, await verification.Vectors.CountAsync(value => value.IndexGenerationId == batch.GenerationId));
    }

    [NativeSqlServerTheory]
    [InlineData("source")]
    [InlineData("epoch")]
    [InlineData("profile")]
    [InlineData("payload")]
    [InlineData("chunk")]
    [InlineData("lease")]
    [InlineData("downstream")]
    public async Task Changed_or_contradictory_state_rejects_preview_and_old_confirmation(string change)
    {
        await using var environment = await SeedAsync();
        var failed = await FailAsync(environment);
        var command = Mutation(failed.Work.Job.JobId.Value);
        var service = Service(environment);
        var preview = await service.PreviewAsync(command, "test", CancellationToken.None);
        await using (var db = await environment.Factory.CreateDbContextAsync())
        {
            switch (change)
            {
                case "source":
                    var record = await db.PipelineRecords.SingleAsync(value => value.Id == failed.Work.Job.PipelineRecordId.Value);
                    (await db.SourceRevisions.SingleAsync(value => value.Id == record.SourceRevisionId)).SuppressedAtUtc = DateTimeOffset.UtcNow; break;
                case "epoch": (await db.IndexState.SingleAsync()).CorpusEpoch = Guid.NewGuid(); break;
                case "profile": (await db.IndexGenerations.SingleAsync(value => value.Id == failed.Generation)).ModelFingerprint = "changed"; break;
                case "payload": (await db.Vectors.FirstAsync(value => value.IndexGenerationId == failed.Generation)).Values = new byte[1024]; break;
                case "chunk": (await db.TextChunks.SingleAsync(value => value.Id == failed.ChunkIds[0])).ContextHeader = "changed without a bound hash"; break;
                case "lease": (await db.Jobs.SingleAsync(value => value.Id == failed.Work.Job.JobId.Value)).LeaseGeneration++; break;
                case "downstream": db.Artifacts.Add(new() { Id=Guid.NewGuid(),PipelineRecordId=failed.Work.Job.PipelineRecordId.Value,
                    SourceRevision=failed.Work.Job.SourceRevision,Stage=(int)PipelineStage.Embed,ContentHash=new string('b',64),
                    ContentType="text/plain",SearchText="contradictory downstream",CreatedAtUtc=DateTimeOffset.UtcNow }); break;
            }
            await db.SaveChangesAsync();
        }
        if (change != "lease") await Assert.ThrowsAsync<NativeOperationException>(() => service.PreviewAsync(command, "test", CancellationToken.None).AsTask());
        await Assert.ThrowsAsync<NativeOperationException>(() => service.CommitAsync(command, preview.ConfirmationId, "stale-"+change, "test", CancellationToken.None).AsTask());
        await using var check = await environment.Factory.CreateDbContextAsync();
        Assert.Equal((int)PublicJobState.Failed, (await check.Jobs.SingleAsync(value => value.Id == failed.Work.Job.JobId.Value)).PublicState);
        Assert.Empty(await check.NativeOperationReceipts.Where(value => value.IdempotencyKey == "stale-" + change).ToArrayAsync());
    }

    private async Task<SqlToUsearchRebuildTests.PipelineEnvironment> SeedAsync()
    {
        var environment = await SqlToUsearchRebuildTests.PipelineEnvironment.CreateAsync(fixture, "Unretained control", embed:false);
        _ = await ClaimAsync(environment);
        await environment.AddRetainedAndPumpAsync(string.Join("\n\n", Enumerable.Range(0,12).Select(index=>$"Section {index}. "+new string((char)('a'+index),900)+".")));
        return environment;
    }

    private static async Task<(StageWorkItem Work, Guid Generation, long[] Vectors, long[] ChunkIds)> FailAsync(SqlToUsearchRebuildTests.PipelineEnvironment environment, int savedBatches = 1)
    {
        var work = await ClaimAsync(environment);
        var store = new SqlEmbeddingCheckpointStore(environment.Factory, TimeProvider.System);
        var batch = await store.ReadNextAsync(work, Profile, CancellationToken.None);
        for (var index = 0; index < savedBatches && batch.Chunks.Count > 0; index++)
        {
            var results = new List<EmbeddingResult>();
            foreach (var chunk in batch.Chunks) results.Add(await environment.Embeddings.CreateEmbeddingAsync(chunk.SearchText, CancellationToken.None));
            await store.CommitAsync(work,batch,results,CancellationToken.None);
            batch = await store.ReadNextAsync(work, Profile, CancellationToken.None);
        }
        await new SqlStageTransitionStore(environment.Factory).FailAsync(new(work.DispatchMessage,work.Job,
            "stage worker failed non-retryably","embedding-checkpoint-source-unavailable","test"),CancellationToken.None);
        await using var db=await environment.Factory.CreateDbContextAsync();
        return (work,batch.GenerationId,await db.Vectors.Where(value=>value.IndexGenerationId==batch.GenerationId)
            .OrderBy(value=>value.VectorId).Select(value=>value.VectorId).ToArrayAsync(), await db.Vectors.Where(value => value.IndexGenerationId == batch.GenerationId)
            .Select(value => value.TextChunkId).ToArrayAsync());
    }

    private static async Task<StageWorkItem> ClaimAsync(SqlToUsearchRebuildTests.PipelineEnvironment environment)
    {
        var now=DateTimeOffset.UtcNow.AddSeconds(1);
        var dispatch=await new SqlOutboxStore(environment.Factory).ClaimNextDueAsync("retry-dispatch",now,TimeSpan.FromMinutes(2),[PipelineOperations.Embed],CancellationToken.None);
        Assert.NotNull(dispatch);
        var job=await new SqlJobClaimStore(environment.Factory).ClaimForDispatchAsync(dispatch,"retry-worker",now,TimeSpan.FromMinutes(2),CancellationToken.None);
        Assert.NotNull(job);return new(dispatch,job);
    }
    private static NativeCorpusMutation Mutation(Guid job)=>new("embedding_retry",JsonSerializer.SerializeToElement(new {jobId=job}));
    private static NativeCorpusCommandService Service(SqlToUsearchRebuildTests.PipelineEnvironment environment, INativeOperationStore? store = null, IDeploymentValidationHold? hold = null)=>
        new(store ?? new SqlNativeOperationStore(environment.Factory,TimeProvider.System,embeddingRuntime:Runtime),new SqlNativeCorpusActionStore(environment.Factory,new PathPolicy(),new LocalPrivateContentDisclosure(),Runtime),deploymentValidationHold:hold ?? environment.DeploymentHold);
    private sealed class SyntheticBatchedProvider(IEmbeddingProvider inner) : IBatchedEmbeddingProvider
    {
        public EmbeddingProfile Profile => EmbeddingRetryTests.Profile;
        public int MaximumBatchSize => 4;
        public int EmbeddedCount { get; private set; }
        public ValueTask<EmbeddingResult> CreateEmbeddingAsync(string text, CancellationToken ct) => inner.CreateEmbeddingAsync(text, ct);
        public async ValueTask<IReadOnlyList<EmbeddingResult>> CreateEmbeddingsAsync(IReadOnlyList<string> texts, CancellationToken ct)
        {
            EmbeddedCount += texts.Count;
            var values = new List<EmbeddingResult>();
            foreach (var text in texts) values.Add(await inner.CreateEmbeddingAsync(text, ct));
            return values;
        }
    }
    private sealed class SyntheticGpuInference(IEmbeddingProvider inner) : IEmbeddingGpuInference
    {
        public int Calls { get; private set; }
        public async ValueTask<GpuInteractiveNativeResult<IReadOnlyList<EmbeddingResult>>> EmbedBatchAsync(GpuOwnedWorkContext ownership, IReadOnlyList<string> texts, CancellationToken ct)
        {
            Calls++;
            var values = new List<EmbeddingResult>();
            foreach (var text in texts) values.Add(await inner.CreateEmbeddingAsync(text, ct));
            return new(values, true);
        }
    }
    private sealed class Lifecycle(SqlGpuSchedulerStore store) : IGpuExecutorLifecycleSink
    {
        public ValueTask<GpuExecutorDispatchMutationResult> AcknowledgeAsync(GpuExecutorAcknowledgement value, CancellationToken ct) => store.AcknowledgeAsync(value, ct);
        public ValueTask<GpuExecutorDispatchMutationResult> MarkDeliveryUncertainAsync(GpuExecutorDeliveryUncertainty value, CancellationToken ct) => store.MarkDeliveryUncertainAsync(value, ct);
        public ValueTask<GpuExecutorDispatchMutationResult> RecordReceiptAsync(GpuExecutorResultReceipt value, CancellationToken ct) => store.RecordReceiptAsync(value, ct);
        public ValueTask<GpuExecutorDispatchMutationResult> RecordTrustedEvidenceAsync(GpuExecutorTrustedEvidence value, CancellationToken ct) => store.RecordTrustedEvidenceAsync(value, ct);
        public ValueTask<GpuBatchCallbackResult> HandleCallbackAsync(Guid operation, GpuBatchCallback value, CancellationToken ct) => store.ApplyBatchCallbackAsync(operation, value, ct);
    }
    private sealed class NoEvents : IStatusEventPublisher
    { public ValueTask PublishAsync(StatusChanged changed, CancellationToken ct) => ValueTask.CompletedTask; }
    private sealed class Hold : IDeploymentValidationHold
    { public bool IsHeld { get; set; } public ValueTask WaitUntilReleasedAsync(CancellationToken ct) => ValueTask.CompletedTask; }
    private sealed class PathPolicy:ISourceRootPathPolicy
    { public SourceRootPathValidation ValidateAndCanonicalise(SourceRootCreateRequest request)=>throw new NotSupportedException(); }
}
