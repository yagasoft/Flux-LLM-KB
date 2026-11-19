using System.Data;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluxKnowledge.Application.Gpu;
using FluxKnowledge.Application.Indexing;
using FluxKnowledge.Application.IntegrationV1;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Pipeline;
using FluxKnowledge.Application.Visibility;
using FluxKnowledge.Domain.Gpu;
using FluxKnowledge.Domain.Pipeline;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using FluxKnowledge.Infrastructure.SqlServer.Workers;
using FluxKnowledge.Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Xunit;

namespace FluxKnowledge.Integration.Tests.IntegrationV1;

public sealed partial class EmbeddingRetryTests
{
    [NativeSqlServerTheory]
    [InlineData(437)]
    [InlineData(2048)]
    public async Task Distinct_group_history_preserves_confirmation_bytes_with_bounded_payload_reads(int groups)
    {
        await using var environment = await SeedAsync(canonicalRoot: Path.Combine(Path.GetTempPath(), "proof-scaling-" + Guid.NewGuid().ToString("N")));
        var work = await ClaimAsync(environment);
        var admission = new FixtureSourceAdmission(environment.Factory);
        var store = new SqlEmbeddingCheckpointStore(environment.Factory, TimeProvider.System, Runtime, admission);
        var first = await store.ReadNextAsync(work, Profile, default);
        await using (var setup = environment.Factory.CreateDbContext())
        {
            var canonical = await setup.Artifacts.SingleAsync(x => x.PipelineRecordId == work.Job.PipelineRecordId.Value && x.Stage == (int)PipelineStage.CanonicalIndex);
            var policy = (await setup.TextChunks.FirstAsync(x => x.ArtifactId == canonical.Id)).PassagePolicyFingerprint;
            await setup.TextChunks.Where(x => x.ArtifactId == canonical.Id).ExecuteDeleteAsync();
            var body = new StringBuilder();
            var chunks = new List<TextChunkEntity>();
            for (var ordinal = 0; ordinal < groups * 16 + 16; ordinal++)
            {
                var content = $"Scaling passage {ordinal:D8}. " + new string('x', 230) + "\n";
                chunks.Add(new() { ArtifactId = canonical.Id, SourceRevision = work.Job.SourceRevision, Ordinal = ordinal,
                    StartOffset = body.Length, Length = content.Length, Content = content, ContentHash = CodeDisclosureIntegrity.Hash(content),
                    SearchInputHash = new CanonicalTextChunk(0, ordinal, body.Length, content.Length, content, "", policy).SearchInputHash,
                    PassagePolicyFingerprint = policy });
                body.Append(content);
            }
            canonical.SearchText = body.ToString();
            canonical.ContentHash = CodeDisclosureIntegrity.Hash(canonical.SearchText);
            setup.TextChunks.AddRange(chunks);
            await setup.SaveChangesAsync();
            var now = DateTimeOffset.UtcNow;
            var record = await setup.PipelineRecords.SingleAsync(x => x.Id == work.Job.PipelineRecordId.Value);
            var source = await setup.SourceRevisions.SingleAsync(x => x.Id == record.SourceRevisionId);
            var root = await setup.SourceRootConfigurations.SingleAsync(x => x.Id == source.SourceRootId);
            var relative = Path.GetFileName(source.CanonicalPath);
            setup.SourceRootScanStates.Add(new() { SourceRootId = root.Id, VolumeIdentity = "synthetic-scaling-volume",
                BaselineEpoch = 1, NamespaceComplete = true, Mode = "Journal", PolicyFingerprint = SqlRepositoryWorkRecovery.Policy(root) });
            setup.SourceInventoryEntries.Add(new() { Id = Guid.NewGuid(), SourceRootId = root.Id, RelativePath = relative,
                PathFingerprint = SqlSourceChangeJournalStore.PathKey(relative), BaselineEpoch = 1, SourceRevisionId = source.Id,
                SourceArtifactId = (await setup.SourceArtifacts.SingleAsync(x => x.SourceRevisionId == source.Id)).Id });
            const string slot = "synthetic-proof-scaling";
            setup.GpuCapacitySlots.Add(new() { SlotKey = slot, UpdatedAtUtc = now });
            // Seed an older settled failure followed by its exact successful
            // replacement, then distinct groups. Every chunk has different
            // normalised bytes and requests use reverse canonical-ID order.
            for (var group = -1; group < groups; group++)
            {
                var id = Guid.NewGuid();
                var batch = Guid.NewGuid();
                var dispatch = Guid.NewGuid();
                var inputs = chunks.Skip(Math.Max(0, group) * 16).Take(16).Reverse().ToArray();
                var vectors = inputs.Select(chunk =>
                {
                    var ratio = (chunk.Ordinal + 1) / 1000f;
                    var scale = 1 / MathF.Sqrt(1 + ratio * ratio);
                    var values = new byte[Profile.Dimensions * sizeof(float)];
                    Buffer.BlockCopy(new float[] { scale, ratio * scale }, 0, values, 0, 2 * sizeof(float));
                    return new VectorEntity { TextChunkId = chunk.Id, SourceRevision = work.Job.SourceRevision,
                        IndexGenerationId = first.GenerationId, ModelFingerprint = Profile.ModelFingerprint, Dimensions = Profile.Dimensions,
                        Values = values, TextChunkContentHash = chunk.ContentHash, SearchInputHash = chunk.SearchInputHash,
                        PayloadChecksum = Convert.ToHexStringLower(SHA256.HashData(values)), CreatedAtUtc = now };
                }).ToArray();
                var resultDigest = SHA256.HashData(vectors.SelectMany(x => x.Values).ToArray());
                var json = JsonSerializer.Serialize(inputs.Select(x => new { x.Id, Hash = x.SearchInputHash }));
                setup.GpuBatches.Add(new() { Id = batch, CapacitySlotKey = slot, PriorityLane = (int)GpuPriorityLane.DocumentIndexing,
                    ModelRuntimeKey = Runtime.RuntimeKey, SettingsFingerprint = Runtime.SettingsFingerprint, ItemCount = 1, EstimatedBytes = 1024,
                    AdmissionGeneration = 1, OwnerKey = "synthetic-scaling-owner", State = (int)(group < 0 ? GpuBatchState.Released : GpuBatchState.Completed), CreatedAtUtc = now, UpdatedAtUtc = now });
                setup.GpuMiniTasks.Add(new() { Id = id, ParentJobId = work.Job.JobId.Value, SourceRevision = work.Job.SourceRevision,
                    PriorityLane = (int)GpuPriorityLane.DocumentIndexing, ModelRuntimeKey = Runtime.RuntimeKey, SettingsFingerprint = Runtime.SettingsFingerprint,
                    EstimatedBytes = 1024, AdmissionGeneration = 1, IdempotencyKey = $"embedding-gpu:{id:N}",
                    ExecutionState = (int)(group < 0 ? GpuMiniTaskExecutionState.OutcomeUncertain : GpuMiniTaskExecutionState.Completed), BatchId = batch, CreatedAtUtc = now });
                setup.GpuExecutorDispatches.Add(new() { DispatchId = dispatch, BatchId = batch, CapacitySlotKey = slot,
                    OwnerKey = "synthetic-scaling-owner", ExecutorKey = EmbeddingGpuExecutor.Name, AdmissionGeneration = 1,
                    State = (int)GpuExecutorDispatchState.Terminal, UpdatedAtUtc = now });
                setup.EmbeddingGpuRequests.Add(new() { MiniTaskId = id, ParentJobId = work.Job.JobId.Value, PipelineRecordId = work.Job.PipelineRecordId.Value,
                    SourceRevision = work.Job.SourceRevision, GenerationId = first.GenerationId, CorpusEpoch = first.CorpusEpoch,
                    ModelFingerprint = Profile.ModelFingerprint, Dimensions = Profile.Dimensions, InputsJson = json, InputDigest = CodeDisclosureIntegrity.Hash(json),
                    State = 2, ExecutorInstanceId = Guid.NewGuid(), ClaimOperationId = Guid.NewGuid(), OwnerProcessId = Environment.ProcessId,
                    OwnerStartedAtUtc = now.AddMinutes(-1), OwnerMachineFingerprint = new string('a', 64), DispatchId = dispatch,
                    NativeCleanupConfirmed = true, CleanupConfirmedAtUtc = now, ResultDigest = group < 0 ? null : resultDigest, CreatedAtUtc = now, UpdatedAtUtc = now });
                if (group < 0) await setup.SaveChangesAsync();
                else setup.Vectors.AddRange(vectors);
            }
            (await setup.IndexGenerations.SingleAsync(x => x.Id == first.GenerationId)).VectorCount = groups * 16;
            await setup.SaveChangesAsync();
        }
        await new SqlStageTransitionStore(environment.Factory, sourceAdmission: admission).FailAsync(new(work.DispatchMessage, work.Job,
            "stage worker failed non-retryably", "Synthetic completed history for proof compatibility.", "test"), default);
        await using var old = environment.Factory.CreateDbContext();
        await using var transaction = await old.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        await SqlPublishedPassageSelection.AcquireFenceAsync(old, default);
        var expected = await ReadLegacyEligibleAsync(old, work.Job.JobId.Value, Runtime, false, default);
        var counter = new RecoveryReadCounter();
        // Reuse the exact transaction and snapshot while observing only the new proof.
        var options = new DbContextOptionsBuilder<FluxKnowledgeDbContext>().UseSqlServer(old.Database.GetDbConnection())
            .AddInterceptors(counter).Options;
        await using var current = new FluxKnowledgeDbContext(options);
        await current.Database.UseTransactionAsync(transaction.GetDbTransaction());
        var timer = Stopwatch.StartNew();
        var actual = await SqlEmbeddingRetry.ReadEligibleAsync(current, work.Job.JobId.Value, Runtime, false, default);
        timer.Stop();
        output.WriteLine($"groups={groups}; native proof under publication fence: {timer.Elapsed.TotalMilliseconds:F1}ms; " +
            $"SQL readers={counter.Count + 1} (including sequential canonical reader); intercepted rows={counter.Rows}; " +
            $"execute-to-reader={counter.ExecuteToReaderMilliseconds:F1}ms; vector payload per full pass={groups * 16L * Profile.Dimensions * sizeof(float)} bytes; maximum result page=128 chunks.");
        Assert.Equal(JsonSerializer.Serialize(expected.Targets), JsonSerializer.Serialize(actual.Targets));
        Assert.InRange(counter.Count, 1, groups / 2 + 250);
        Assert.True(timer.Elapsed < TimeSpan.FromMinutes(1), $"Proof used {timer.Elapsed} of the two-minute worker lease.");
        // A late sibling and an earlier missing vector must still invalidate the
        // next proof, including history beyond a single bounded payload page.
        var sibling = await current.EmbeddingGpuRequests.OrderByDescending(x => x.MiniTaskId).FirstAsync(x => x.ParentJobId == work.Job.JobId.Value && x.ResultDigest != null);
        var originalDigest = sibling.ResultDigest;
        sibling.ResultDigest = new byte[32];
        await current.SaveChangesAsync();
        await Assert.ThrowsAsync<NativeOperationException>(() => SqlEmbeddingRetry.ReadEligibleAsync(current, work.Job.JobId.Value, Runtime, false, default));
        sibling.ResultDigest = originalDigest;
        await current.SaveChangesAsync();
        var originalInputs = sibling.InputsJson;
        var originalInputDigest = sibling.InputDigest;
        sibling.InputsJson = JsonSerializer.Serialize(JsonSerializer.Deserialize<LegacyGpuInput[]>(originalInputs)!.Reverse());
        sibling.InputDigest = CodeDisclosureIntegrity.Hash(sibling.InputsJson);
        await current.SaveChangesAsync();
        await Assert.ThrowsAsync<NativeOperationException>(() => SqlEmbeddingRetry.ReadEligibleAsync(current, work.Job.JobId.Value, Runtime, false, default));
        sibling.InputsJson = originalInputs;
        sibling.InputDigest = originalInputDigest;
        await current.SaveChangesAsync();
        await current.Vectors.Where(x => x.IndexGenerationId == first.GenerationId).OrderBy(x => x.VectorId).Take(1).ExecuteDeleteAsync();
        await Assert.ThrowsAsync<NativeOperationException>(() => SqlEmbeddingRetry.ReadEligibleAsync(current, work.Job.JobId.Value, Runtime, false, default));
        await transaction.RollbackAsync();
        await current.Database.UseTransactionAsync(null);
        var service = Service(environment);
        var command = Mutation(work.Job.JobId.Value);
        var preview = await service.PreviewAsync(command, "test", default);
        await service.CommitAsync(command, preview.ConfirmationId, $"scaling-retry-{groups}", "test", default);
        var claimTime = DateTimeOffset.UtcNow;
        var delivery = await new SqlOutboxStore(environment.Factory, embeddingRuntime: Runtime, sourceAdmission: admission)
            .ClaimNextDueAsync("scaling-dispatch", claimTime, TimeSpan.FromMinutes(2), [FluxKnowledge.Application.Workers.PipelineOperations.Embed], default);
        Assert.NotNull(delivery);
        var job = await new SqlJobClaimStore(environment.Factory, sourceAdmission: admission)
            .ClaimForDispatchAsync(delivery, "scaling-worker", claimTime, TimeSpan.FromMinutes(2), default);
        Assert.NotNull(job);
        var nextWork = new FluxKnowledge.Application.Workers.StageWorkItem(delivery, job);
        timer.Restart();
        var next = await store.ReadNextAsync(nextWork, Profile, default);
        timer.Stop();
        output.WriteLine($"groups={groups}; marked filesystem next quantum including publication fence: {timer.Elapsed.TotalMilliseconds:F1}ms; remaining lease headroom={(nextWork.DispatchMessage.LeaseExpiresAtUtc - DateTimeOffset.UtcNow).TotalSeconds:F1}s.");
        Assert.Equal(16, next.Chunks.Count);
        Assert.True(timer.Elapsed < TimeSpan.FromMinutes(1));
        Assert.True(nextWork.DispatchMessage.LeaseExpiresAtUtc > DateTimeOffset.UtcNow.AddMinutes(1));
        // A fresh store and DbContexts model a restart: settled results already
        // validated by the previous quantum must not reread historical vectors.
        var resumedReads = new RecoveryReadCounter();
        var resumedFactory = new RecoveryContextFactory(fixture.ConnectionString, resumedReads);
        var resumedStore = new SqlEmbeddingCheckpointStore(resumedFactory, TimeProvider.System, Runtime,
            new FixtureSourceAdmission(resumedFactory));
        var resumed = await resumedStore.ReadNextAsync(nextWork, Profile, default);
        Assert.Equal(next.Chunks.Select(chunk => chunk.Id), resumed.Chunks.Select(chunk => chunk.Id));
        Assert.Equal(0, resumedReads.VectorPayloadReaders);
        await using var proofCheck = environment.Factory.CreateDbContext();
        Assert.Equal(groups, await proofCheck.GpuSchedulerOperationReceipts.CountAsync(receipt =>
            receipt.OperationKind == "embedding-checkpoint-validation/v1"));
        if (groups == 437)
        {
            var vector = await proofCheck.Vectors.AsNoTracking().OrderBy(value => value.VectorId)
                .FirstAsync(value => value.IndexGenerationId == first.GenerationId);
            await proofCheck.Vectors.Where(value => value.VectorId == vector.VectorId)
                .ExecuteUpdateAsync(update => update.SetProperty(value => value.PayloadChecksum, new string('0', 64)));
            await Assert.ThrowsAsync<RepositorySourceDeferredException>(() => resumedStore.ReadNextAsync(nextWork, Profile, default).AsTask());
            Assert.Equal(groups, await proofCheck.GpuSchedulerOperationReceipts.CountAsync(receipt =>
                receipt.OperationKind == "embedding-checkpoint-validation/v1"));
            await proofCheck.Vectors.Where(value => value.VectorId == vector.VectorId)
                .ExecuteUpdateAsync(update => update.SetProperty(value => value.PayloadChecksum, vector.PayloadChecksum));
            var changedReads = new RecoveryReadCounter();
            var changedFactory = new RecoveryContextFactory(fixture.ConnectionString, changedReads);
            var changedStore = new SqlEmbeddingCheckpointStore(changedFactory, TimeProvider.System, Runtime,
                new FixtureSourceAdmission(changedFactory));
            _ = await changedStore.ReadNextAsync(nextWork, Profile, default);
            Assert.Equal(1, changedReads.VectorPayloadReaders); // Only the changed group is reread.
            var request = await proofCheck.EmbeddingGpuRequests.AsNoTracking()
                .FirstAsync(value => value.ParentJobId == nextWork.Job.JobId.Value && value.ResultDigest != null);
            var reordered = JsonSerializer.Serialize(JsonSerializer.Deserialize<LegacyGpuInput[]>(request.InputsJson)!.Reverse());
            await proofCheck.EmbeddingGpuRequests.Where(value => value.MiniTaskId == request.MiniTaskId)
                .ExecuteUpdateAsync(update => update.SetProperty(value => value.InputsJson, reordered)
                    .SetProperty(value => value.InputDigest, CodeDisclosureIntegrity.Hash(reordered)));
            await Assert.ThrowsAsync<RepositorySourceDeferredException>(() => changedStore.ReadNextAsync(nextWork, Profile, default).AsTask());
            Assert.Equal(groups + 1, await proofCheck.GpuSchedulerOperationReceipts.CountAsync(receipt =>
                receipt.OperationKind == "embedding-checkpoint-validation/v1"));
        }
    }
}
