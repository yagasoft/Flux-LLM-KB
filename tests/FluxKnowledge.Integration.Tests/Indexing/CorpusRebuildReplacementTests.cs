using FluxKnowledge.Application.Indexing;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Workers;
using FluxKnowledge.Application.Gpu;
using FluxKnowledge.Infrastructure.SqlServer.Workers;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using FluxKnowledge.Application.Pipeline;
using FluxKnowledge.Domain.Gpu;
using FluxKnowledge.Domain.Jobs;
using FluxKnowledge.Domain.Pipeline;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using FluxKnowledge.Infrastructure.Usearch;
using FluxKnowledge.Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Indexing;

[Collection("sql-full-text")]
public sealed class CorpusRebuildReplacementTests(NativeSqlServerFixture fixture) : IClassFixture<NativeSqlServerFixture>
{
    [NativeSqlServerTheory]
    [InlineData(PublicJobState.WorkerQueued)]
    [InlineData(PublicJobState.GpuQueued)]
    [InlineData(PublicJobState.Failed)]
    public async Task Replacement_preserves_input_and_history_and_old_deliveries_cannot_claim_after_new_rebuild_finishes(PublicJobState oldState)
    {
        await using var environment = await SqlToUsearchRebuildTests.PipelineEnvironment.CreateAsync(fixture, "Recovery retains INV-42 and its exact source.");
        var (store, old, builder) = await InterruptedAsync(environment.Factory);
        var input = Assert.Single(old.Inputs);
        await using var context = environment.Factory.CreateDbContext();
        await context.Jobs.Where(job => job.Id == input.EmbeddingJobId).ExecuteUpdateAsync(setters => setters
            .SetProperty(job => job.PublicState, (int)oldState).SetProperty(job => job.AttemptCount, 3)
            .SetProperty(job => job.ErrorDetails, "retained-original-failure"));
        var replacement = await ReplacementAsync(store, old);
        Assert.Equal(input.CanonicalArtifactId, Assert.Single(replacement.Inputs).CanonicalArtifactId);
        Assert.NotEqual(input.EmbeddingJobId, replacement.Inputs[0].EmbeddingJobId);
        Assert.NotEqual(old.TargetEpoch, replacement.TargetEpoch);
        await store.CommitAsync(replacement, "slot-a", CancellationToken.None);
        Assert.True((await store.CommitAsync(replacement, "slot-a", CancellationToken.None)).AlreadyCommitted);
        var history = await context.Jobs.AsNoTracking().SingleAsync(job => job.Id == input.EmbeddingJobId);
        Assert.Equal((int)oldState, history.PublicState);
        Assert.Equal(3, history.AttemptCount);
        Assert.Equal("retained-original-failure", history.ErrorDetails);
        Assert.Equal(old.ManifestHash, (await context.CorpusRebuildOperations.AsNoTracking().SingleAsync(value => value.Id == old.OperationId)).ManifestHash);
        await store.PrepareAsync(replacement.OperationId, input.PipelineRecordId, builder, CancellationToken.None);
        environment.PermitRebuild(replacement.OperationId);
        await environment.PumpAsync();
        await FinishAsync(store, replacement.OperationId);
        Assert.Null((await context.IndexState.AsNoTracking().SingleAsync()).CorpusRebuildOperationId);
        Assert.Null(await new SqlOutboxStore(environment.Factory).ClaimNextDueAsync("old-delivery", DateTimeOffset.UtcNow.AddHours(1),
            TimeSpan.FromMinutes(1), [PipelineOperations.Embed, PipelineOperations.Publish], CancellationToken.None));
        Assert.False((await store.ReadPlanAsync(Guid.NewGuid(), new("next", 4), new string('c', 64), CancellationToken.None)).Inputs.Count == 0);
    }

