using FluxKnowledge.Application.Gpu;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Workers;
using FluxKnowledge.Application.Indexing;
using FluxKnowledge.Application.Pipeline;
using FluxKnowledge.Domain.Gpu;
using FluxKnowledge.Domain.Jobs;
using FluxKnowledge.Domain.Pipeline;
using FluxKnowledge.Infrastructure.Inference.Search;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using FluxKnowledge.Infrastructure.SqlServer.Workers;
using FluxKnowledge.Integration.Tests.Support;
using FluxKnowledge.Integrations.Windows;
using FluxKnowledge.Application.Contracts;
using FluxKnowledge.Application.Documents;
using FluxKnowledge.Application.Search;
using FluxKnowledge.Infrastructure.SqlServer.Search;
using FluxKnowledge.Infrastructure.Usearch;
using Microsoft.AspNetCore.DataProtection;
using FluxKnowledge.Application.IntegrationV1;
using FluxKnowledge.Application.IntegrationV1.Corpus;
using FluxKnowledge.Application.Sources;
using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Infrastructure.SqlServer.Visibility;
using System.Text.Json;
using System.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Data.SqlClient;
using FluxKnowledge.Integration.Tests.Models;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Indexing;

[Collection("sql-full-text")]
public sealed class EmbeddingGpuRequestTests(NativeSqlServerFixture fixture) : IClassFixture<NativeSqlServerFixture>, IAsyncLifetime
{
    private static readonly EmbeddingGpuRuntime Runtime = new(BgeOfflineModels.GpuRuntimeKey,
        BgeOfflineModels.GpuSettingsFingerprint, new(BgeOfflineModels.EmbeddingFingerprint, 1024), 1024);
    private static string Text => string.Join("\n\n", Enumerable.Range(0, 12).Select(i => $"Heading {i}. " + new string('a', 900) + "."));

