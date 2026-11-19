using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using System.Data.Common;
using FluxKnowledge.Application.Gpu;
using FluxKnowledge.Application.Indexing;
using FluxKnowledge.Application.IntegrationV1;
using FluxKnowledge.Application.Pipeline;
using FluxKnowledge.Application.Workers;
using FluxKnowledge.Domain.Gpu;
using FluxKnowledge.Domain.Jobs;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.SqlServer.Workers;
using FluxKnowledge.Integration.Tests.Indexing;
using FluxKnowledge.Integration.Tests.Support;
using FluxKnowledge.Integrations.Windows;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace FluxKnowledge.Integration.Tests.IntegrationV1;

public sealed partial class EmbeddingRetryTests
{
    [NativeSqlServerFact]
    public Task Confirmed_unstarted_handoff_reuses_original_request_and_successful_siblings_through_publication() =>
        AssertPendingHandoffRecoveryAsync();

    [NativeSqlServerFact]
    public Task Large_handoff_history_keeps_native_recovery_queries_bounded_and_preserves_every_saved_sibling() =>
        AssertPendingHandoffRecoveryAsync(savedGroups: 437);

    [NativeSqlServerTheory]
    [InlineData("digest")]
    [InlineData("owner")]
    [InlineData("cleanup")]
    [InlineData("partial-vectors")]
    [InlineData("two-unstarted")]
    [InlineData("idempotency-key")]
    [InlineData("request-after-preview")]
    [InlineData("source-after-preview")]
    [InlineData("configuration-after-preview")]
    public Task Unstarted_handoff_recovery_refuses_execution_evidence_conflicts_and_stale_confirmation(string boundary) =>
        AssertPendingHandoffRecoveryAsync(boundary);

