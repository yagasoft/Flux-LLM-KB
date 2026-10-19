using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluxKnowledge.Application.Gpu;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Workers;
using FluxKnowledge.Domain.Gpu;
using FluxKnowledge.Domain.Jobs;
using FluxKnowledge.Infrastructure.Inference.Search;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using FluxKnowledge.Infrastructure.SqlServer.Workers;
using FluxKnowledge.Integration.Tests.Models;
using FluxKnowledge.Integration.Tests.Support;
using FluxKnowledge.Integrations.Windows;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Indexing;

public sealed partial class EmbeddingGpuRequestTests
{
    private static string OwnedBatchText => string.Join("\n\n", Enumerable.Range(0, 48)
        .Select(index => $"Heading {index}. " + new string('a', 900) + "."));

    [NativeSqlServerFact]
    public async Task Owned_embedding_batches_replay_a_lost_four_input_handoff_then_complete_sixteen_without_rewriting_history()
    {
        await using var environment = await SqlToUsearchRebuildTests.PipelineEnvironment.CreateAsync(fixture, OwnedBatchText, embed: false);
        var worker = await ClaimAsync(environment);
        var previousStore = new SqlEmbeddingCheckpointStore(environment.Factory, TimeProvider.System);
        var previous = await previousStore.ReadNextAsync(worker, Runtime.Profile, default);
        Assert.Equal(4, previous.Chunks.Count);
        var interrupted = new SqlEmbeddingGpuRequestStore(environment.Factory, new InterruptBeforeGpuHandoff(),
            new ChannelGpuSchedulerWakeSignal(), Runtime, TimeProvider.System);
        await Assert.ThrowsAsync<ResponseLostException>(() => interrupted.QueueAsync(worker, previous, default).AsTask());
        await using var verify = await environment.Factory.CreateDbContextAsync();
        var saved = await verify.EmbeddingGpuRequests.AsNoTracking().SingleAsync();
        Assert.Empty(await verify.GpuMiniTasks.ToArrayAsync());
        var upgraded = new SqlEmbeddingCheckpointStore(environment.Factory, TimeProvider.System, Runtime);
        var replay = await upgraded.ReadNextAsync(worker, Runtime.Profile, default);
        Assert.Equal(previous.Chunks, replay.Chunks);
        var requests = NewBatchRequests(environment);
        await requests.QueueAsync(worker, replay, default);
        Assert.Equal(saved.MiniTaskId, (await verify.GpuMiniTasks.AsNoTracking().SingleAsync()).Id);
        await ExecutePendingBatchAsync(environment, requests);
        var nextWorker = await ClaimAsync(environment, now: DateTimeOffset.UtcNow.AddMinutes(1));
        var next = await upgraded.ReadNextAsync(nextWorker, Runtime.Profile, default);
        Assert.Equal(16, next.Chunks.Count);
        Assert.DoesNotContain(next.Chunks, item => previous.Chunks.Any(old => old.Id == item.Id));
        await requests.QueueAsync(nextWorker, next, default);
        await requests.QueueAsync(nextWorker, next, default);
        Assert.Equal(2, await verify.EmbeddingGpuRequests.CountAsync());
        var models = await ExecutePendingBatchAsync(environment, requests);
        Assert.Equal([4, 4, 4, 4], models.Batches.Select(batch => batch.Length));
        Assert.Single(models.Events, item => item == "embedding.open");
        Assert.Single(models.Events, item => item == "embedding.dispose");
        Assert.Equal(20, await verify.Vectors.CountAsync());
        var unchanged = await verify.EmbeddingGpuRequests.AsNoTracking().SingleAsync(value => value.MiniTaskId == saved.MiniTaskId);
        Assert.Equal(saved.InputsJson, unchanged.InputsJson);
        Assert.Equal(saved.InputDigest, unchanged.InputDigest);
        Assert.True(unchanged.NativeCleanupConfirmed);
        var job = await verify.Jobs.AsNoTracking().SingleAsync(value => value.Id == worker.Job.JobId.Value);
        var draft = await verify.IndexGenerations.AsNoTracking().SingleAsync(value => value.Id == next.GenerationId);
        Assert.NotEmpty(await SqlEmbeddingRetry.ReadSettledGpuBindingAsync(verify, job, draft, Runtime, default));
    }