    public async Task InitializeAsync()
    {
        await using var context = await SqlTestData.CreateFactory(fixture).CreateDbContextAsync();
        await context.SourceDeletionCleanupItems.ExecuteDeleteAsync();
        await context.SourceDeletionOperations.ExecuteDeleteAsync();
        await SqlTestData.ClearPhase3SourceDataAsync(fixture);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [NativeSqlServerFact]
    public async Task Deny_all_held_recovery_proves_an_exited_process_and_settles_a_real_rebuild_reservation_without_native_inference()
    {
        await using var environment = await SqlToUsearchRebuildTests.PipelineEnvironment.CreateAsync(fixture, "Rebuild recovery input.");
        var builder = new PassageBuilder(new Words());
        await using var context = environment.Factory.CreateDbContext();
        context.GpuCapacitySlots.Add(new GpuCapacitySlotEntity { SlotKey=PaddleOcrVlmRuntimeContract.CapacitySlotKey, UpdatedAtUtc=DateTimeOffset.UtcNow });
        await context.SaveChangesAsync();
        var rebuild = new SqlCorpusRebuildStore(environment.Factory);
        var plan = await rebuild.ReadPlanAsync(Guid.NewGuid(), Runtime.Profile, builder.PolicyFingerprint, CancellationToken.None);
        await rebuild.CommitAsync(plan, PaddleOcrVlmRuntimeContract.CapacitySlotKey, CancellationToken.None);
        environment.PermitRebuild(plan.OperationId);
        await rebuild.PrepareAsync(plan.OperationId, Assert.Single(plan.Inputs).PipelineRecordId, builder, CancellationToken.None);
        var worker = await ClaimAsync(environment);
        var batch = await new SqlEmbeddingCheckpointStore(environment.Factory, TimeProvider.System).ReadNextAsync(worker, Runtime.Profile, CancellationToken.None);
        var hold = new RecoveryHold { Current = new(true, plan.OperationId) };
        var scheduler = new SqlGpuSchedulerStore(environment.Factory, deploymentValidationHold: hold);
        var wake = new ChannelGpuSchedulerWakeSignal();
        var requests = new SqlEmbeddingGpuRequestStore(environment.Factory, scheduler, wake, Runtime, TimeProvider.System);
        await requests.QueueAsync(worker, batch, CancellationToken.None);
        await scheduler.RunAdmissionRoundAsync(Guid.NewGuid(), GpuSchedulerWakeReason.WorkReady,
            new(4,4096,TimeSpan.FromMinutes(1),TimeSpan.FromSeconds(1),TimeSpan.FromMinutes(1)),
            (_,_) => ValueTask.FromResult(new GpuAdmissionDecision(GpuAdmissionDisposition.Admit,
                PaddleOcrVlmRuntimeContract.CapacitySlotKey,"held-rebuild",null,EmbeddingGpuExecutor.Name)), CancellationToken.None);
        var handle = Assert.Single(await scheduler.ReadPendingDispatchesAsync(CancellationToken.None));
        Assert.True((await scheduler.AcknowledgeAsync(new(Guid.NewGuid(),handle),CancellationToken.None)).Committed);
        var probe = new WindowsInteractiveGpuOwnerProbe();
        var start = new ProcessStartInfo("pwsh") { UseShellExecute=false, CreateNoWindow=true, WindowStyle=ProcessWindowStyle.Hidden };
        foreach (var argument in new[] { "-NoProfile","-NonInteractive","-Command","[Threading.Thread]::Sleep(600000)" }) start.ArgumentList.Add(argument);
        using var child = Process.Start(start)!;
        try
        {
            var owner = new GpuInteractiveOwnerIdentity(child.Id,new DateTimeOffset(child.StartTime.ToUniversalTime()),probe.Current.MachineFingerprint);
            Assert.Equal(GpuInteractiveOwnerObservation.Alive,probe.Observe(owner));
            Assert.NotNull(await requests.ClaimExecutionAsync(handle,Guid.NewGuid(),Guid.NewGuid(),owner,CancellationToken.None));
            var reservation = Assert.Single(await scheduler.ReadStaleCapacityReservationsAsync(DateTimeOffset.UtcNow.AddMinutes(5),CancellationToken.None));
            Assert.True((await scheduler.MarkCapacityUncertainAsync(Guid.NewGuid(),reservation,CancellationToken.None)).Committed);
            hold.Current = new(true,null);
            child.Kill(true);
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(GpuInteractiveOwnerObservation.Exited,probe.Observe(owner));
            var models = new BgeGpuInferenceTests.RecordingModels();
            var recovery = new EmbeddingGpuExecutor(requests,new Lifecycle(scheduler),scheduler,new BgeGpuInferenceSession(models),
                probe,Runtime,new ChannelOutboxWakeSignal(),wake,TimeProvider.System);
            await recovery.RecoverAsync(CancellationToken.None);
            Assert.Empty(models.Events);
            Assert.Equal((int)GpuCapacitySlotState.Available,await context.GpuCapacitySlots.AsNoTracking().Select(value=>value.State).SingleAsync());
            Assert.True((await context.EmbeddingGpuRequests.AsNoTracking().SingleAsync()).NativeCleanupConfirmed);
            Assert.Null(await new SqlOutboxStore(environment.Factory,hold).ClaimNextDueAsync("held",DateTimeOffset.UtcNow,
                TimeSpan.FromMinutes(2),[PipelineOperations.Embed],CancellationToken.None));
            await using var drain = await SqlGpuMaintenanceDrainLease.AcquireAsync(environment.Factory,CancellationToken.None);
            Assert.True(await drain.IsDrainedAsync(PaddleOcrVlmRuntimeContract.CapacitySlotKey,CancellationToken.None));
        }
        finally { if (!child.HasExited) { child.Kill(true); await child.WaitForExitAsync(); } }
    }

    private sealed class RecoveryHold : IDeploymentValidationHold
    {
        public DeploymentHoldAdmission Current { get; set; } = new(true,null);
        public bool IsHeld=>Current.IsHeld;
        public DeploymentHoldAdmission ReadAdmissionState()=>Current;
        public ValueTask WaitUntilReleasedAsync(CancellationToken cancellationToken)=>ValueTask.CompletedTask;
    }

    [NativeSqlServerTheory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    public async Task Background_dispatch_runs_once_and_releases_capacity_only_after_confirmed_native_cleanup(int fault)
    {
        await using var environment = await SqlToUsearchRebuildTests.PipelineEnvironment.CreateAsync(fixture, Text, embed: false);
        var worker = await ClaimAsync(environment);
        var checkpoint = new SqlEmbeddingCheckpointStore(environment.Factory, TimeProvider.System);
        var batch = await checkpoint.ReadNextAsync(worker, Runtime.Profile, CancellationToken.None);
        var scheduler = new SqlGpuSchedulerStore(environment.Factory);
        var wake = new ChannelGpuSchedulerWakeSignal();
        var requests = new SqlEmbeddingGpuRequestStore(environment.Factory, scheduler, wake, Runtime, TimeProvider.System);
        await requests.QueueAsync(worker, batch, CancellationToken.None);
        await using var context = await environment.Factory.CreateDbContextAsync();
        context.GpuCapacitySlots.Add(new GpuCapacitySlotEntity { SlotKey = "gpu-0", State = 0, UpdatedAtUtc = DateTimeOffset.UtcNow });
        await context.SaveChangesAsync();
        var models = new BgeGpuInferenceTests.RecordingModels { Failure = fault switch { 1 => "dispose", 6 or 7 => "run", _ => null } };
        async ValueTask Watchdog()
        {
            var reservation = Assert.Single(await scheduler.ReadStaleCapacityReservationsAsync(DateTimeOffset.UtcNow, CancellationToken.None));
            Assert.True((await scheduler.MarkCapacityUncertainAsync(Guid.NewGuid(), reservation, CancellationToken.None)).Committed);
        }
        IEmbeddingGpuInference inference = fault == 5 ? new UnconfirmedResultInference() : new BgeGpuInferenceSession(models);
        IEmbeddingGpuRequestStore executorRequests = fault is 7 or 8 ? new FailCleanupOnce(requests, fault == 8) : requests;
        var executor = new EmbeddingGpuExecutor(executorRequests, new Lifecycle(scheduler, fault == 2, fault == 3, fault is 4 or 9 ? Watchdog : null, fault == 9), scheduler, inference,
            new WindowsInteractiveGpuOwnerProbe(), Runtime, new ChannelOutboxWakeSignal(), wake, TimeProvider.System);
        await scheduler.RunAdmissionRoundAsync(Guid.NewGuid(), GpuSchedulerWakeReason.WorkReady,
            new(4, 4096, TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(1)),
            (_, _) => ValueTask.FromResult(new GpuAdmissionDecision(GpuAdmissionDisposition.Admit, "gpu-0", "owner", null, executor.ExecutorKey)), CancellationToken.None);
        var handle = Assert.Single(await scheduler.ReadPendingDispatchesAsync(CancellationToken.None));
        var deliveries = Task.WhenAll(Enumerable.Range(0, 4).Select(_ => executor.DeliverAsync(handle, CancellationToken.None).AsTask()));
        if (fault is 2 or 3 or 7 or 8 or 9) await Assert.ThrowsAsync<ResponseLostException>(() => deliveries);
        else await deliveries;
        if (fault == 9) executor = new EmbeddingGpuExecutor(requests, new Lifecycle(scheduler), scheduler, inference,
            new WindowsInteractiveGpuOwnerProbe(), Runtime, new ChannelOutboxWakeSignal(), wake, TimeProvider.System);
        await executor.DeliverAsync(handle, CancellationToken.None);
        if (fault == 5) Assert.Empty(models.Events);
        else Assert.Equal(["embedding.open", "embedding.run", "embedding.dispose"], models.Events);
        if (fault is 1 or 5)
        {
            Assert.Empty(await context.Vectors.ToListAsync());
            Assert.False((await context.EmbeddingGpuRequests.AsNoTracking().SingleAsync()).NativeCleanupConfirmed);
            Assert.Equal((int)GpuCapacitySlotState.Uncertain, await context.GpuCapacitySlots.AsNoTracking().Select(value => value.State).SingleAsync());
            Assert.Equal((int)PublicJobState.GpuProcessing, await context.Jobs.AsNoTracking().Where(value => value.Id == worker.Job.JobId.Value).Select(value => value.PublicState).SingleAsync());
            Assert.Empty(await context.GpuExecutorResultReceipts.ToListAsync());
            return;
        }
        Assert.Equal(fault is 6 or 7 or 8 ? 0 : 4, await context.Vectors.CountAsync());
        Assert.True((await context.EmbeddingGpuRequests.AsNoTracking().SingleAsync()).NativeCleanupConfirmed);
        Assert.Equal(2, (await context.EmbeddingGpuRequests.AsNoTracking().SingleAsync()).State);
        Assert.Equal((int)GpuCapacitySlotState.Available, await context.GpuCapacitySlots.AsNoTracking().Select(value => value.State).SingleAsync());
        Assert.Equal((int)PublicJobState.WorkerQueued, await context.Jobs.AsNoTracking().Where(value => value.Id == worker.Job.JobId.Value).Select(value => value.PublicState).SingleAsync());
        if (fault is 6 or 7 or 8)
        {
            var retry = await context.Jobs.AsNoTracking().SingleAsync(value => value.Id == worker.Job.JobId.Value);
            Assert.Equal("embedding-gpu-retry", retry.Reason);
            Assert.True(retry.DueAtUtc > DateTimeOffset.UtcNow);
            Assert.Single(await context.GpuExecutorResultReceipts.ToListAsync());
            return;
        }
        var next = await ClaimAsync(environment);
        var remaining = await checkpoint.ReadNextAsync(next, Runtime.Profile, CancellationToken.None);
        Assert.DoesNotContain(remaining.Chunks, chunk => batch.Chunks.Any(saved => saved.Id == chunk.Id));
        if (fault is 4 or 9) Assert.Empty(await context.GpuExecutorResultReceipts.ToListAsync());
        else Assert.Single(await context.GpuExecutorResultReceipts.ToListAsync());
    }

    [NativeSqlServerTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Continuation_requires_both_a_settled_task_and_exact_capacity_release_proof(bool outcomeFirst)
    {
        await using var environment = await SqlToUsearchRebuildTests.PipelineEnvironment.CreateAsync(fixture, Text, embed: false);
        var (requests, handle, work, instance) = await PrepareOwnedAsync(environment);
        var scheduler = new SqlGpuSchedulerStore(environment.Factory);
        await requests.RecordNativeCleanupAsync(handle, instance, work.ClaimOperationId, CancellationToken.None);
        Assert.False(await requests.RequeueSettledAsync(handle, work.MiniTaskId, CancellationToken.None));
        var reservation = Assert.Single(await scheduler.ReadStaleCapacityReservationsAsync(DateTimeOffset.UtcNow, CancellationToken.None));
        await scheduler.MarkCapacityUncertainAsync(Guid.NewGuid(), reservation, CancellationToken.None);
        var outcome = new GpuExecutorTrustedEvidence(Guid.NewGuid(), handle, "test-confirmed-cleanup", DateTimeOffset.UtcNow,
            GpuExecutorEvidenceClass.TaskOutcomeUncertainConfirmed);
        var capacity = outcome with { OperationId = Guid.NewGuid(), EvidenceClass = GpuExecutorEvidenceClass.CapacityReleaseConfirmed };
        await scheduler.RecordTrustedEvidenceAsync(outcome, CancellationToken.None);
        await scheduler.RecordTrustedEvidenceAsync(capacity, CancellationToken.None);
        async ValueTask SettleOutcome() => Assert.True((await scheduler.ReconcileTaskOutcomeAsync(Guid.NewGuid(),
            new(handle, outcome.OperationId, work.MiniTaskId), CancellationToken.None)).Committed);
        async ValueTask SettleCapacity() => Assert.True((await scheduler.ReconcileCapacityAsync(Guid.NewGuid(),
            new(handle, capacity.OperationId), CancellationToken.None)).Committed);
        if (outcomeFirst) await SettleOutcome(); else await SettleCapacity();
        Assert.False(await requests.RequeueSettledAsync(handle, work.MiniTaskId, CancellationToken.None));
        Assert.True((await new SqlDerivedIndexRecoveryStore(environment.Factory, TimeProvider.System).ReadActiveAsync(CancellationToken.None)).IsProjectionUnavailable);
        if (outcomeFirst) await SettleCapacity(); else await SettleOutcome();
        Assert.True((await new SqlDerivedIndexRecoveryStore(environment.Factory, TimeProvider.System).ReadActiveAsync(CancellationToken.None)).IsProjectionUnavailable);
        Assert.True(await requests.RequeueSettledAsync(handle, work.MiniTaskId, CancellationToken.None));
        Assert.True(await requests.RequeueSettledAsync(handle, work.MiniTaskId, CancellationToken.None));
    }

    [NativeSqlServerTheory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Paused_embedding_settles_to_a_resumable_parent_and_recovers_the_latest_legacy_attempt(bool saved, bool legacy)
    {
        await using var environment = await PrepareRetainedEnvironmentAsync();
        var (requests, handle, work, instance) = await PrepareOwnedAsync(environment);
        if (legacy)
        {
            for (var i = 0; i < 2; i++)
            {
                await requests.CommitAsync(handle, instance, work, Outputs(work.Batch.Chunks.Count), CancellationToken.None);
                await requests.RecordNativeCleanupAsync(handle, instance, work.ClaimOperationId, CancellationToken.None);
                await DeliverCleanedAsync(environment, requests, handle);
                (requests, handle, work, instance) = await PrepareOwnedAsync(environment);
            }
        }
        if (saved) await requests.CommitAsync(handle, instance, work, Outputs(work.Batch.Chunks.Count), CancellationToken.None);
        await using var context = await environment.Factory.CreateDbContextAsync();
        var root = await context.SourceRootConfigurations.SingleAsync();
        root.State = (int)SourceRootState.Paused;
        await context.SaveChangesAsync();
        await requests.RecordNativeCleanupAsync(handle, instance, work.ClaimOperationId, CancellationToken.None);
        await DeliverCleanedAsync(environment, requests, handle);
        if (legacy)
        {
            // Persist the exact old release's stranded state, with older settled attempts present.
            await context.Jobs.Where(job => job.Id == work.ParentJobId)
                .ExecuteUpdateAsync(set => set.SetProperty(job => job.PublicState, (int)PublicJobState.GpuProcessing));
            Assert.Single(await requests.ReadRecoveryAsync(CancellationToken.None), value => value.MiniTaskId == work.MiniTaskId);
            await new SqlDerivedIndexRecoveryStore(environment.Factory, TimeProvider.System).ReadActiveAsync(CancellationToken.None);
            var hold = new RecoveryHold();
            var guarded = new SqlEmbeddingGpuRequestStore(environment.Factory, new SqlGpuSchedulerStore(environment.Factory),
                new ChannelGpuSchedulerWakeSignal(), Runtime, TimeProvider.System, hold);
            Assert.Empty(await guarded.ReadRecoveryAsync(CancellationToken.None));
            Assert.False(await guarded.RequeueSettledAsync(handle, work.MiniTaskId, CancellationToken.None));
            Assert.Equal((int)PublicJobState.GpuProcessing, await context.Jobs.AsNoTracking().Where(job => job.Id == work.ParentJobId).Select(job => job.PublicState).SingleAsync());
            hold.Current = new(false, null);
            await Task.WhenAll(DeliverCleanedAsync(environment, guarded, handle, recover: true),
                DeliverCleanedAsync(environment, guarded, handle, recover: true));
        }
        await new SqlDerivedIndexRecoveryStore(environment.Factory, TimeProvider.System).ReadActiveAsync(CancellationToken.None);
        Assert.Equal((int)PublicJobState.WorkerQueued, await context.Jobs.AsNoTracking().Where(job => job.Id == work.ParentJobId).Select(job => job.PublicState).SingleAsync());
        Assert.True(await requests.RequeueSettledAsync(handle, work.MiniTaskId, CancellationToken.None));
        Assert.Empty(await requests.ReadRecoveryAsync(CancellationToken.None));
        Assert.Null(await new SqlOutboxStore(environment.Factory).ClaimNextDueAsync("paused", DateTimeOffset.UtcNow.AddSeconds(10),
            TimeSpan.FromMinutes(1), [PipelineOperations.Embed], CancellationToken.None));
        root.State = (int)SourceRootState.Enabled;
        await context.SaveChangesAsync();
        var resumed = await ClaimAsync(environment, now: DateTimeOffset.UtcNow.AddSeconds(10));
        Assert.Equal(work.ParentJobId, resumed.Job.JobId.Value);
        var next = await new SqlEmbeddingCheckpointStore(environment.Factory, TimeProvider.System).ReadNextAsync(resumed, Runtime.Profile, CancellationToken.None);
        if (saved) Assert.DoesNotContain(next.Chunks, chunk => work.Batch.Chunks.Any(previous => previous.Id == chunk.Id));
        else Assert.Equal(work.Batch.Chunks, next.Chunks);
        Assert.Null(await new SqlOutboxStore(environment.Factory).ClaimNextDueAsync("duplicate", DateTimeOffset.UtcNow.AddSeconds(10),
            TimeSpan.FromMinutes(1), [PipelineOperations.Embed], CancellationToken.None));
    }

    [NativeSqlServerFact]
    public async Task Historical_settled_replay_cannot_requeue_a_newer_active_embedding_attempt()
    {
        await using var environment = await PrepareRetainedEnvironmentAsync();
        var (requests, oldHandle, oldWork, instance) = await PrepareOwnedAsync(environment);
        await requests.CommitAsync(oldHandle, instance, oldWork, Outputs(oldWork.Batch.Chunks.Count), CancellationToken.None);
        await requests.RecordNativeCleanupAsync(oldHandle, instance, oldWork.ClaimOperationId, CancellationToken.None);
        await DeliverCleanedAsync(environment, requests, oldHandle);
        var (_, _, next, _) = await PrepareOwnedAsync(environment);
        Assert.True(await requests.RequeueSettledAsync(oldHandle, oldWork.MiniTaskId, CancellationToken.None));
        Assert.Equal(next.MiniTaskId, Assert.Single(await requests.ReadRecoveryAsync(CancellationToken.None)).MiniTaskId);
        await new SqlDerivedIndexRecoveryStore(environment.Factory, TimeProvider.System).ReadActiveAsync(CancellationToken.None);
        await using var context = await environment.Factory.CreateDbContextAsync();
        Assert.Equal((int)PublicJobState.GpuProcessing, await context.Jobs.AsNoTracking().Where(job => job.Id == next.ParentJobId).Select(job => job.PublicState).SingleAsync());
        Assert.Equal((int)GpuCapacitySlotState.Reserved, await context.GpuCapacitySlots.AsNoTracking().Select(slot => slot.State).SingleAsync());
    }

    [NativeSqlServerTheory]
    [InlineData("epoch")]
    [InlineData("revision")]
    [InlineData("suppressed")]
    [InlineData("deleting")]
    [InlineData("dispatch-owner")]
    [InlineData("capacity-proof")]
    public async Task Legacy_settlement_refuses_changed_authority_or_missing_cleanup_capacity_proof(string fault)
    {
        await using var environment = await PrepareRetainedEnvironmentAsync();
        var (requests, handle, work, instance) = await PrepareOwnedAsync(environment);
        await requests.RecordNativeCleanupAsync(handle, instance, work.ClaimOperationId, CancellationToken.None);
        await DeliverCleanedAsync(environment, requests, handle);
        await using var context = await environment.Factory.CreateDbContextAsync();
        (await context.Jobs.SingleAsync(job => job.Id == work.ParentJobId)).PublicState = (int)PublicJobState.GpuProcessing;
        switch (fault)
        {
            case "epoch": (await context.IndexState.SingleAsync()).CorpusEpoch = Guid.NewGuid(); break;
            case "revision": (await context.EmbeddingGpuRequests.SingleAsync()).SourceRevision++; break;
            case "suppressed": (await context.SourceRevisions.SingleAsync()).SuppressedAtUtc = DateTimeOffset.UtcNow; break;
            case "deleting": (await context.SourceRootConfigurations.SingleAsync()).State = (int)SourceRootState.Deleting; break;
            case "dispatch-owner": (await context.GpuExecutorDispatches.SingleAsync()).OwnerKey = "foreign"; break;
            case "capacity-proof": (await context.GpuBatches.SingleAsync()).State = (int)GpuBatchState.CapacityUncertain; break;
        }
        await context.SaveChangesAsync();
        Assert.False(await requests.RequeueSettledAsync(handle, work.MiniTaskId, CancellationToken.None));
        Assert.DoesNotContain(await requests.ReadRecoveryAsync(CancellationToken.None), candidate => candidate.MiniTaskId == work.MiniTaskId);
        await Assert.ThrowsAsync<InvalidOperationException>(() => new SqlDerivedIndexRecoveryStore(environment.Factory, TimeProvider.System)
            .ReadActiveAsync(CancellationToken.None).AsTask());
        Assert.Equal((int)PublicJobState.GpuProcessing, await context.Jobs.AsNoTracking().Where(job => job.Id == work.ParentJobId).Select(job => job.PublicState).SingleAsync());
    }

    private async Task<SqlToUsearchRebuildTests.PipelineEnvironment> PrepareRetainedEnvironmentAsync()
    {
        var environment = await SqlToUsearchRebuildTests.PipelineEnvironment.CreateAsync(fixture, "Control draft.", embed: false);
        _ = await ClaimAsync(environment);
        await environment.AddRetainedAndPumpAsync(string.Join("\n\n", Enumerable.Repeat(Text, 4)));
        return environment;
    }

    private static async Task DeliverCleanedAsync(SqlToUsearchRebuildTests.PipelineEnvironment environment,
        SqlEmbeddingGpuRequestStore requests, GpuExecutorBatchHandle handle, bool recover = false)
    {
        var scheduler = new SqlGpuSchedulerStore(environment.Factory);
        var models = new BgeGpuInferenceTests.RecordingModels();
        var executor = new EmbeddingGpuExecutor(requests, new Lifecycle(scheduler), scheduler, new BgeGpuInferenceSession(models),
            new WindowsInteractiveGpuOwnerProbe(), Runtime, new ChannelOutboxWakeSignal(), new ChannelGpuSchedulerWakeSignal(), TimeProvider.System);
        if (recover) await executor.RecoverAsync(CancellationToken.None);
        else await executor.DeliverAsync(handle, CancellationToken.None);
        Assert.Empty(models.Events);
    }

    [NativeSqlServerFact]
    public async Task Embed_worker_queues_the_profiled_batch_without_running_inference_under_its_worker_lease()
    {
        await using var environment = await SqlToUsearchRebuildTests.PipelineEnvironment.CreateAsync(fixture, Text, embed: false);
        var work = await ClaimAsync(environment);
        var requests = new SqlEmbeddingGpuRequestStore(environment.Factory, new SqlGpuSchedulerStore(environment.Factory),
            new ChannelGpuSchedulerWakeSignal(), Runtime, TimeProvider.System);
        var worker = new EmbedStageWorker(environment.Store, new RefuseInference(),
            new StageTransitionService(new SqlStageTransitionStore(environment.Factory), new NoEvents(), new ChannelOutboxWakeSignal(), TimeProvider.System),
            TimeProvider.System, new SqlEmbeddingCheckpointStore(environment.Factory, TimeProvider.System), requests);
        await worker.ExecuteAsync(work, CancellationToken.None);
        await using var context = await environment.Factory.CreateDbContextAsync();
        Assert.Single(await context.EmbeddingGpuRequests.ToListAsync());
        Assert.Single(await context.GpuMiniTasks.ToListAsync());
        Assert.Empty(await context.Vectors.ToListAsync());
        Assert.Equal((int)PublicJobState.GpuQueued, await context.Jobs.Where(value => value.Id == work.Job.JobId.Value).Select(value => value.PublicState).SingleAsync());
    }

    [NativeSqlServerTheory]
    [InlineData(GpuInteractiveOwnerObservation.Alive, false)]
    [InlineData(GpuInteractiveOwnerObservation.Unknown, false)]
    [InlineData(GpuInteractiveOwnerObservation.Exited, false)]
    [InlineData(GpuInteractiveOwnerObservation.Exited, true)]
    public async Task Restart_releases_only_after_exact_process_exit_and_preserves_saved_checkpoint(
        GpuInteractiveOwnerObservation observation, bool checkpointSaved)
    {
        await using var environment = await SqlToUsearchRebuildTests.PipelineEnvironment.CreateAsync(fixture, Text, embed: false);
        var (requests, handle, work, instance) = await PrepareOwnedAsync(environment);
        if (checkpointSaved) await requests.CommitAsync(handle, instance, work, Outputs(4), CancellationToken.None);
        var scheduler = new SqlGpuSchedulerStore(environment.Factory);
        var models = new BgeGpuInferenceTests.RecordingModels();
        var executor = new EmbeddingGpuExecutor(requests, new Lifecycle(scheduler), scheduler, new BgeGpuInferenceSession(models),
            new ObservedProcess(observation), Runtime, new ChannelOutboxWakeSignal(), new ChannelGpuSchedulerWakeSignal(), TimeProvider.System);
        await executor.RecoverAsync(CancellationToken.None);
        await executor.RecoverAsync(CancellationToken.None);
        Assert.Empty(models.Events);
        await using var context = await environment.Factory.CreateDbContextAsync();
        var request = await context.EmbeddingGpuRequests.AsNoTracking().SingleAsync();
        Assert.Equal(checkpointSaved ? 4 : 0, await context.Vectors.CountAsync());
        Assert.Equal(observation == GpuInteractiveOwnerObservation.Exited, request.NativeCleanupConfirmed);
        Assert.Equal(observation == GpuInteractiveOwnerObservation.Exited ? 2 : 0, request.State);
        Assert.Equal(observation == GpuInteractiveOwnerObservation.Exited ? (int)GpuCapacitySlotState.Available : (int)GpuCapacitySlotState.Reserved,
            await context.GpuCapacitySlots.Select(value => value.State).SingleAsync());
        Assert.Equal(observation == GpuInteractiveOwnerObservation.Exited ? (int)PublicJobState.WorkerQueued : (int)PublicJobState.GpuProcessing,
            await context.Jobs.Where(value => value.Id == work.ParentJobId).Select(value => value.PublicState).SingleAsync());
        Assert.True((await new SqlDerivedIndexRecoveryStore(environment.Factory, TimeProvider.System).ReadActiveAsync(CancellationToken.None)).IsProjectionUnavailable);
    }

    [NativeSqlServerTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Startup_closes_unbound_acknowledged_or_uncertain_work_without_native_execution(bool uncertain)
    {
        await using var environment = await SqlToUsearchRebuildTests.PipelineEnvironment.CreateAsync(fixture, Text, embed: false);
        var (requests, handle, work, _) = await PrepareOwnedAsync(environment);
        await using var context = await environment.Factory.CreateDbContextAsync();
        await context.EmbeddingGpuRequests.ExecuteUpdateAsync(update => update
            .SetProperty(value => value.ExecutorInstanceId, (Guid?)null).SetProperty(value => value.ClaimOperationId, (Guid?)null)
            .SetProperty(value => value.OwnerProcessId, (int?)null).SetProperty(value => value.OwnerStartedAtUtc, (DateTimeOffset?)null)
            .SetProperty(value => value.OwnerMachineFingerprint, (string?)null).SetProperty(value => value.DispatchId, (Guid?)null));
        var scheduler = new SqlGpuSchedulerStore(environment.Factory);
        if (uncertain)
            await scheduler.MarkCapacityUncertainAsync(Guid.NewGuid(), Assert.Single(await scheduler.ReadStaleCapacityReservationsAsync(DateTimeOffset.UtcNow, CancellationToken.None)), CancellationToken.None);
        var models = new BgeGpuInferenceTests.RecordingModels();
        var executor = new EmbeddingGpuExecutor(requests, new Lifecycle(scheduler), scheduler, new BgeGpuInferenceSession(models),
            new ObservedProcess(GpuInteractiveOwnerObservation.Unknown), Runtime, new ChannelOutboxWakeSignal(), new ChannelGpuSchedulerWakeSignal(), TimeProvider.System);
        await executor.RecoverAsync(CancellationToken.None);
        await executor.RecoverAsync(CancellationToken.None);
        Assert.Empty(models.Events);
        Assert.Empty(await context.Vectors.ToListAsync());
        Assert.Equal(2, (await context.EmbeddingGpuRequests.AsNoTracking().SingleAsync()).State);
        Assert.Equal((int)GpuCapacitySlotState.Available, await context.GpuCapacitySlots.Select(value => value.State).SingleAsync());
        Assert.Equal((int)PublicJobState.WorkerQueued, await context.Jobs.Where(value => value.Id == work.ParentJobId).Select(value => value.PublicState).SingleAsync());
        Assert.Null(await requests.ClaimExecutionAsync(handle, Guid.NewGuid(), Guid.NewGuid(), new WindowsInteractiveGpuOwnerProbe().Current, CancellationToken.None));
    }

    [NativeSqlServerTheory]
    [InlineData("matching")]
    [InlineData("missing")]
    [InlineData("other-source")]
    [InlineData("other-runtime")]
    [InlineData("other-generation")]
    public async Task Public_root_delete_accepts_only_exact_embedding_ownership_and_drains_before_purge(string binding)
    {
        await using var environment = await SqlToUsearchRebuildTests.PipelineEnvironment.CreateAsync(fixture, "Control draft.", embed: false);
        var otherWorker = await ClaimAsync(environment);
        var checkpoints = new SqlEmbeddingCheckpointStore(environment.Factory, TimeProvider.System);
        var other = await checkpoints.ReadNextAsync(otherWorker, Runtime.Profile, CancellationToken.None);
        var recordId = await environment.AddRetainedAndPumpAsync(Text);
        var worker = await ClaimAsync(environment);
        Assert.Equal(recordId, worker.Job.PipelineRecordId.Value);
        var batch = await checkpoints.ReadNextAsync(worker, Runtime.Profile, CancellationToken.None);
        var scheduler = new SqlGpuSchedulerStore(environment.Factory);
        var requests = new SqlEmbeddingGpuRequestStore(environment.Factory, scheduler, new ChannelGpuSchedulerWakeSignal(), Runtime, TimeProvider.System);
        await requests.QueueAsync(worker, batch, CancellationToken.None);
        await using var context = await environment.Factory.CreateDbContextAsync();
        context.GpuCapacitySlots.Add(new GpuCapacitySlotEntity { SlotKey = "gpu-0", State = 0, UpdatedAtUtc = DateTimeOffset.UtcNow });
        await context.SaveChangesAsync();
        await scheduler.RunAdmissionRoundAsync(Guid.NewGuid(), GpuSchedulerWakeReason.WorkReady,
            new(4, 4096, TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(1)),
            (_, _) => ValueTask.FromResult(new GpuAdmissionDecision(GpuAdmissionDisposition.Admit, "gpu-0", "owner", null, EmbeddingGpuExecutor.Name)), CancellationToken.None);
        var handle = Assert.Single(await scheduler.ReadPendingDispatchesAsync(CancellationToken.None));
        await scheduler.AcknowledgeAsync(new(Guid.NewGuid(), handle), CancellationToken.None);
        Assert.NotNull(await requests.ClaimExecutionAsync(handle, Guid.NewGuid(), Guid.NewGuid(), new WindowsInteractiveGpuOwnerProbe().Current, CancellationToken.None));
        var request = await context.EmbeddingGpuRequests.SingleAsync();
        switch (binding)
        {
            case "missing": context.EmbeddingGpuRequests.Remove(request); break;
            case "other-source": request.PipelineRecordId = otherWorker.Job.PipelineRecordId.Value; break;
            case "other-runtime": (await context.GpuMiniTasks.SingleAsync()).ModelRuntimeKey = "unknown-runtime"; break;
            case "other-generation": request.GenerationId = other.GenerationId; break;
        }
        await context.SaveChangesAsync();
        var rootId = await (from record in context.PipelineRecords join revision in context.SourceRevisions on record.SourceRevisionId equals revision.Id
            where record.Id == recordId select revision.SourceRootId).SingleAsync();
        var service = new NativeCorpusCommandService(new SqlNativeOperationStore(environment.Factory, TimeProvider.System, embeddingRuntime: Runtime),
            new SqlNativeCorpusActionStore(environment.Factory, new NoRootCreationPolicy(), new LocalPrivateContentDisclosure()));
        var mutation = new NativeCorpusMutation("root_delete", JsonSerializer.SerializeToElement(new { rootId }));
        var preview = await service.PreviewAsync(mutation, "test", CancellationToken.None);
        if (binding != "matching")
        {
            var refusal = await Assert.ThrowsAsync<NativeOperationException>(() => service.CommitAsync(mutation, preview.ConfirmationId,
                $"embedding-delete:{rootId:N}", "test", CancellationToken.None).AsTask());
            Assert.Equal("source-delete-external-execution-owned", refusal.ReasonCode);
            Assert.Equal((int)SourceRootState.Enabled, await context.SourceRootConfigurations.AsNoTracking().Where(value => value.Id == rootId).Select(value => value.State).SingleAsync());
            return;
        }
        await service.CommitAsync(mutation, preview.ConfirmationId, $"embedding-delete:{rootId:N}", "test", CancellationToken.None);
        var deletion = new SqlSourceDeletionStore(environment.Factory, TimeProvider.System, embeddingRuntime: Runtime);
        var draining = await deletion.ClaimNextAsync(CancellationToken.None);
        Assert.NotNull(draining);
        Assert.Equal("draining", (await deletion.PurgeAsync(draining, null, CancellationToken.None)).Phase);
        Assert.True(await context.EmbeddingGpuRequests.AnyAsync());
        Assert.False((await context.PipelineRecords.AsNoTracking().SingleAsync(value => value.Id == recordId)).IsDeleted);
        var models = new BgeGpuInferenceTests.RecordingModels();
        var executor = new EmbeddingGpuExecutor(requests, new Lifecycle(scheduler), scheduler, new BgeGpuInferenceSession(models),
            new ObservedProcess(GpuInteractiveOwnerObservation.Exited), Runtime, new ChannelOutboxWakeSignal(), new ChannelGpuSchedulerWakeSignal(), TimeProvider.System);
        await executor.RecoverAsync(CancellationToken.None);
        Assert.Empty(models.Events);
        Assert.Equal((int)GpuCapacitySlotState.Available, await context.GpuCapacitySlots.AsNoTracking().Select(value => value.State).SingleAsync());
        Assert.Equal((int)PublicJobState.GpuProcessing, await context.Jobs.AsNoTracking().Where(value => value.Id == worker.Job.JobId.Value).Select(value => value.PublicState).SingleAsync());
        var first = await deletion.ClaimNextAsync(CancellationToken.None);
        Assert.NotNull(first);
        // GPU process exit does not establish termination of an independently
        // claimed outbox worker. Preserve that existing lease drain as well.
        Assert.Equal("draining", (await deletion.PurgeAsync(first, null, CancellationToken.None)).Phase);
        await context.OutboxMessages.Where(value => value.Id == worker.DispatchMessage.DispatchMessageId.Value)
            .ExecuteUpdateAsync(update => update.SetProperty(value => value.LeaseExpiresAtUtc, DateTimeOffset.UtcNow.AddSeconds(-1)));
        first = await deletion.ClaimNextAsync(CancellationToken.None);
        Assert.NotNull(first);
        Assert.True((await deletion.PurgeAsync(first, null, CancellationToken.None)).RequiresIndexBuild);
        var second = await deletion.ClaimNextAsync(CancellationToken.None);
        Assert.NotNull(second);
        Assert.Equal("cleanup-files", (await deletion.PurgeAsync(second, null, CancellationToken.None)).Phase);
        Assert.Empty(await context.EmbeddingGpuRequests.AsNoTracking().ToListAsync());
        Assert.Empty(await context.GpuMiniTasks.AsNoTracking().ToListAsync());
        Assert.Empty(await context.GpuBatches.AsNoTracking().ToListAsync());
        Assert.True(await context.IndexGenerations.AnyAsync(value => value.Id == other.GenerationId && value.EmbeddingJobId == otherWorker.Job.JobId.Value && value.RetiredAtUtc == null));
        Assert.True(await context.PipelineRecords.AnyAsync(value => value.Id == otherWorker.Job.PipelineRecordId.Value));
    }

    [NativeSqlServerFact]
    public async Task Unstarted_recovery_cannot_close_a_request_claimed_after_its_snapshot()
    {
        await using var environment = await SqlToUsearchRebuildTests.PipelineEnvironment.CreateAsync(fixture, Text, embed: false);
        var (requests, handle, _, _) = await PrepareOwnedAsync(environment);
        await using var context = await environment.Factory.CreateDbContextAsync();
        await context.EmbeddingGpuRequests.ExecuteUpdateAsync(update => update
            .SetProperty(value => value.ExecutorInstanceId, (Guid?)null).SetProperty(value => value.ClaimOperationId, (Guid?)null)
            .SetProperty(value => value.OwnerProcessId, (int?)null).SetProperty(value => value.OwnerStartedAtUtc, (DateTimeOffset?)null)
            .SetProperty(value => value.OwnerMachineFingerprint, (string?)null).SetProperty(value => value.DispatchId, (Guid?)null));
        var unstarted = Assert.Single(await requests.ReadRecoveryAsync(CancellationToken.None));
        Assert.Null(unstarted.Owner);
        var owner = new WindowsInteractiveGpuOwnerProbe().Current;
        var claimed = await requests.ClaimExecutionAsync(handle, Guid.NewGuid(), Guid.NewGuid(), owner, CancellationToken.None);
        Assert.NotNull(claimed);
        Assert.False(await requests.ConfirmUnstartedCleanupAsync(unstarted, Guid.NewGuid(), Guid.NewGuid(), owner, CancellationToken.None));
        var bound = Assert.Single(await requests.ReadRecoveryAsync(CancellationToken.None));
        Assert.False(await requests.ConfirmExitedCleanupAsync(bound with { Owner = owner with { ProcessId = owner.ProcessId + 1 } }, CancellationToken.None));
        Assert.False((await context.EmbeddingGpuRequests.AsNoTracking().SingleAsync()).NativeCleanupConfirmed);
        Assert.Equal((int)GpuCapacitySlotState.Reserved, await context.GpuCapacitySlots.Select(value => value.State).SingleAsync());
    }

    [NativeSqlServerFact]
    public async Task Source_delete_requests_cooperative_stop_but_waits_for_native_cleanup_before_releasing_capacity()
    {
        await using var environment = await SqlToUsearchRebuildTests.PipelineEnvironment.CreateAsync(fixture, "Control.", embed: false);
        _ = await ClaimAsync(environment);
        var recordId = await environment.AddRetainedAndPumpAsync(Text);
        var worker = await ClaimAsync(environment);
        var batch = await new SqlEmbeddingCheckpointStore(environment.Factory, TimeProvider.System).ReadNextAsync(worker, Runtime.Profile, CancellationToken.None);
        var scheduler = new SqlGpuSchedulerStore(environment.Factory);
        var requests = new SqlEmbeddingGpuRequestStore(environment.Factory, scheduler, new ChannelGpuSchedulerWakeSignal(), Runtime, TimeProvider.System);
        await requests.QueueAsync(worker, batch, CancellationToken.None);
        await using var context = await environment.Factory.CreateDbContextAsync();
        context.GpuCapacitySlots.Add(new GpuCapacitySlotEntity { SlotKey = "gpu-0", State = 0, UpdatedAtUtc = DateTimeOffset.UtcNow });
        await context.SaveChangesAsync();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var finishNative = new ManualResetEventSlim();
        var models = new BgeGpuInferenceTests.RecordingModels { BeforeRun = () => { started.TrySetResult(); Assert.True(finishNative.Wait(TimeSpan.FromSeconds(10))); } };
        var executor = new EmbeddingGpuExecutor(requests, new Lifecycle(scheduler), scheduler, new BgeGpuInferenceSession(models),
            new WindowsInteractiveGpuOwnerProbe(), Runtime, new ChannelOutboxWakeSignal(), new ChannelGpuSchedulerWakeSignal(), TimeProvider.System);
        await scheduler.RunAdmissionRoundAsync(Guid.NewGuid(), GpuSchedulerWakeReason.WorkReady,
            new(4, 4096, TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(1)),
            (_, _) => ValueTask.FromResult(new GpuAdmissionDecision(GpuAdmissionDisposition.Admit, "gpu-0", "owner", null, EmbeddingGpuExecutor.Name)), CancellationToken.None);
        var delivery = executor.DeliverAsync(Assert.Single(await scheduler.ReadPendingDispatchesAsync(CancellationToken.None)), CancellationToken.None).AsTask();
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var rootId = await (from record in context.PipelineRecords join revision in context.SourceRevisions on record.SourceRevisionId equals revision.Id
                where record.Id == recordId select revision.SourceRootId).SingleAsync();
            (await context.SourceRootConfigurations.SingleAsync(value => value.Id == rootId)).State = (int)SourceRootState.Deleting;
            context.SourceDeletionOperations.Add(new SourceDeletionOperationEntity
            {
                Id = Guid.NewGuid(), SourceRootId = rootId, State = 0, Phase = "accepted", CreatedAtUtc = DateTimeOffset.UtcNow, UpdatedAtUtc = DateTimeOffset.UtcNow
            });
            await context.SaveChangesAsync();
            var deletion = new SqlSourceDeletionStore(environment.Factory, TimeProvider.System, embeddingRuntime: Runtime, localEmbeddingExecutions: executor);
            var operation = await deletion.ClaimNextAsync(CancellationToken.None);
            Assert.NotNull(operation);
            Assert.Equal("draining", (await deletion.PurgeAsync(operation, null, CancellationToken.None)).Phase);
            Assert.Equal((int)GpuCapacitySlotState.Reserved, await context.GpuCapacitySlots.AsNoTracking().Select(value => value.State).SingleAsync());
            Assert.False((await context.EmbeddingGpuRequests.AsNoTracking().SingleAsync()).NativeCleanupConfirmed);
            Assert.Empty(await context.Vectors.ToArrayAsync());
        }
        finally { finishNative.Set(); await delivery.WaitAsync(TimeSpan.FromSeconds(5)); }
        Assert.Equal(["embedding.open", "embedding.run", "embedding.dispose"], models.Events);
        Assert.True((await context.EmbeddingGpuRequests.AsNoTracking().SingleAsync()).NativeCleanupConfirmed);
        Assert.Equal((int)GpuCapacitySlotState.Available, await context.GpuCapacitySlots.AsNoTracking().Select(value => value.State).SingleAsync());
        Assert.Equal((int)PublicJobState.GpuProcessing, await context.Jobs.AsNoTracking().Where(value => value.Id == worker.Job.JobId.Value).Select(value => value.PublicState).SingleAsync());
        Assert.Empty(await context.Vectors.ToArrayAsync());
    }

    [NativeSqlServerFact]
    public async Task Bounded_gpu_batches_resume_seal_publish_and_return_a_native_ann_result()
    {
        await using var environment = await SqlToUsearchRebuildTests.PipelineEnvironment.CreateAsync(fixture, Text,
            new PassageBuilder(new Words()), embed: false);
        var scheduler = new SqlGpuSchedulerStore(environment.Factory);
        var policy = new GpuWorkloadPolicy([
            new(PaddleOcrVlmRuntimeContract.ModelRuntimeKey, PaddleOcrVlmRuntimeContract.SettingsFingerprint, GpuWorkloadKind.Ocr),
            new(Runtime.RuntimeKey, Runtime.SettingsFingerprint, GpuWorkloadKind.Retrieval)]);
        var gate = new SharedGpuAdmissionGate(policy, EmbeddingGpuExecutor.Name, Runtime.EstimatedBytes);
        var options = new GpuSchedulerOptions(1, Runtime.EstimatedBytes, TimeSpan.FromMinutes(1),
            TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(1), policy);
        var requests = new SqlEmbeddingGpuRequestStore(environment.Factory, scheduler, new ChannelGpuSchedulerWakeSignal(), Runtime, TimeProvider.System);
        var transitions = new StageTransitionService(new SqlStageTransitionStore(environment.Factory), new NoEvents(), new ChannelOutboxWakeSignal(), TimeProvider.System);
        var worker = new EmbedStageWorker(environment.Store, new RefuseInference(), transitions, TimeProvider.System,
            new SqlEmbeddingCheckpointStore(environment.Factory, TimeProvider.System), requests);
        var models = new BgeGpuInferenceTests.RecordingModels();
        var executor = new EmbeddingGpuExecutor(requests, new Lifecycle(scheduler), scheduler, new BgeGpuInferenceSession(models),
            new WindowsInteractiveGpuOwnerProbe(), Runtime, new ChannelOutboxWakeSignal(), new ChannelGpuSchedulerWakeSignal(), TimeProvider.System);
        await using var context = await environment.Factory.CreateDbContextAsync();
        context.GpuCapacitySlots.Add(new GpuCapacitySlotEntity { SlotKey = PaddleOcrVlmRuntimeContract.CapacitySlotKey, State = 0, UpdatedAtUtc = DateTimeOffset.UtcNow });
        await context.SaveChangesAsync();
        var attempts = 0;
        while (true)
        {
            Assert.True(attempts < 16);
            await worker.ExecuteAsync(await ClaimAsync(environment), CancellationToken.None);
            if (await context.Artifacts.AnyAsync(value => value.Stage == (int)FluxKnowledge.Domain.Pipeline.PipelineStage.Embed)) break;
            await scheduler.RunAdmissionRoundAsync(Guid.NewGuid(), GpuSchedulerWakeReason.WorkReady, options,
                gate.DecideAsync, CancellationToken.None);
            await executor.DeliverAsync(Assert.Single(await scheduler.ReadPendingDispatchesAsync(CancellationToken.None)), CancellationToken.None);
            attempts++;
        }
        Assert.True(attempts > 1);
        Assert.Equal(attempts, models.Events.Count(value => value == "embedding.run"));
        Assert.Equal(attempts, models.Events.Count(value => value == "embedding.dispose"));
        Assert.All(await context.EmbeddingGpuRequests.AsNoTracking().ToArrayAsync(), request => { Assert.Equal(2, request.State); Assert.True(request.NativeCleanupConfirmed); });
        Assert.Equal(await context.TextChunks.CountAsync(), await context.Vectors.CountAsync());
        Assert.Equal((int)GpuCapacitySlotState.Available, await context.GpuCapacitySlots.Select(value => value.State).SingleAsync());
        await new PublishStageWorker(environment.Store, environment.Store, environment.Builder, transitions, TimeProvider.System)
            .ExecuteAsync(await ClaimAsync(environment, PipelineOperations.Publish), CancellationToken.None);
        var active = await environment.ActiveGenerationAsync();
        Assert.Equal(1024, active.Dimensions);
        Assert.Equal(Runtime.Profile.ModelFingerprint, active.ModelFingerprint);
        var vectors = await environment.Store.ReadVectorsAsync(active.Id, CancellationToken.None);
        Assert.Contains(Assert.Single(await environment.Reader.SearchAsync(Outputs(1)[0].Values, 1, CancellationToken.None)).VectorId,
            vectors.Select(vector => vector.VectorId));
        Assert.False((await new SqlDerivedIndexRecoveryStore(environment.Factory, TimeProvider.System).ReadActiveAsync(CancellationToken.None)).IsProjectionUnavailable);

        // Foreground retrieval uses the same durable admission, private execution and release path.
        var foreground = new GpuInteractiveExecutor(scheduler, new Lifecycle(scheduler), scheduler,
            new ChannelGpuSchedulerWakeSignal(), TimeProvider.System, Runtime.RuntimeKey, Runtime.SettingsFingerprint, Runtime.EstimatedBytes);
        var reader = new SqlCorpusRetrievalReader(environment.Factory);
        var codec = new CorpusEvidenceCodec(new EphemeralDataProtectionProvider());
        var engine = new HybridPassageRetrievalEngine(reader, reader,
            new SqlCorpusGenerationLeaseStore(environment.Factory, TimeProvider.System),
            new UsearchCorpusAnnLeaseFactory(environment.Store, new UsearchGenerationValidator()),
            new BgeScheduledPassageInference(foreground, new BgeGpuInferenceSession(models)),
            new WindowsInteractiveGpuOwnerProbe(), codec, new LocalPrivateContentDisclosure());
        var search = engine.SearchAsync(new("Heading", 2, "all", null, null), CancellationToken.None).AsTask();
        GpuExecutorBatchHandle? handle = null;
        var dispatchDeadline = DateTimeOffset.UtcNow.AddSeconds(1.5);
        while (handle is null && DateTimeOffset.UtcNow < dispatchDeadline)
        {
            await scheduler.RunAdmissionRoundAsync(Guid.NewGuid(), GpuSchedulerWakeReason.WorkReady, options,
                gate.DecideAsync, CancellationToken.None);
            handle = (await scheduler.ReadPendingDispatchesAsync(CancellationToken.None)).SingleOrDefault();
            if (handle is null) await Task.Delay(10);
        }
        Assert.NotNull(handle);
        Assert.Equal(foreground.ExecutorKey, handle.ExecutorKey);
        await foreground.DeliverAsync(handle, CancellationToken.None);
        var result = await search;
        Assert.Equal("ready", result.SemanticStatus);
        Assert.Equal(2, result.Results.Count);
        Assert.All(result.Results, hit => Assert.Contains("rerank:trained", hit.Explanation));
        var corpus = new CorpusRetrievalService(reader, codec, new LocalPrivateContentDisclosure(), new(true), engine);
        foreach (var hit in result.Results)
            Assert.Equal(hit.Passage, (await corpus.ReadAsync(new(hit.EvidenceRef, 0), CancellationToken.None)).Text);
        Assert.Equal(["embedding.open", "embedding.run", "embedding.dispose", "ranking.open", "ranking.run", "ranking.dispose"], models.Events.TakeLast(6));
        Assert.Empty(await context.CorpusQueryLeases.AsNoTracking().ToArrayAsync());
        Assert.Equal((int)GpuCapacitySlotState.Available, await context.GpuCapacitySlots.AsNoTracking().Select(value => value.State).SingleAsync());
    }

    [NativeSqlServerFact]
    public async Task Three_failed_attempts_exhaust_one_batch_without_acquiring_a_fourth_reservation()
    {
        await using var environment = await SqlToUsearchRebuildTests.PipelineEnvironment.CreateAsync(fixture, Text, embed: false);
        var scheduler = new SqlGpuSchedulerStore(environment.Factory);
        var requests = new SqlEmbeddingGpuRequestStore(environment.Factory, scheduler, new ChannelGpuSchedulerWakeSignal(), Runtime, TimeProvider.System);
        var checkpoints = new SqlEmbeddingCheckpointStore(environment.Factory, TimeProvider.System);
        var models = new BgeGpuInferenceTests.RecordingModels { Failure = "run" };
        var executor = new EmbeddingGpuExecutor(requests, new Lifecycle(scheduler), scheduler, new BgeGpuInferenceSession(models),
            new WindowsInteractiveGpuOwnerProbe(), Runtime, new ChannelOutboxWakeSignal(), new ChannelGpuSchedulerWakeSignal(), TimeProvider.System);
        await using var context = await environment.Factory.CreateDbContextAsync();
        context.GpuCapacitySlots.Add(new GpuCapacitySlotEntity { SlotKey = "gpu-0", State = 0, UpdatedAtUtc = DateTimeOffset.UtcNow });
        await context.SaveChangesAsync();
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var work = await ClaimAsync(environment, now: DateTimeOffset.UtcNow.AddSeconds(10));
            await requests.QueueAsync(work, await checkpoints.ReadNextAsync(work, Runtime.Profile, CancellationToken.None), CancellationToken.None);
            await scheduler.RunAdmissionRoundAsync(Guid.NewGuid(), GpuSchedulerWakeReason.WorkReady,
                new(4, 4096, TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(1)),
                (_, _) => ValueTask.FromResult(new GpuAdmissionDecision(GpuAdmissionDisposition.Admit, "gpu-0", "owner", null, EmbeddingGpuExecutor.Name)), CancellationToken.None);
            await executor.DeliverAsync(Assert.Single(await scheduler.ReadPendingDispatchesAsync(CancellationToken.None)), CancellationToken.None);
        }
        var last = await ClaimAsync(environment, now: DateTimeOffset.UtcNow.AddSeconds(10));
        var lastBatch = await checkpoints.ReadNextAsync(last, Runtime.Profile, CancellationToken.None);
        var refusal = await Assert.ThrowsAsync<InvalidOperationException>(() => requests.QueueAsync(last,
            lastBatch, CancellationToken.None).AsTask());
        Assert.Equal("embedding-gpu-batch-retry-limit", refusal.Message);
        var transitions = new StageTransitionService(new SqlStageTransitionStore(environment.Factory), new NoEvents(), new ChannelOutboxWakeSignal(), TimeProvider.System);
        await transitions.FailAsync(new(last.DispatchMessage, last.Job, refusal.Message, null, nameof(EmbedStageWorker)), CancellationToken.None);
        Assert.Equal(3, await context.EmbeddingGpuRequests.CountAsync());
        Assert.Equal(3, models.Events.Count(value => value == "embedding.run"));
        Assert.Empty(await context.Vectors.ToArrayAsync());
        Assert.Equal((int)PublicJobState.Failed, await context.Jobs.Where(value => value.Id == last.Job.JobId.Value).Select(value => value.PublicState).SingleAsync());
        Assert.Equal((int)GpuCapacitySlotState.Available, await context.GpuCapacitySlots.Select(value => value.State).SingleAsync());
    }

    [NativeSqlServerFact]
    public async Task Handoff_is_durable_idempotent_and_authorises_gpu_checkpoint_only_after_exact_acknowledgement()
    {
        await using var environment = await SqlToUsearchRebuildTests.PipelineEnvironment.CreateAsync(fixture, Text, embed: false);
        var work = await ClaimAsync(environment);
        var checkpoints = new SqlEmbeddingCheckpointStore(environment.Factory, TimeProvider.System);
        var batch = await checkpoints.ReadNextAsync(work, Runtime.Profile, CancellationToken.None);
        var scheduler = new SqlGpuSchedulerStore(environment.Factory);
        var requests = new SqlEmbeddingGpuRequestStore(environment.Factory, scheduler, new ChannelGpuSchedulerWakeSignal(), Runtime, TimeProvider.System);
        await requests.QueueAsync(work, batch, CancellationToken.None);
        await requests.QueueAsync(work, batch, CancellationToken.None);
        Assert.True((await new SqlDerivedIndexRecoveryStore(environment.Factory, TimeProvider.System).ReadActiveAsync(CancellationToken.None)).IsProjectionUnavailable);
        await using var context = await environment.Factory.CreateDbContextAsync();
        var request = Assert.Single(await context.EmbeddingGpuRequests.ToArrayAsync());
        Assert.Equal(batch.GenerationId, request.GenerationId);
        Assert.Equal((int)PublicJobState.GpuQueued, await context.Jobs.Where(job => job.Id == work.Job.JobId.Value).Select(job => job.PublicState).SingleAsync());
        Assert.Single(await context.GpuMiniTasks.ToArrayAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => checkpoints.CommitAsync(work, batch, Outputs(4), CancellationToken.None).AsTask());
        context.GpuCapacitySlots.Add(new GpuCapacitySlotEntity { SlotKey = "gpu-0", State = 0, UpdatedAtUtc = DateTimeOffset.UtcNow });
        await context.SaveChangesAsync();
        var options = new GpuSchedulerOptions(4, 4096, TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(1));
        await scheduler.RunAdmissionRoundAsync(Guid.NewGuid(), GpuSchedulerWakeReason.WorkReady, options,
            (_, _) => ValueTask.FromResult(new GpuAdmissionDecision(GpuAdmissionDisposition.Admit, "gpu-0", "owner", null, "embedding-gpu:bge-m3-v1")), CancellationToken.None);
        var handle = Assert.Single(await scheduler.ReadPendingDispatchesAsync(CancellationToken.None));
        var instance = Guid.NewGuid();
        var owner = new WindowsInteractiveGpuOwnerProbe().Current;
        var claimOperation = Guid.NewGuid();
        Assert.Null(await requests.ClaimExecutionAsync(handle, instance, claimOperation, owner, CancellationToken.None));
        Assert.True((await scheduler.AcknowledgeAsync(new(Guid.NewGuid(), handle), CancellationToken.None)).Committed);
        var execution = await requests.ClaimExecutionAsync(handle, instance, claimOperation, owner, CancellationToken.None);
        Assert.NotNull(execution);
        Assert.True((await new SqlDerivedIndexRecoveryStore(environment.Factory, TimeProvider.System).ReadActiveAsync(CancellationToken.None)).IsProjectionUnavailable);
        Assert.Equal(batch.GenerationId, execution.Batch.GenerationId);
        Assert.Equal(batch.Chunks, execution.Batch.Chunks);
        var replay = await requests.ClaimExecutionAsync(handle, instance, claimOperation, owner, CancellationToken.None);
        Assert.NotNull(replay);
        Assert.Equal(execution.Batch.Chunks, replay.Batch.Chunks);
        Assert.Null(await requests.ClaimExecutionAsync(handle, instance, Guid.NewGuid(), owner, CancellationToken.None));
        Assert.Null(await requests.ClaimExecutionAsync(handle, Guid.NewGuid(), claimOperation, owner, CancellationToken.None));
        await requests.CommitAsync(handle, instance, execution, Outputs(4), CancellationToken.None);
        await requests.CommitAsync(handle, instance, execution, Outputs(4), CancellationToken.None);
        Assert.True((await new SqlDerivedIndexRecoveryStore(environment.Factory, TimeProvider.System).ReadActiveAsync(CancellationToken.None)).IsProjectionUnavailable);
        Assert.Equal(4, await context.Vectors.CountAsync());
        var completed = await context.EmbeddingGpuRequests.AsNoTracking().SingleAsync();
        Assert.Equal(1, completed.State);
        Assert.Equal(32, completed.ResultDigest!.Length);
        Assert.False(completed.NativeCleanupConfirmed);
        Assert.Equal((int)GpuCapacitySlotState.Reserved, await context.GpuCapacitySlots.AsNoTracking().Select(slot => slot.State).SingleAsync());
    }

    [NativeSqlServerTheory]
    [InlineData("input")]
    [InlineData("epoch")]
    [InlineData("profile")]
    public async Task Handoff_refuses_changed_batch_without_queueing_work(string change)
    {
        await using var environment = await SqlToUsearchRebuildTests.PipelineEnvironment.CreateAsync(fixture, Text, embed: false);
        var work = await ClaimAsync(environment);
        var batch = await new SqlEmbeddingCheckpointStore(environment.Factory, TimeProvider.System).ReadNextAsync(work, Runtime.Profile, CancellationToken.None);
        batch = change switch
        {
            "input" => batch with { Chunks = batch.Chunks.Select(chunk => chunk with { ContextHeader = "changed" }).ToArray() },
            "epoch" => batch with { CorpusEpoch = Guid.NewGuid() },
            _ => batch with { Profile = new("wrong-model", 1024) }
        };
        var requests = new SqlEmbeddingGpuRequestStore(environment.Factory, new SqlGpuSchedulerStore(environment.Factory),
            new ChannelGpuSchedulerWakeSignal(), Runtime, TimeProvider.System);
        await Assert.ThrowsAsync<InvalidOperationException>(() => requests.QueueAsync(work, batch, CancellationToken.None).AsTask());
        await using var context = await environment.Factory.CreateDbContextAsync();
        Assert.Empty(await context.EmbeddingGpuRequests.ToListAsync());
        Assert.Empty(await context.GpuMiniTasks.ToListAsync());
    }

    [NativeSqlServerTheory]
    [InlineData("owner")]
    [InlineData("epoch")]
    [InlineData("withdrawn")]
    [InlineData("partial")]
    [InlineData("wrong-model")]
    [InlineData("non-finite")]
    public async Task Checkpoint_commit_refuses_lost_identity_or_invalid_complete_batch_without_writing_vectors(string fault)
    {
        await using var environment = await SqlToUsearchRebuildTests.PipelineEnvironment.CreateAsync(fixture, Text, embed: false);
        var (requests, handle, execution, instance) = await PrepareOwnedAsync(environment);
        var results = Outputs(4);
        await using var context = await environment.Factory.CreateDbContextAsync();
        switch (fault)
        {
            case "owner": instance = Guid.NewGuid(); break;
            case "epoch": (await context.IndexState.SingleAsync()).CorpusEpoch = Guid.NewGuid(); break;
            case "withdrawn": (await context.PipelineRecords.SingleAsync(value => value.Id == execution.PipelineRecordId)).IsDeleted = true; break;
            case "partial": results = Outputs(3); break;
            case "wrong-model": results = results.Select(result => result with { ModelFingerprint = "wrong" }).ToArray(); break;
            case "non-finite": var invalid = results[0].Values.ToArray(); invalid[0] = float.NaN; results = [new(invalid, Runtime.Profile.ModelFingerprint), ..results.Skip(1)]; break;
        }
        await context.SaveChangesAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => requests.CommitAsync(handle, instance, execution, results, CancellationToken.None).AsTask());
        Assert.Empty(await context.Vectors.ToListAsync());
        var request = await context.EmbeddingGpuRequests.AsNoTracking().SingleAsync();
        Assert.Null(request.ResultDigest);
        Assert.False(request.NativeCleanupConfirmed);
        Assert.Equal((int)GpuCapacitySlotState.Reserved, await context.GpuCapacitySlots.AsNoTracking().Select(slot => slot.State).SingleAsync());
    }

    [NativeSqlServerTheory]
    [InlineData("dispatch-owner")]
    [InlineData("batch-slot")]
    [InlineData("batch-state")]
    public async Task Commit_refuses_contradictory_dispatch_batch_and_physical_slot_fences(string fault)
    {
        await using var environment = await SqlToUsearchRebuildTests.PipelineEnvironment.CreateAsync(fixture, Text, embed: false);
        var (requests, handle, execution, instance) = await PrepareOwnedAsync(environment);
        await using var context = await environment.Factory.CreateDbContextAsync();
        if (fault == "dispatch-owner") (await context.GpuExecutorDispatches.SingleAsync()).OwnerKey = "contradictory";
        else if (fault == "batch-state") (await context.GpuBatches.SingleAsync()).State = (int)GpuBatchState.Released;
        else
        {
            context.GpuCapacitySlots.Add(new GpuCapacitySlotEntity { SlotKey = "other-slot", State = 0, UpdatedAtUtc = DateTimeOffset.UtcNow });
            await context.SaveChangesAsync();
            (await context.GpuBatches.SingleAsync()).CapacitySlotKey = "other-slot";
        }
        await context.SaveChangesAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => requests.CommitAsync(handle, instance, execution, Outputs(4), CancellationToken.None).AsTask());
        Assert.Empty(await context.Vectors.ToListAsync());
        Assert.Null((await context.EmbeddingGpuRequests.AsNoTracking().SingleAsync()).ResultDigest);
    }

    [NativeSqlServerFact]
    public async Task Scheduler_admits_only_one_embedding_request_even_when_two_four_passage_batches_are_ready()
    {
        await using var environment = await SqlToUsearchRebuildTests.PipelineEnvironment.CreateAsync(fixture, Text, embed: false);
        var scheduler = new SqlGpuSchedulerStore(environment.Factory);
        var requests = new SqlEmbeddingGpuRequestStore(environment.Factory, scheduler, new ChannelGpuSchedulerWakeSignal(), Runtime, TimeProvider.System);
        var checkpoint = new SqlEmbeddingCheckpointStore(environment.Factory, TimeProvider.System);
        var first = await ClaimAsync(environment);
        await requests.QueueAsync(first, await checkpoint.ReadNextAsync(first, Runtime.Profile, CancellationToken.None), CancellationToken.None);
        await environment.AddRetainedAndPumpAsync(Text);
        var second = await ClaimAsync(environment);
        await requests.QueueAsync(second, await checkpoint.ReadNextAsync(second, Runtime.Profile, CancellationToken.None), CancellationToken.None);
        await using var context = await environment.Factory.CreateDbContextAsync();
        context.GpuCapacitySlots.Add(new GpuCapacitySlotEntity { SlotKey = "gpu-0", State = 0, UpdatedAtUtc = DateTimeOffset.UtcNow });
        await context.SaveChangesAsync();
        await scheduler.RunAdmissionRoundAsync(Guid.NewGuid(), GpuSchedulerWakeReason.WorkReady,
            new(4, 4096, TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(1)),
            (candidate, _) =>
            {
                Assert.Equal(1, candidate.ItemCount);
                return ValueTask.FromResult(new GpuAdmissionDecision(GpuAdmissionDisposition.Admit, "gpu-0", "owner", null, "bge-background"));
            }, CancellationToken.None);
        Assert.Single(await context.GpuMiniTasks.Where(task => task.ExecutionState == (int)GpuMiniTaskExecutionState.Active).ToListAsync());
        Assert.Single(await context.GpuMiniTasks.Where(task => task.ExecutionState == (int)GpuMiniTaskExecutionState.Ready).ToListAsync());
    }

    [NativeSqlServerFact]
    public async Task Migration_refuses_to_discard_a_durable_background_request_and_empty_round_trip_is_valid()
    {
        await using var environment = await SqlToUsearchRebuildTests.PipelineEnvironment.CreateAsync(fixture, Text, embed: false);
        var work = await ClaimAsync(environment);
        var request = new SqlEmbeddingGpuRequestStore(environment.Factory, new SqlGpuSchedulerStore(environment.Factory),
            new ChannelGpuSchedulerWakeSignal(), Runtime, TimeProvider.System);
        await request.QueueAsync(work, await new SqlEmbeddingCheckpointStore(environment.Factory, TimeProvider.System)
            .ReadNextAsync(work, Runtime.Profile, CancellationToken.None), CancellationToken.None);
        await using var context = await environment.Factory.CreateDbContextAsync();
        var migrations = context.Database.GetMigrations().ToArray();
        var index = Array.FindIndex(migrations, value => value.EndsWith("_AddEmbeddingGpuRequests", StringComparison.Ordinal));
        Assert.True(index > 0);
        var refusal = await Assert.ThrowsAsync<SqlException>(() => context.GetService<IMigrator>().MigrateAsync(migrations[index - 1]));
        Assert.Contains("embedding-gpu-downgrade-requires-empty-reset", refusal.Message);
        Assert.Single(await context.EmbeddingGpuRequests.ToListAsync());
        // Newer migrations may already have been downgraded before this older no-loss guard refuses.
        await context.Database.MigrateAsync();
        await SqlTestData.ClearPipelineAsync(fixture);
        await context.GetService<IMigrator>().MigrateAsync(migrations[index - 1]);
        await context.Database.MigrateAsync();
        Assert.Empty(await context.EmbeddingGpuRequests.ToListAsync());
    }

    private static async Task<(SqlEmbeddingGpuRequestStore Requests, GpuExecutorBatchHandle Handle, EmbeddingGpuExecutionWork Work, Guid Instance)>
        PrepareOwnedAsync(SqlToUsearchRebuildTests.PipelineEnvironment environment)
    {
        var work = await ClaimAsync(environment, now: DateTimeOffset.UtcNow.AddSeconds(10));
        var batch = await new SqlEmbeddingCheckpointStore(environment.Factory, TimeProvider.System).ReadNextAsync(work, Runtime.Profile, CancellationToken.None);
        var scheduler = new SqlGpuSchedulerStore(environment.Factory);
        var requests = new SqlEmbeddingGpuRequestStore(environment.Factory, scheduler, new ChannelGpuSchedulerWakeSignal(), Runtime, TimeProvider.System);
        await requests.QueueAsync(work, batch, CancellationToken.None);
        await using var context = await environment.Factory.CreateDbContextAsync();
        if (!await context.GpuCapacitySlots.AnyAsync())
            context.GpuCapacitySlots.Add(new GpuCapacitySlotEntity { SlotKey = "gpu-0", State = 0, UpdatedAtUtc = DateTimeOffset.UtcNow });
        await context.SaveChangesAsync();
        await scheduler.RunAdmissionRoundAsync(Guid.NewGuid(), GpuSchedulerWakeReason.WorkReady,
            new(4, 4096, TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(1)),
            (_, _) => ValueTask.FromResult(new GpuAdmissionDecision(GpuAdmissionDisposition.Admit, "gpu-0", "owner", null, EmbeddingGpuExecutor.Name)), CancellationToken.None);
        var handle = Assert.Single(await scheduler.ReadPendingDispatchesAsync(CancellationToken.None));
        await scheduler.AcknowledgeAsync(new(Guid.NewGuid(), handle), CancellationToken.None);
        var instance = Guid.NewGuid();
        var execution = await requests.ClaimExecutionAsync(handle, instance, Guid.NewGuid(), new WindowsInteractiveGpuOwnerProbe().Current, CancellationToken.None);
        Assert.NotNull(execution);
        return (requests, handle, execution, instance);
    }

    internal static IReadOnlyList<EmbeddingResult> Outputs(int count) => Enumerable.Range(0, count).Select(_ =>
    {
        var values = new float[1024]; values[0] = 1; return new EmbeddingResult(values, Runtime.Profile.ModelFingerprint);
    }).ToArray();

    private sealed class RefuseInference : IEmbeddingProvider
    {
        public ValueTask<EmbeddingResult> CreateEmbeddingAsync(string text, CancellationToken ct)
            => throw new InvalidOperationException("Worker must hand off rather than run inference.");
    }

    private sealed class UnconfirmedResultInference : IEmbeddingGpuInference
    {
        public ValueTask<GpuInteractiveNativeResult<IReadOnlyList<EmbeddingResult>>> EmbedBatchAsync(GpuOwnedWorkContext ownership, IReadOnlyList<string> texts, CancellationToken ct)
            => ValueTask.FromResult(new GpuInteractiveNativeResult<IReadOnlyList<EmbeddingResult>>(Outputs(texts.Count), false));
    }

    private sealed class NoEvents : IStatusEventPublisher
    {
        public ValueTask PublishAsync(FluxKnowledge.Application.Contracts.StatusChanged changed, CancellationToken ct) => ValueTask.CompletedTask;
    }

    private sealed class ResponseLostException : IOException;

    private sealed class FailCleanupOnce(IEmbeddingGpuRequestStore inner, bool failCommit) : IEmbeddingGpuRequestStore
    {
        private int _cleanupCalls;
        public EmbeddingProfile Profile => inner.Profile;
        public ValueTask QueueAsync(StageWorkItem work, EmbeddingWorkBatch batch, CancellationToken ct) => inner.QueueAsync(work, batch, ct);
        public ValueTask<EmbeddingGpuExecutionWork?> ClaimExecutionAsync(GpuExecutorBatchHandle handle, Guid instance, Guid operation,
            GpuInteractiveOwnerIdentity owner, CancellationToken ct) => inner.ClaimExecutionAsync(handle, instance, operation, owner, ct);
        public ValueTask CommitAsync(GpuExecutorBatchHandle handle, Guid instance, EmbeddingGpuExecutionWork work,
            IReadOnlyList<EmbeddingResult> results, CancellationToken ct)
            => failCommit ? ValueTask.FromException(new IOException("checkpoint-before-commit")) : inner.CommitAsync(handle, instance, work, results, ct);
        public ValueTask RecordNativeCleanupAsync(GpuExecutorBatchHandle handle, Guid instance, Guid operation, CancellationToken ct)
            => Interlocked.Increment(ref _cleanupCalls) == 1 ? ValueTask.FromException(new ResponseLostException())
                : inner.RecordNativeCleanupAsync(handle, instance, operation, ct);
        public ValueTask<EmbeddingGpuCompletion?> ReadPendingCompletionAsync(GpuExecutorBatchHandle handle, CancellationToken ct)
            => inner.ReadPendingCompletionAsync(handle, ct);
        public ValueTask<bool> RequeueSettledAsync(GpuExecutorBatchHandle handle, Guid taskId, CancellationToken ct) => inner.RequeueSettledAsync(handle, taskId, ct);
        public ValueTask<IReadOnlyList<EmbeddingGpuRecoveryWork>> ReadRecoveryAsync(CancellationToken ct) => inner.ReadRecoveryAsync(ct);
        public ValueTask<bool> ConfirmUnstartedCleanupAsync(EmbeddingGpuRecoveryWork work, Guid instance, Guid operation, GpuInteractiveOwnerIdentity owner, CancellationToken ct)
            => inner.ConfirmUnstartedCleanupAsync(work, instance, operation, owner, ct);
        public ValueTask<bool> ConfirmExitedCleanupAsync(EmbeddingGpuRecoveryWork work, CancellationToken ct) => inner.ConfirmExitedCleanupAsync(work, ct);
    }

    private sealed class ObservedProcess(GpuInteractiveOwnerObservation observation) : IGpuInteractiveOwnerProbe
    {
        public GpuInteractiveOwnerIdentity Current { get; } = new WindowsInteractiveGpuOwnerProbe().Current;
        public GpuInteractiveOwnerObservation Observe(GpuInteractiveOwnerIdentity owner) => observation;
    }

    private sealed class NoRootCreationPolicy : ISourceRootPathPolicy
    {
        public SourceRootPathValidation ValidateAndCanonicalise(SourceRootCreateRequest request) => throw new NotSupportedException();
    }

    private sealed class Words : IPassageTokenizer
    {
        public string Fingerprint => "synthetic-scheduled-word-v1";
        public int CountTokens(string text) => text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
    }

    private sealed class Lifecycle(SqlGpuSchedulerStore store, bool loseReceipt = false, bool loseCallback = false, Func<ValueTask>? beforeReceipt = null,
        bool loseEvidence = false) : IGpuExecutorLifecycleSink
    {
        private int _receiptCalls;
        private int _callbackCalls;
        private int _evidenceCalls;
        public ValueTask<GpuExecutorDispatchMutationResult> AcknowledgeAsync(GpuExecutorAcknowledgement value, CancellationToken ct) => store.AcknowledgeAsync(value, ct);
        public ValueTask<GpuExecutorDispatchMutationResult> MarkDeliveryUncertainAsync(GpuExecutorDeliveryUncertainty value, CancellationToken ct) => store.MarkDeliveryUncertainAsync(value, ct);
        public async ValueTask<GpuExecutorDispatchMutationResult> RecordReceiptAsync(GpuExecutorResultReceipt value, CancellationToken ct)
        {
            var first = Interlocked.Increment(ref _receiptCalls) == 1;
            if (first && beforeReceipt is not null) await beforeReceipt();
            var result = await store.RecordReceiptAsync(value, ct);
            if (first && loseReceipt) throw new ResponseLostException();
            return result;
        }
        public async ValueTask<GpuExecutorDispatchMutationResult> RecordTrustedEvidenceAsync(GpuExecutorTrustedEvidence value, CancellationToken ct)
        {
            var result = await store.RecordTrustedEvidenceAsync(value, ct);
            if (Interlocked.Increment(ref _evidenceCalls) == 1 && loseEvidence) throw new ResponseLostException();
            return result;
        }
        public async ValueTask<GpuBatchCallbackResult> HandleCallbackAsync(Guid operation, GpuBatchCallback value, CancellationToken ct)
        {
            var result = await store.ApplyBatchCallbackAsync(operation, value, ct);
            if (Interlocked.Increment(ref _callbackCalls) == 1 && loseCallback) throw new ResponseLostException();
            return result;
        }
    }

    private static async Task<StageWorkItem> ClaimAsync(SqlToUsearchRebuildTests.PipelineEnvironment environment, string operation = PipelineOperations.Embed,
        DateTimeOffset? now = null)
    {
        var claimedAt = now ?? DateTimeOffset.UtcNow;
        var dispatch = await new SqlOutboxStore(environment.Factory, environment.DeploymentHold).ClaimNextDueAsync("embedding-gpu-dispatch", claimedAt,
            TimeSpan.FromMinutes(2), [operation], CancellationToken.None);
        Assert.NotNull(dispatch);
        var job = await new SqlJobClaimStore(environment.Factory, environment.DeploymentHold).ClaimForDispatchAsync(dispatch, "embedding-gpu-worker", claimedAt,
            TimeSpan.FromMinutes(2), CancellationToken.None);
        Assert.NotNull(job);
        return new(dispatch, job);
    }
}