    [NativeSqlServerFact]
    public async Task Replacement_rechecks_input_and_rolls_back_supersession_and_epoch_on_failure()
    {
        await using var environment = await SqlToUsearchRebuildTests.PipelineEnvironment.CreateAsync(fixture, "Atomic replacement.");
        var (store, old, _) = await InterruptedAsync(environment.Factory);
        var replacement = await ReplacementAsync(store, old);
        var failing = new SqlCorpusRebuildStore(environment.Factory, afterProjectionReset: _ => ValueTask.FromException(new InvalidOperationException("replacement-fault")));
        Assert.Equal("replacement-fault", (await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await failing.CommitAsync(replacement, "slot-a", CancellationToken.None))).Message);
        await using var context = environment.Factory.CreateDbContext();
        Assert.Equal(old.OperationId, (await context.IndexState.AsNoTracking().SingleAsync()).CorpusRebuildOperationId);
        Assert.Single(await context.CorpusRebuildOperations.ToArrayAsync());
        await context.Artifacts.Where(value => value.Id == old.Inputs[0].CanonicalArtifactId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(value => value.DocumentMetadataJson, "{}"));
        Assert.Equal("corpus-rebuild-input-changed", (await Assert.ThrowsAsync<CorpusRebuildRefusalException>(async () =>
            await store.CommitAsync(replacement, "slot-a", CancellationToken.None))).Message);
        Assert.Equal(old.OperationId, (await context.IndexState.AsNoTracking().SingleAsync()).CorpusRebuildOperationId);
    }

    [NativeSqlServerFact]
    public async Task Wrong_operation_binding_and_unrelated_unfinished_work_are_refused()
    {
        await using var environment = await SqlToUsearchRebuildTests.PipelineEnvironment.CreateAsync(fixture, "Bound replacement.");
        var (store, old, _) = await InterruptedAsync(environment.Factory);
        Assert.Equal("corpus-rebuild-replacement-identity-mismatch", (await Assert.ThrowsAsync<CorpusRebuildRefusalException>(async () =>
            await store.ReadReplacementPlanAsync(Guid.NewGuid(), old.OperationId, Guid.NewGuid(), old.ManifestHash,
                old.Profile, new string('c', 64), CancellationToken.None))).Message);
        await SqlTestData.SeedWorkItemAsync(fixture, DateTimeOffset.UtcNow, PublicJobState.WorkerQueued, null, stage: PipelineStage.Embed, operation: PipelineOperations.Embed);
        Assert.Equal("corpus-rebuild-projection-work-unsettled", (await Assert.ThrowsAsync<CorpusRebuildRefusalException>(async () =>
            await ReplacementAsync(store, old))).Message);
    }

    [NativeSqlServerFact]
    public async Task Lost_commit_response_replays_authoritative_receipt_and_superseded_operation_cannot_resume_or_downgrade()
    {
        await using var environment = await SqlToUsearchRebuildTests.PipelineEnvironment.CreateAsync(fixture, "Lost response recovery.");
        var (store, old, builder) = await InterruptedAsync(environment.Factory);
        var replacement = await ReplacementAsync(store, old);
        var responseLost = new SqlCorpusRebuildStore(environment.Factory, afterCommit: _ => ValueTask.FromException(new IOException("response-lost")));
        await Assert.ThrowsAsync<IOException>(async () => await responseLost.CommitAsync(replacement, "slot-a", CancellationToken.None));
        Assert.True((await store.CommitAsync(replacement, "slot-a", CancellationToken.None)).AlreadyCommitted);
        Assert.Equal("corpus-rebuild-operation-not-active", (await Assert.ThrowsAsync<CorpusRebuildRefusalException>(async () =>
            await store.PrepareAsync(old.OperationId, old.Inputs[0].PipelineRecordId, builder, CancellationToken.None))).Message);
        await using var context = environment.Factory.CreateDbContext();
        Assert.Single(await context.CorpusRebuildSupersededJobs.ToArrayAsync());
        var error = await Assert.ThrowsAsync<SqlException>(() => context.GetService<IMigrator>().MigrateAsync("20260927121634_AddCorpusRebuildWorklist"));
        Assert.Contains("corpus-rebuild-supersession-downgrade-refused", error.Message);
        Assert.Equal(replacement.OperationId, (await context.IndexState.AsNoTracking().SingleAsync()).CorpusRebuildOperationId);
    }

    [NativeSqlServerFact]
    public async Task Existing_null_supersession_manifest_keeps_its_original_bytes_and_hash()
    {
        await using var environment = await SqlToUsearchRebuildTests.PipelineEnvironment.CreateAsync(fixture, "Original manifest contract.");
        var (store, old, _) = await InterruptedAsync(environment.Factory);
        var json = JsonSerializer.Serialize(old);
        Assert.DoesNotContain("Supersession", json);
        Assert.Equal(old.ManifestHash, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(old with { ManifestHash = "" })))));
        await using var context = environment.Factory.CreateDbContext();
        Assert.Equal(json, (await context.CorpusRebuildOperations.SingleAsync()).ManifestJson);
        Assert.True((await store.CommitAsync(JsonSerializer.Deserialize<CorpusRebuildPlan>(json)!, "slot-a", CancellationToken.None)).AlreadyCommitted);
    }

    [NativeSqlServerTheory]
    [InlineData("none")]
    [InlineData("claim")]
    [InlineData("admission")]
    [InlineData("runtime")]
    [InlineData("cleanup")]
    public async Task Only_proven_unstarted_gpu_work_is_cancelled_and_history_is_retained(string corruption)
    {
        await using var environment = await SqlToUsearchRebuildTests.PipelineEnvironment.CreateAsync(fixture, "GPU queued replacement.");
        var (store, old, _) = await InterruptedAsync(environment.Factory);
        environment.PermitRebuild(old.OperationId);
        var now = DateTimeOffset.UtcNow;
        var dispatch = (await new SqlOutboxStore(environment.Factory, environment.DeploymentHold).ClaimNextDueAsync("replacement-test", now,
            TimeSpan.FromMinutes(2), [PipelineOperations.Embed], CancellationToken.None))!;
        var job = (await new SqlJobClaimStore(environment.Factory, environment.DeploymentHold).ClaimForDispatchAsync(dispatch, "replacement-test", now,
            TimeSpan.FromMinutes(2), CancellationToken.None))!;
        var work = new StageWorkItem(dispatch, job);
        var runtime = new EmbeddingGpuRuntime("replacement-test-runtime", new string('d', 64), old.Profile, 1024);
        var scheduler = new SqlGpuSchedulerStore(environment.Factory, deploymentValidationHold: environment.DeploymentHold);
        var batch = await new SqlEmbeddingCheckpointStore(environment.Factory, TimeProvider.System).ReadNextAsync(work, old.Profile, CancellationToken.None);
        await new SqlEmbeddingGpuRequestStore(environment.Factory, scheduler, new ChannelGpuSchedulerWakeSignal(), runtime, TimeProvider.System)
            .QueueAsync(work, batch, CancellationToken.None);
        await using var context = environment.Factory.CreateDbContext();
        var request = await context.EmbeddingGpuRequests.SingleAsync();
        var task = await context.GpuMiniTasks.SingleAsync();
        task.ReservationAttemptCount = 2; // Capacity deferral is not native admission; preserve its history.
        task.DeferredUntilUtc = now.AddMinutes(5);
        if (corruption == "claim")
        {
            request.ExecutorInstanceId = Guid.NewGuid(); request.ClaimOperationId = Guid.NewGuid();
            request.OwnerProcessId = 123; request.OwnerStartedAtUtc = now; request.OwnerMachineFingerprint = new string('a', 64);
            request.DispatchId = Guid.NewGuid();
        }
        if (corruption == "admission") task.AdmissionGeneration = 1;
        if (corruption == "runtime") task.ModelRuntimeKey = "foreign-runtime";
        if (corruption == "cleanup") { request.NativeCleanupConfirmed = true; request.CleanupConfirmedAtUtc = now; }
        await context.SaveChangesAsync();
        if (corruption != "none")
        {
            await Assert.ThrowsAsync<CorpusRebuildRefusalException>(async () => await store.ReadReplacementPlanAsync(Guid.NewGuid(), old.OperationId,
                old.TargetEpoch, old.ManifestHash, old.Profile, old.PassagePolicyFingerprint, CancellationToken.None, runtime));
            Assert.Equal(old.OperationId, (await context.IndexState.AsNoTracking().SingleAsync()).CorpusRebuildOperationId);
            return;
        }
        var replacement = await store.ReadReplacementPlanAsync(Guid.NewGuid(), old.OperationId, old.TargetEpoch, old.ManifestHash,
            old.Profile, old.PassagePolicyFingerprint, CancellationToken.None, runtime);
        Assert.Single(replacement.Supersession!.UnstartedRequestIds);
        await store.CommitAsync(replacement, "slot-a", CancellationToken.None);
        var retained = await context.EmbeddingGpuRequests.AsNoTracking().SingleAsync();
        Assert.Equal(request.MiniTaskId, retained.MiniTaskId);
        Assert.Equal(request.InputDigest, retained.InputDigest);
        Assert.Equal(2, retained.State);
        Assert.True(retained.NativeCleanupConfirmed);
        Assert.Null(retained.ExecutorInstanceId);
        Assert.Null(retained.ResultDigest);
        Assert.Equal((int)GpuMiniTaskExecutionState.Cancelled, (await context.GpuMiniTasks.AsNoTracking().SingleAsync()).ExecutionState);
        Assert.Equal(2, (await context.GpuMiniTasks.AsNoTracking().SingleAsync()).ReservationAttemptCount);
        Assert.Empty(await context.GpuExecutorDispatches.ToArrayAsync());
        Assert.Empty(await context.GpuExecutorResultReceipts.ToArrayAsync());
        Assert.Equal(retained.MiniTaskId, await SqlEmbeddingGpuRequestStore.OwnedLocalTasks(context, runtime).Select(value => value.Id).SingleAsync());
        Assert.False(await SqlEmbeddingGpuRequestStore.HasContradictoryRequestsAsync(context, [old.Inputs[0].PipelineRecordId], [], CancellationToken.None));
        await context.EmbeddingGpuRequests.Where(value => value.MiniTaskId == retained.MiniTaskId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(value => value.CorpusEpoch, Guid.NewGuid()));
        Assert.True(await SqlEmbeddingGpuRequestStore.HasContradictoryRequestsAsync(context, [old.Inputs[0].PipelineRecordId], [], CancellationToken.None));
    }

    [NativeSqlServerFact]
    public async Task Snapshot_changes_and_a_competing_replacement_refuse_without_replacing_committed_authority()
    {
        await using var environment = await SqlToUsearchRebuildTests.PipelineEnvironment.CreateAsync(fixture, "Competing recovery.");
        var (store, old, _) = await InterruptedAsync(environment.Factory);
        var stale = await ReplacementAsync(store, old);
        await using var context = environment.Factory.CreateDbContext();
        await context.Jobs.Where(value => value.Id == old.Inputs[0].EmbeddingJobId).ExecuteUpdateAsync(setters => setters.SetProperty(value => value.AttemptCount, 1));
        Assert.Equal("corpus-rebuild-manifest-changed", (await Assert.ThrowsAsync<CorpusRebuildRefusalException>(async () =>
            await store.CommitAsync(stale, "slot-a", CancellationToken.None))).Message);
        Assert.Equal(old.OperationId, (await context.IndexState.AsNoTracking().SingleAsync()).CorpusRebuildOperationId);
        var first = await ReplacementAsync(store, old);
        var second = await ReplacementAsync(store, old);
        await store.CommitAsync(first, "slot-a", CancellationToken.None);
        await Assert.ThrowsAsync<CorpusRebuildRefusalException>(async () => await store.CommitAsync(second, "slot-a", CancellationToken.None));
        Assert.Equal(first.OperationId, (await context.IndexState.AsNoTracking().SingleAsync()).CorpusRebuildOperationId);
        Assert.Equal(2, await context.CorpusRebuildOperations.CountAsync());
    }

    [NativeSqlServerFact]
    public async Task Replacement_holds_real_admission_query_and_publication_fences_until_transaction_commit()
    {
        await using var environment = await SqlToUsearchRebuildTests.PipelineEnvironment.CreateAsync(fixture, "Fenced recovery.");
        var (store, old, _) = await InterruptedAsync(environment.Factory);
        var replacement = await ReplacementAsync(store, old);
        var checkedFences = false;
        var fencedStore = new SqlCorpusRebuildStore(environment.Factory, afterProjectionReset: async ct =>
        {
            await using var connection = new SqlConnection(fixture.ConnectionString);
            await connection.OpenAsync(ct);
            foreach (var resource in new[] { "FluxKnowledge.GpuScheduler.Admission", "FluxKnowledge.DerivedIndexRecovery" })
            {
                await using var command = connection.CreateCommand();
                command.CommandText = "DECLARE @result int; EXEC @result=sp_getapplock @Resource=@resource,@LockMode='Shared',@LockOwner='Session',@LockTimeout=0; SELECT @result;";
                command.Parameters.AddWithValue("@resource", resource);
                Assert.True(Convert.ToInt32(await command.ExecuteScalarAsync(ct)) < 0);
            }
            await using var publication = connection.CreateCommand();
            publication.CommandText = "SET LOCK_TIMEOUT 0; UPDATE IndexState SET CorpusVersion=CorpusVersion+1 WHERE Id=1;";
            var refused = await Assert.ThrowsAsync<SqlException>(() => publication.ExecuteNonQueryAsync(ct));
            Assert.Equal(1222, refused.Number);
            checkedFences = true;
        });
        await fencedStore.CommitAsync(replacement, "slot-a", CancellationToken.None);
        Assert.True(checkedFences);
    }

    [NativeSqlServerTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Exact_publish_successor_is_superseded_and_a_foreign_completed_artifact_is_refused(bool corruptParent)
    {
        await using var environment = await SqlToUsearchRebuildTests.PipelineEnvironment.CreateAsync(fixture, "Publish replacement lineage.");
        var (store, old, builder) = await InterruptedAsync(environment.Factory);
        environment.PermitRebuild(old.OperationId);
        var now = DateTimeOffset.UtcNow;
        var outbox = new SqlOutboxStore(environment.Factory, environment.DeploymentHold);
        var dispatch = (await outbox.ClaimNextDueAsync("embed", now, TimeSpan.FromMinutes(2), [PipelineOperations.Embed], CancellationToken.None))!;
        var job = (await new SqlJobClaimStore(environment.Factory, environment.DeploymentHold).ClaimForDispatchAsync(dispatch, "embed", now,
            TimeSpan.FromMinutes(2), CancellationToken.None))!;
        var transitions = new StageTransitionService(new SqlStageTransitionStore(environment.Factory, null, TimeProvider.System),
            new NoEvents(), new ChannelOutboxWakeSignal(), TimeProvider.System);
        await new EmbedStageWorker(environment.Store, new BatchedProvider(environment.Embeddings, old.Profile), transitions, TimeProvider.System,
            new SqlEmbeddingCheckpointStore(environment.Factory, TimeProvider.System))
            .ExecuteAsync(new(dispatch, job), CancellationToken.None);
        var oldPublish = (await outbox.ClaimNextDueAsync("publish", now.AddSeconds(1), TimeSpan.FromMinutes(2), [PipelineOperations.Publish], CancellationToken.None))!;
        await using var context = environment.Factory.CreateDbContext();
        if (corruptParent)
        {
            await context.OutboxMessages.Where(value => value.Id == dispatch.DispatchMessageId.Value)
                .ExecuteUpdateAsync(setters => setters.SetProperty(value => value.CompletedArtifactId, (Guid?)old.Inputs[0].CanonicalArtifactId));
            Assert.Equal("corpus-rebuild-replacement-publish-provenance-invalid", (await Assert.ThrowsAsync<CorpusRebuildRefusalException>(async () =>
                await ReplacementAsync(store, old))).Message);
            return;
        }
        var replacement = await ReplacementAsync(store, old);
        Assert.Equal(2, replacement.Supersession!.JobIds.Count);
        await store.CommitAsync(replacement, "slot-a", CancellationToken.None);
        environment.PermitRebuild(replacement.OperationId);
        await store.PrepareAsync(replacement.OperationId, old.Inputs[0].PipelineRecordId, builder, CancellationToken.None);
        await environment.PumpAsync();
        await FinishAsync(store, replacement.OperationId);
        Assert.Null(await new SqlJobClaimStore(environment.Factory).ClaimForDispatchAsync(oldPublish, "stale-publish", now.AddHours(1),
            TimeSpan.FromMinutes(2), CancellationToken.None));
        Assert.Equal(2, await context.CorpusRebuildSupersededJobs.CountAsync());
        var oldPublishJobId = await context.OutboxMessages.Where(value => value.Id == oldPublish.DispatchMessageId.Value).Select(value => value.JobId).SingleAsync();
        Assert.Equal((int)PublicJobState.WorkerQueued, await context.Jobs.Where(value => value.Id == oldPublishJobId).Select(value => value.PublicState).SingleAsync());
    }

    private sealed class NoEvents : IStatusEventPublisher
    {
        public ValueTask PublishAsync(FluxKnowledge.Application.Contracts.StatusChanged changed, CancellationToken ct) => ValueTask.CompletedTask;
    }

    private sealed class BatchedProvider(IEmbeddingProvider provider, EmbeddingProfile profile) : IBatchedEmbeddingProvider
    {
        public EmbeddingProfile Profile => profile;
        public int MaximumBatchSize => 4;
        public ValueTask<EmbeddingResult> CreateEmbeddingAsync(string text, CancellationToken ct) => provider.CreateEmbeddingAsync(text, ct);
        public async ValueTask<IReadOnlyList<EmbeddingResult>> CreateEmbeddingsAsync(IReadOnlyList<string> texts, CancellationToken ct)
        {
            var results = new List<EmbeddingResult>();
            foreach (var text in texts) results.Add(await provider.CreateEmbeddingAsync(text, ct));
            return results;
        }
    }

    private static ValueTask<CorpusRebuildPlan> ReplacementAsync(SqlCorpusRebuildStore store, CorpusRebuildPlan old) =>
        store.ReadReplacementPlanAsync(Guid.NewGuid(), old.OperationId, old.TargetEpoch, old.ManifestHash, old.Profile, old.PassagePolicyFingerprint, CancellationToken.None);

    private static async Task FinishAsync(SqlCorpusRebuildStore store, Guid operationId)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (true)
        {
            try { await store.FinishAsync(operationId, "slot-a", new UsearchGenerationValidator(), timeout.Token); return; }
            catch (CorpusRebuildRefusalException exception) when (exception.Message == "corpus-rebuild-full-text-not-ready")
            { await Task.Delay(100, timeout.Token); }
        }
    }

    private static async Task<(SqlCorpusRebuildStore, CorpusRebuildPlan, PassageBuilder)> InterruptedAsync(IDbContextFactory<FluxKnowledgeDbContext> factory)
    {
        await using var context = factory.CreateDbContext();
        context.GpuCapacitySlots.Add(new GpuCapacitySlotEntity { SlotKey = "slot-a", UpdatedAtUtc = DateTimeOffset.UtcNow });
        await context.SaveChangesAsync();
        var store = new SqlCorpusRebuildStore(factory);
        var builder = new PassageBuilder(new Tokenizer());
        var profile = await context.IndexGenerations.Where(value => value.IndexPath != "")
            .Select(value => new EmbeddingProfile(value.ModelFingerprint, value.Dimensions)).FirstAsync();
        var old = await store.ReadPlanAsync(Guid.NewGuid(), profile, builder.PolicyFingerprint, CancellationToken.None);
        await store.CommitAsync(old, "slot-a", CancellationToken.None);
        foreach (var input in old.Inputs) await store.PrepareAsync(old.OperationId, input.PipelineRecordId, builder, CancellationToken.None);
        return (store, old, builder);
    }

    private sealed class Tokenizer : IPassageTokenizer
    {
        public int CountTokens(string text) => text.Length;
        public string Fingerprint => "replacement-test-tokenizer";
    }
}