    private async Task AssertPendingHandoffRecoveryAsync(string? boundary = null, int savedGroups = 1)
    {
        await using var environment = await SeedAsync(savedGroups > 1 ? 2000 : 12);
        var admission = new FixtureSourceAdmission(environment.Factory);
        var scheduler = new SqlGpuSchedulerStore(environment.Factory, sourceAdmission: admission);
        var wake = new ChannelGpuSchedulerWakeSignal();
        var requests = new SqlEmbeddingGpuRequestStore(environment.Factory, scheduler, wake, Runtime, TimeProvider.System, sourceAdmission: admission);
        var checkpoint = new SqlEmbeddingCheckpointStore(environment.Factory, TimeProvider.System, sourceAdmission: admission);
        var work = await ClaimAsync(environment);
        var first = await checkpoint.ReadNextAsync(work, Profile, default);
        await using (var count = environment.Factory.CreateDbContext())
            Assert.True(await count.TextChunks.CountAsync(x => x.Artifact.PipelineRecordId == work.Job.PipelineRecordId.Value) > savedGroups + 4);
        await using (var setup = environment.Factory.CreateDbContext())
        {
            setup.GpuCapacitySlots.Add(new() { SlotKey = "pending-handoff-test", UpdatedAtUtc = DateTimeOffset.UtcNow });
            await setup.SaveChangesAsync();
        }
        for (var group = 0; group < savedGroups; group++)
        {
            await requests.QueueAsync(work, savedGroups > 1 ? first with { Chunks = first.Chunks.Take(1).ToArray() } : first, default);
            await CompletePendingHandoffGroupAsync(environment, requests, scheduler, wake);
            work = await ClaimAsync(environment);
            first = await checkpoint.ReadNextAsync(work, Profile, default);
        }
        var pendingBatch = await checkpoint.ReadNextAsync(work, Profile, default);
        var interrupted = new SqlEmbeddingGpuRequestStore(environment.Factory,
            new SqlGpuSchedulerStore(environment.Factory, beforeIdempotencyRead: _ => throw new InvalidOperationException("injected-before-handoff"), sourceAdmission: admission),
            wake, Runtime, TimeProvider.System, sourceAdmission: admission);
        await Assert.ThrowsAsync<InvalidOperationException>(() => interrupted.QueueAsync(work, pendingBatch, default).AsTask());
        await using var before = environment.Factory.CreateDbContext();
        var pending = await before.EmbeddingGpuRequests.AsNoTracking().SingleAsync(x => x.ParentJobId == work.Job.JobId.Value && x.State == 0);
        var siblings = await before.Vectors.AsNoTracking().OrderBy(x => x.VectorId)
            .Select(x => new { x.VectorId, x.PayloadChecksum, Version = Convert.ToBase64String(x.RowVersion) }).ToArrayAsync();
        Assert.Equal(savedGroups, await before.GpuMiniTasks.CountAsync(x => x.ParentJobId == work.Job.JobId.Value));
        await new SqlStageTransitionStore(environment.Factory, sourceAdmission: admission).FailAsync(new(work.DispatchMessage, work.Job,
            "stage worker failed non-retryably", "Could not acquire the GPU scheduler mutation fence.", "test"), default);
        var counter = new RecoveryReadCounter();
        var countedFactory = new RecoveryContextFactory(before.Database.GetConnectionString()!, counter);
        var service = savedGroups > 1 ? new FluxKnowledge.Application.IntegrationV1.Corpus.NativeCorpusCommandService(
            new SqlNativeOperationStore(countedFactory, TimeProvider.System, embeddingRuntime: Runtime),
            new SqlNativeCorpusActionStore(countedFactory, new PathPolicy(), new Infrastructure.SqlServer.Visibility.LocalPrivateContentDisclosure(), Runtime)) : Service(environment);
        var command = Mutation(work.Job.JobId.Value);
        var afterPreview = boundary?.EndsWith("-after-preview", StringComparison.Ordinal) == true;
        var earlierPreview = afterPreview ? await service.PreviewAsync(command, "test", default) : null;
        if (boundary is not null)
        {
            await using var fault = environment.Factory.CreateDbContext();
            var candidate = await fault.EmbeddingGpuRequests.SingleAsync(x => x.MiniTaskId == pending.MiniTaskId);
            switch (boundary)
            {
                case "digest": candidate.InputDigest = new string('b', 64); break;
                case "owner":
                    var owner = new WindowsInteractiveGpuOwnerProbe().Current;
                    candidate.ExecutorInstanceId = Guid.NewGuid(); candidate.ClaimOperationId = Guid.NewGuid(); candidate.DispatchId = Guid.NewGuid();
                    candidate.OwnerProcessId = owner.ProcessId; candidate.OwnerStartedAtUtc = owner.StartedAtUtc; candidate.OwnerMachineFingerprint = owner.MachineFingerprint;
                    break;
                case "cleanup": candidate.NativeCleanupConfirmed = true; candidate.CleanupConfirmedAtUtc = DateTimeOffset.UtcNow; break;
                case "partial-vectors":
                    var partial = (Infrastructure.SqlServer.Persistence.Entities.VectorEntity)fault.Entry(await fault.Vectors.FirstAsync()).CurrentValues.ToObject();
                    partial.VectorId = 0; partial.RowVersion = []; partial.TextChunkId = pendingBatch.Chunks[0].Id;
                    partial.TextChunkContentHash = pendingBatch.Chunks[0].ContentHash; partial.SearchInputHash = pendingBatch.Chunks[0].SearchInputHash;
                    fault.Vectors.Add(partial); (await fault.IndexGenerations.SingleAsync(x => x.Id == pendingBatch.GenerationId)).VectorCount++;
                    break;
                case "two-unstarted":
                    var another = (Infrastructure.SqlServer.Persistence.Entities.EmbeddingGpuRequestEntity)fault.Entry(candidate).CurrentValues.ToObject();
                    another.MiniTaskId = Guid.NewGuid(); another.RowVersion = [];
                    another.InputsJson = JsonSerializer.Serialize(pendingBatch.Chunks.Reverse().Select(x => new { x.Id, Hash = x.SearchInputHash }));
                    another.InputDigest = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(another.InputsJson)));
                    fault.EmbeddingGpuRequests.Add(another); break;
                case "idempotency-key":
                    fault.GpuMiniTasks.Add(new() { Id = Guid.NewGuid(), ParentJobId = work.Job.JobId.Value, SourceRevision = work.Job.SourceRevision, PriorityLane = (int)GpuPriorityLane.DocumentIndexing,
                        ModelRuntimeKey = Runtime.RuntimeKey, SettingsFingerprint = Runtime.SettingsFingerprint, EstimatedBytes = 1024,
                        IdempotencyKey = $"embedding-gpu:{pending.MiniTaskId:N}", CreatedAtUtc = DateTimeOffset.UtcNow });
                    break;
                case "request-after-preview": candidate.UpdatedAtUtc += TimeSpan.FromTicks(1); break;
                case "source-after-preview": (await fault.SourceRevisions.SingleAsync(x => x.Id ==
                    fault.PipelineRecords.Where(p => p.Id == work.Job.PipelineRecordId.Value).Select(p => p.SourceRevisionId).Single())).SuppressedAtUtc = DateTimeOffset.UtcNow; break;
                case "configuration-after-preview":
                    var sourceId = await fault.PipelineRecords.Where(x => x.Id == work.Job.PipelineRecordId.Value).Select(x => x.SourceRevisionId).SingleAsync();
                    var rootId = await fault.SourceRevisions.Where(x => x.Id == sourceId).Select(x => x.SourceRootId).SingleAsync();
                    (await fault.SourceRootConfigurations.SingleAsync(x => x.Id == rootId)).ConfigurationRevision++; break;
            }
            await fault.SaveChangesAsync();
            if (afterPreview)
                await Assert.ThrowsAsync<NativeOperationException>(() => service.CommitAsync(command, earlierPreview!.ConfirmationId, "stale-unstarted-recovery", "test", default).AsTask());
            else await Assert.ThrowsAsync<NativeOperationException>(() => service.PreviewAsync(command, "test", default).AsTask());
            Assert.Equal((int)PublicJobState.Failed, (await fault.Jobs.AsNoTracking().SingleAsync(x => x.Id == work.Job.JobId.Value)).PublicState);
            return;
        }
        var preview = await service.PreviewAsync(command, "test", default);
        await service.CommitAsync(command, preview.ConfirmationId, "recover-never-handed-off-" + work.Job.JobId.Value.ToString("N"), "test", default);
        if (savedGroups > 1) Assert.InRange(counter.Count, 1, savedGroups * 3 + 300);
        var replayed = await ClaimAsync(environment);
        var upgradedCheckpoint = new SqlEmbeddingCheckpointStore(environment.Factory, TimeProvider.System, Runtime, admission);
        var replayBatch = await upgradedCheckpoint.ReadNextAsync(replayed, Profile, default);
        Assert.Equal(pendingBatch.Chunks, replayBatch.Chunks);
        await requests.QueueAsync(replayed, replayBatch, default);
        await requests.QueueAsync(replayed, replayBatch, default);
        await using (var queued = environment.Factory.CreateDbContext())
        {
            Assert.Equal(savedGroups + 1, await queued.EmbeddingGpuRequests.CountAsync(x => x.ParentJobId == work.Job.JobId.Value));
            Assert.Equal(savedGroups + 1, await queued.GpuMiniTasks.CountAsync(x => x.ParentJobId == work.Job.JobId.Value));
            Assert.Equal(pending.MiniTaskId, (await queued.GpuMiniTasks.SingleAsync(x => x.Id == pending.MiniTaskId)).Id);
        }
        await CompletePendingHandoffGroupAsync(environment, requests, scheduler, wake);
        var maximumTurns = 2 + await before.TextChunks.CountAsync(x => x.Artifact.PipelineRecordId == work.Job.PipelineRecordId.Value) /
            EmbeddingGpuRuntime.MaximumRequestChunks;
        for (var turn = 0; turn < maximumTurns; turn++)
        {
            var next = await ClaimAsync(environment);
            var batch = await upgradedCheckpoint.ReadNextAsync(next, Profile, default);
            if (batch.Chunks.Count == 0)
            {
                await new EmbedStageWorker(environment.Store, new SyntheticBatchedProvider(environment.Embeddings),
                    new StageTransitionService(new SqlStageTransitionStore(environment.Factory, sourceAdmission: admission), new NoEvents(),
                        new ChannelOutboxWakeSignal(), TimeProvider.System), TimeProvider.System, upgradedCheckpoint, requests).ExecuteAsync(next, default);
                break;
            }
            await requests.QueueAsync(next, batch, default);
            await CompletePendingHandoffGroupAsync(environment, requests, scheduler, wake);
        }
        await environment.PumpAsync();
        await using var verify = environment.Factory.CreateDbContext();
        Assert.True((await verify.PipelineRecords.SingleAsync(x => x.Id == work.Job.PipelineRecordId.Value)).CompletionCriteriaMet);
        Assert.Equal(siblings, await verify.Vectors.AsNoTracking().Where(x => siblings.Select(y => y.VectorId).Contains(x.VectorId))
            .OrderBy(x => x.VectorId).Select(x => new { x.VectorId, x.PayloadChecksum, Version = Convert.ToBase64String(x.RowVersion) }).ToArrayAsync());
        var original = await verify.EmbeddingGpuRequests.SingleAsync(x => x.MiniTaskId == pending.MiniTaskId);
        Assert.Equal(pending.InputsJson, original.InputsJson);
        Assert.Equal(pending.InputDigest, original.InputDigest);
        Assert.Equal(1, await verify.AuditEvents.CountAsync(x => x.EventType == "embedding recovery queued"));
    }

    private sealed class RecoveryReadCounter : DbCommandInterceptor
    {
        public int Count { get; private set; }
        public double ExecuteToReaderMilliseconds { get; private set; }
        public long Rows { get; private set; }
        public int VectorPayloadReaders { get; private set; }
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Count++;
            return ValueTask.FromResult(result);
        }
        public override ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData,
            DbDataReader result, CancellationToken cancellationToken = default)
        {
            ExecuteToReaderMilliseconds += eventData.Duration.TotalMilliseconds;
            if (Enumerable.Range(0, result.FieldCount).Any(index => result.GetName(index) == "Values"))
                VectorPayloadReaders++;
            return ValueTask.FromResult(result);
        }
        public override InterceptionResult DataReaderDisposing(DbCommand command, DataReaderDisposingEventData eventData, InterceptionResult result)
        {
            Rows += eventData.ReadCount;
            return result;
        }
    }
    private sealed class RecoveryContextFactory(string connection, RecoveryReadCounter counter) : IDbContextFactory<FluxKnowledgeDbContext>
    {
        public FluxKnowledgeDbContext CreateDbContext() => new(new DbContextOptionsBuilder<FluxKnowledgeDbContext>()
            .UseSqlServer(connection).AddInterceptors(counter).Options);
    }

    private static async Task CompletePendingHandoffGroupAsync(SqlToUsearchRebuildTests.PipelineEnvironment environment,
        SqlEmbeddingGpuRequestStore requests, SqlGpuSchedulerStore scheduler, ChannelGpuSchedulerWakeSignal wake)
    {
        await scheduler.RunAdmissionRoundAsync(Guid.NewGuid(), GpuSchedulerWakeReason.WorkReady,
            new(4, 4096, TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(1)),
            (_, _) => ValueTask.FromResult(new GpuAdmissionDecision(GpuAdmissionDisposition.Admit, "pending-handoff-test", "test-owner", null,
                EmbeddingGpuExecutor.Name)), default);
        var handle = Assert.Single(await scheduler.ReadPendingDispatchesAsync(default));
        await new EmbeddingGpuExecutor(requests, new Lifecycle(scheduler), scheduler, new SyntheticGpuInference(environment.Embeddings),
            new WindowsInteractiveGpuOwnerProbe(), Runtime, new ChannelOutboxWakeSignal(), wake, TimeProvider.System).DeliverAsync(handle, default);
    }
}