    [NativeSqlServerFact]
    public async Task Owned_embedding_batches_recheck_pending_group_exclusivity_at_queue()
    {
        await using var environment = await SqlToUsearchRebuildTests.PipelineEnvironment.CreateAsync(fixture, OwnedBatchText, embed: false);
        var worker = await ClaimAsync(environment);
        var larger = await new SqlEmbeddingCheckpointStore(environment.Factory, TimeProvider.System, Runtime).ReadNextAsync(worker, Runtime.Profile, default);
        Assert.Equal(16, larger.Chunks.Count);
        var previous = larger with { Chunks = larger.Chunks.Take(4).ToArray() };
        var interrupted = new SqlEmbeddingGpuRequestStore(environment.Factory, new InterruptBeforeGpuHandoff(),
            new ChannelGpuSchedulerWakeSignal(), Runtime, TimeProvider.System);
        await Assert.ThrowsAsync<ResponseLostException>(() => interrupted.QueueAsync(worker, previous, default).AsTask());
        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => NewBatchRequests(environment).QueueAsync(worker, larger, default).AsTask());
        Assert.Equal("embedding-gpu-request-conflict", refused.Message);
        await using var verify = await environment.Factory.CreateDbContextAsync();
        Assert.Single(await verify.EmbeddingGpuRequests.ToArrayAsync());
        Assert.Empty(await verify.GpuMiniTasks.ToArrayAsync());
    }

    [NativeSqlServerFact]
    public async Task Owned_embedding_batches_preserve_the_failed_four_input_retry_budget()
    {
        await using var environment = await SqlToUsearchRebuildTests.PipelineEnvironment.CreateAsync(fixture, OwnedBatchText, embed: false);
        var worker = await ClaimAsync(environment);
        var previous = await new SqlEmbeddingCheckpointStore(environment.Factory, TimeProvider.System).ReadNextAsync(worker, Runtime.Profile, default);
        var requests = NewBatchRequests(environment);
        var upgraded = new SqlEmbeddingCheckpointStore(environment.Factory, TimeProvider.System, Runtime);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (attempt > 0)
            {
                worker = await ClaimAsync(environment, now: DateTimeOffset.UtcNow.AddMinutes(1));
                var replay = await upgraded.ReadNextAsync(worker, Runtime.Profile, default);
                Assert.Equal(previous.Chunks, replay.Chunks);
                previous = replay;
            }
            await requests.QueueAsync(worker, previous, default);
            await ExecutePendingBatchAsync(environment, requests, fail: true);
        }
        worker = await ClaimAsync(environment, now: DateTimeOffset.UtcNow.AddMinutes(1));
        var exhausted = await upgraded.ReadNextAsync(worker, Runtime.Profile, default);
        Assert.Equal(previous.Chunks, exhausted.Chunks);
        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => requests.QueueAsync(worker, exhausted, default).AsTask());
        Assert.Equal("embedding-gpu-batch-retry-limit", refused.Message);
        await using var verify = await environment.Factory.CreateDbContextAsync();
        var history = await verify.EmbeddingGpuRequests.ToArrayAsync();
        Assert.Equal(3, history.Length);
        Assert.Single(history.Select(value => value.InputDigest).Distinct());
        Assert.All(history, value => { Assert.Equal(2, value.State); Assert.True(value.NativeCleanupConfirmed); Assert.Null(value.ResultDigest); });
        Assert.Empty(await verify.Vectors.ToArrayAsync());
    }

    [NativeSqlServerFact]
    public async Task Owned_embedding_batches_fit_sixteen_maximum_width_persisted_ids_within_the_existing_sql_contract()
    {
        await using var environment = await SqlToUsearchRebuildTests.PipelineEnvironment.CreateAsync(fixture, OwnedBatchText, embed: false);
        var worker = await ClaimAsync(environment);
        var batch = await new SqlEmbeddingCheckpointStore(environment.Factory, TimeProvider.System, Runtime).ReadNextAsync(worker, Runtime.Profile, default);
        await NewBatchRequests(environment).QueueAsync(worker, batch, default);
        await using var verify = await environment.Factory.CreateDbContextAsync();
        var persisted = await verify.EmbeddingGpuRequests.AsNoTracking().SingleAsync();
        var template = JsonNode.Parse(persisted.InputsJson)!.AsArray()[0]!.AsObject();
        var widest = new JsonArray(Enumerable.Range(0, 16).Select(index =>
        {
            var node = template.DeepClone().AsObject(); node["Id"] = long.MaxValue - index; node["Hash"] = new string('f', 64);
            return (JsonNode)node;
        }).ToArray()).ToJsonString(new JsonSerializerOptions { WriteIndented = false });
        Assert.InRange(widest.Length, 1, 2048);
        Assert.InRange(Encoding.Unicode.GetByteCount(widest), 2, 4096);
        verify.EmbeddingGpuRequests.Add(new EmbeddingGpuRequestEntity
        {
            MiniTaskId = Guid.NewGuid(), ParentJobId = persisted.ParentJobId, PipelineRecordId = persisted.PipelineRecordId,
            SourceRevision = persisted.SourceRevision, GenerationId = persisted.GenerationId, CorpusEpoch = persisted.CorpusEpoch,
            ModelFingerprint = persisted.ModelFingerprint, Dimensions = persisted.Dimensions, InputsJson = widest,
            InputDigest = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(widest))),
            CreatedAtUtc = DateTimeOffset.UtcNow, UpdatedAtUtc = DateTimeOffset.UtcNow
        });
        await verify.SaveChangesAsync();
        Assert.Equal(widest, (await verify.EmbeddingGpuRequests.AsNoTracking().SingleAsync(value => value.InputDigest != persisted.InputDigest)).InputsJson);
    }

    [NativeSqlServerTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Owned_embedding_batches_refuse_a_corrupt_or_conflicting_pending_group(bool competing)
    {
        await using var environment = await SqlToUsearchRebuildTests.PipelineEnvironment.CreateAsync(fixture, OwnedBatchText, embed: false);
        var worker = await ClaimAsync(environment);
        var first = await new SqlEmbeddingCheckpointStore(environment.Factory, TimeProvider.System).ReadNextAsync(worker, Runtime.Profile, default);
        var interrupted = new SqlEmbeddingGpuRequestStore(environment.Factory, new InterruptBeforeGpuHandoff(),
            new ChannelGpuSchedulerWakeSignal(), Runtime, TimeProvider.System);
        await Assert.ThrowsAsync<ResponseLostException>(() => interrupted.QueueAsync(worker, first, default).AsTask());
        await using (var change = await environment.Factory.CreateDbContextAsync())
        {
            var pending = await change.EmbeddingGpuRequests.SingleAsync();
            if (!competing) pending.InputDigest = new string('f', 64);
            else change.EmbeddingGpuRequests.Add(new EmbeddingGpuRequestEntity
            {
                MiniTaskId = Guid.NewGuid(), ParentJobId = pending.ParentJobId, PipelineRecordId = pending.PipelineRecordId,
                SourceRevision = pending.SourceRevision, GenerationId = pending.GenerationId, CorpusEpoch = pending.CorpusEpoch,
                ModelFingerprint = pending.ModelFingerprint, Dimensions = pending.Dimensions,
                InputsJson = "[]", InputDigest = Convert.ToHexStringLower(SHA256.HashData("[]"u8)),
                CreatedAtUtc = DateTimeOffset.UtcNow, UpdatedAtUtc = DateTimeOffset.UtcNow
            });
            await change.SaveChangesAsync();
        }
        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new SqlEmbeddingCheckpointStore(environment.Factory, TimeProvider.System, Runtime).ReadNextAsync(worker, Runtime.Profile, default).AsTask());
        Assert.Equal("embedding-gpu-request-conflict", refused.Message);
        await using var verify = await environment.Factory.CreateDbContextAsync();
        Assert.Equal(competing ? 2 : 1, await verify.EmbeddingGpuRequests.CountAsync());
        Assert.Empty(await verify.GpuMiniTasks.ToArrayAsync());
    }

    [NativeSqlServerFact]
    public async Task Owned_embedding_batches_do_not_regroup_an_uncertain_native_owner()
    {
        await using var environment = await SqlToUsearchRebuildTests.PipelineEnvironment.CreateAsync(fixture, OwnedBatchText, embed: false);
        var worker = await ClaimAsync(environment);
        var larger = await new SqlEmbeddingCheckpointStore(environment.Factory, TimeProvider.System, Runtime).ReadNextAsync(worker, Runtime.Profile, default);
        var requests = NewBatchRequests(environment);
        await requests.QueueAsync(worker, larger with { Chunks = larger.Chunks.Take(4).ToArray() }, default);
        await ExecutePendingBatchAsync(environment, requests, modelFailure: "dispose");
        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => requests.QueueAsync(worker, larger, default).AsTask());
        Assert.Equal("embedding-gpu-request-conflict", refused.Message);
        await using var verify = await environment.Factory.CreateDbContextAsync();
        var pending = Assert.Single(await verify.EmbeddingGpuRequests.ToArrayAsync());
        Assert.False(pending.NativeCleanupConfirmed);
        Assert.Equal((int)GpuCapacitySlotState.Uncertain, (await verify.GpuCapacitySlots.SingleAsync()).State);
        Assert.Empty(await verify.Vectors.ToArrayAsync());
    }

    [NativeSqlServerTheory]
    [InlineData(4, false)]
    [InlineData(4, true)]
    [InlineData(16, false)]
    [InlineData(16, true)]
    public async Task Owned_embedding_batches_acknowledge_exact_producer_replay_during_execution_without_mutating_ownership(
        int chunkCount, bool checkpointSaved)
    {
        await using var environment = await SqlToUsearchRebuildTests.PipelineEnvironment.CreateAsync(fixture, OwnedBatchText, embed: false);
        var producer = await ClaimAsync(environment);
        var selected = await new SqlEmbeddingCheckpointStore(environment.Factory, TimeProvider.System, Runtime)
            .ReadNextAsync(producer, Runtime.Profile, default);
        var batch = selected with { Chunks = selected.Chunks.Take(chunkCount).ToArray() };
        var requests = NewBatchRequests(environment);
        await requests.QueueAsync(producer, batch, default);
        await using var verify = await environment.Factory.CreateDbContextAsync();
        verify.GpuCapacitySlots.Add(new() { SlotKey = "gpu-0", State = 0, UpdatedAtUtc = DateTimeOffset.UtcNow });
        await verify.SaveChangesAsync();
        var scheduler = new SqlGpuSchedulerStore(environment.Factory);
        await scheduler.RunAdmissionRoundAsync(Guid.NewGuid(), GpuSchedulerWakeReason.WorkReady,
            new(4, 4096, TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(1)),
            (_, _) => ValueTask.FromResult(new GpuAdmissionDecision(GpuAdmissionDisposition.Admit, "gpu-0", "owner", null, EmbeddingGpuExecutor.Name)), default);
        var handle = Assert.Single(await scheduler.ReadPendingDispatchesAsync(default));
        Assert.True((await scheduler.AcknowledgeAsync(new(Guid.NewGuid(), handle), default)).Committed);
        var instance = Guid.NewGuid();
        var execution = await requests.ClaimExecutionAsync(handle, instance, Guid.NewGuid(), new WindowsInteractiveGpuOwnerProbe().Current, default);
        Assert.NotNull(execution);
        if (checkpointSaved) await requests.CommitAsync(handle, instance, execution, Outputs(chunkCount), default);
        var originalRequest = await verify.EmbeddingGpuRequests.AsNoTracking().SingleAsync();
        var originalTask = await verify.GpuMiniTasks.AsNoTracking().SingleAsync();
        var originalDispatch = await verify.GpuExecutorDispatches.AsNoTracking().SingleAsync();
        var originalSlot = await verify.GpuCapacitySlots.AsNoTracking().SingleAsync();
        var originalJob = await verify.Jobs.AsNoTracking().SingleAsync(value => value.Id == producer.Job.JobId.Value);

        await Assert.ThrowsAsync<InvalidOperationException>(() => requests.QueueAsync(
            producer with { Job = producer.Job with { LeaseOwner = "different-producer" } }, batch, default).AsTask());
        await Assert.ThrowsAsync<InvalidOperationException>(() => requests.QueueAsync(
            producer with { Job = producer.Job with { LeaseGeneration = producer.Job.LeaseGeneration + 1 } }, batch, default).AsTask());
        await Assert.ThrowsAsync<InvalidOperationException>(() => requests.QueueAsync(producer,
            batch with { Chunks = batch.Chunks.Select(chunk => chunk with { ContextHeader = "different-input" }).ToArray() }, default).AsTask());
        await requests.QueueAsync(producer, batch, default);

        var unchanged = await verify.EmbeddingGpuRequests.AsNoTracking().SingleAsync();
        Assert.Equal(originalRequest.RowVersion, unchanged.RowVersion);
        Assert.Equal(checkpointSaved ? 1 : 0, unchanged.State);
        Assert.Equal(instance, unchanged.ExecutorInstanceId);
        Assert.False(unchanged.NativeCleanupConfirmed);
        Assert.Equal(originalTask.RowVersion, (await verify.GpuMiniTasks.AsNoTracking().SingleAsync()).RowVersion);
        Assert.Equal(originalDispatch.RowVersion, (await verify.GpuExecutorDispatches.AsNoTracking().SingleAsync()).RowVersion);
        Assert.Equal(originalSlot.RowVersion, (await verify.GpuCapacitySlots.AsNoTracking().SingleAsync()).RowVersion);
        Assert.Equal(originalJob.RowVersion, (await verify.Jobs.AsNoTracking().SingleAsync(value => value.Id == producer.Job.JobId.Value)).RowVersion);
        Assert.Equal((int)GpuCapacitySlotState.Reserved, (await verify.GpuCapacitySlots.AsNoTracking().SingleAsync()).State);
        Assert.Equal(checkpointSaved ? chunkCount : 0, await verify.Vectors.CountAsync());
        Assert.Empty(await scheduler.ReadPendingDispatchesAsync(default));
    }

    private static SqlEmbeddingGpuRequestStore NewBatchRequests(SqlToUsearchRebuildTests.PipelineEnvironment environment) =>
        new(environment.Factory, new SqlGpuSchedulerStore(environment.Factory), new ChannelGpuSchedulerWakeSignal(), Runtime, TimeProvider.System);

    private static async Task<BgeGpuInferenceTests.RecordingModels> ExecutePendingBatchAsync(
        SqlToUsearchRebuildTests.PipelineEnvironment environment, SqlEmbeddingGpuRequestStore requests, bool fail = false, string? modelFailure = null)
    {
        var scheduler = new SqlGpuSchedulerStore(environment.Factory);
        await using (var db = await environment.Factory.CreateDbContextAsync())
        {
            if (!await db.GpuCapacitySlots.AnyAsync()) db.GpuCapacitySlots.Add(new() { SlotKey = "gpu-0", State = 0, UpdatedAtUtc = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        }
        var models = new BgeGpuInferenceTests.RecordingModels { Failure = modelFailure ?? (fail ? "run" : null) };
        var executor = new EmbeddingGpuExecutor(requests, new Lifecycle(scheduler), scheduler, new BgeGpuInferenceSession(models),
            new WindowsInteractiveGpuOwnerProbe(), Runtime, new ChannelOutboxWakeSignal(), new ChannelGpuSchedulerWakeSignal(), TimeProvider.System);
        await scheduler.RunAdmissionRoundAsync(Guid.NewGuid(), GpuSchedulerWakeReason.WorkReady,
            new(4, 4096, TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(1)),
            (_, _) => ValueTask.FromResult(new GpuAdmissionDecision(GpuAdmissionDisposition.Admit, "gpu-0", "owner", null, executor.ExecutorKey)), default);
        await executor.DeliverAsync(Assert.Single(await scheduler.ReadPendingDispatchesAsync(default)), default);
        return models;
    }

    private sealed class InterruptBeforeGpuHandoff : IGpuSchedulerStore
    {
        public ValueTask<GpuMiniTaskHandoffResult> GpuTaskHandoffAsync(GpuMiniTaskHandoffRequest request, CancellationToken ct) => ValueTask.FromException<GpuMiniTaskHandoffResult>(new ResponseLostException());
        public ValueTask<GpuSchedulerAdmissionRoundResult> RunAdmissionRoundAsync(Guid id, GpuSchedulerWakeReason reason, GpuSchedulerOptions options, Func<GpuBatchCandidate, CancellationToken, ValueTask<GpuAdmissionDecision>> decide, CancellationToken ct) => throw new NotSupportedException();
        public ValueTask<GpuBatchCallbackResult> ApplyBatchCallbackAsync(Guid id, GpuBatchCallback callback, CancellationToken ct) => throw new NotSupportedException();
        public ValueTask<GpuDiagnosticTransitionResult> MarkCapacityUncertainAsync(Guid id, GpuCapacityUncertaintyRequest request, CancellationToken ct) => throw new NotSupportedException();
        public ValueTask<GpuTrustedReconciliationResult> ReconcileCapacityAsync(Guid id, GpuTrustedCapacityReconciliation request, CancellationToken ct) => throw new NotSupportedException();
        public ValueTask<GpuTrustedReconciliationResult> ReconcileTaskOutcomeAsync(Guid id, GpuTaskOutcomeReconciliation request, CancellationToken ct) => throw new NotSupportedException();
        public ValueTask<IReadOnlyList<GpuCapacityUncertaintyRequest>> ReadStaleCapacityReservationsAsync(DateTimeOffset before, CancellationToken ct) => throw new NotSupportedException();
        public ValueTask<GpuSchedulerWakeSnapshot> ReadWakeStateAsync(CancellationToken ct) => throw new NotSupportedException();
        public ValueTask<GpuSchedulerWakeConsumption> ConsumeWakeAsync(Guid id, long generation, CancellationToken ct) => throw new NotSupportedException();
        public ValueTask<bool> AcknowledgeWakeAsync(Guid id, Guid consumed, CancellationToken ct) => throw new NotSupportedException();
        public ValueTask<GpuSchedulerStatusSnapshot> ReadGpuSchedulerStatusAsync(CancellationToken ct) => throw new NotSupportedException();
    }
}
