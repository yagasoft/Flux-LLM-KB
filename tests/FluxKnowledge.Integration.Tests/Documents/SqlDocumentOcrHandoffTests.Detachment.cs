using FluxKnowledge.Application.Documents;
using FluxKnowledge.Application.Gpu;
using FluxKnowledge.Domain.Gpu;
using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using FluxKnowledge.Infrastructure.SqlServer.Workers;
using FluxKnowledge.Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Documents;

public sealed partial class SqlDocumentOcrHandoffTests
{
    [NativeSqlServerFact]
    public async Task Detached_OCR_settles_once_and_keeps_exact_native_capacity_resident()
    {
        var (factory, store, lease, borrow, process, clock) = await CreateBorrowedOcrAsync();
        var handle = lease.Handle;
        Assert.False((await store.PermitNativeStartAsync(lease, process, default)).Accepted);
        Assert.False((await store.PermitResidentNativeStartAsync(lease, borrow with { Version = borrow.Version + 1 },
            process, default)).Accepted);
        Assert.True((await store.PermitResidentNativeStartAsync(lease, borrow, process, default)).Accepted);
        Assert.False((await store.ConfirmCleanupAsync(lease, new(lease.ExecutionInstanceId,
            DocumentOcrCleanupKind.OwnedJobEmpty, process, clock.Now), default)).Accepted);
        var active = Assert.Single(await store.ReadRecoveryCandidatesAsync(default));
        Assert.Equal(borrow, active.ResidentBorrow);
        Assert.False(active.Detached);

        var result = new DocumentOcrExecutionResult(true, "document-ocr-complete", [new(0, 0, [])]);
        var proof = new DocumentOcrResidentIdleProof(borrow.ResidencyId, lease.ExecutionInstanceId, process, "ocr", clock.Now);
        Assert.False((await store.SettleDetachedCompletionAsync(lease, result, borrow,
            proof with { ExecutionInstanceId = Guid.NewGuid() }, default)).Settlement.Accepted);
        Assert.False((await store.SettleDetachedCompletionAsync(lease, result, borrow,
            proof with { ModelKey = "general" }, default)).Settlement.Accepted);
        Assert.False((await store.SettleDetachedCompletionAsync(lease,
            new(true, "document-ocr-complete", [new(1, 0, [])]), borrow, proof, default)).Settlement.Accepted);
        await using (var verify = await factory.CreateDbContextAsync())
        {
            Assert.Null((await verify.DocumentOcrRequests.SingleAsync()).ResultDigest);
            Assert.Equal((int)GpuCapacitySlotState.Reserved, (await verify.GpuCapacitySlots.SingleAsync()).State);
            Assert.Empty(await verify.GpuModelDetachmentReceipts.ToArrayAsync());
        }

        var outcomes = await Task.WhenAll(
            store.SettleDetachedCompletionAsync(lease, result, borrow, proof, default).AsTask(),
            store.SettleDetachedCompletionAsync(lease, result, borrow, proof, default).AsTask());
        var applied = Assert.Single(outcomes, value => value.Settlement.Disposition == DocumentOcrLifecycleDisposition.Applied);
        var idle = Assert.IsType<GpuResidentModelFence>(applied.Idle);
        Assert.Equal(borrow with { Version = borrow.Version + 1 }, idle);
        Assert.Equal(idle, Assert.Single(outcomes,
            value => value.Settlement.Disposition == DocumentOcrLifecycleDisposition.AlreadyApplied).Idle);
        Assert.False((await store.SettleDetachedCompletionAsync(lease, result, borrow with { Version = borrow.Version + 1 },
            proof, default)).Settlement.Accepted);

        await using (var verify = await factory.CreateDbContextAsync())
        {
            var slot = await verify.GpuCapacitySlots.SingleAsync();
            Assert.Equal((int)GpuCapacitySlotState.ResidentIdle, slot.State);
            Assert.Null(slot.ActiveBatchId);
            Assert.NotNull(slot.OwnerKey);
            Assert.Equal((int)GpuBatchState.Completed, (await verify.GpuBatches.SingleAsync()).State);
            Assert.Equal((int)GpuMiniTaskExecutionState.Completed, (await verify.GpuMiniTasks.SingleAsync()).ExecutionState);
            Assert.Equal((int)GpuExecutorDispatchState.Terminal, (await verify.GpuExecutorDispatches.SingleAsync()).State);
            var request = await verify.DocumentOcrRequests.SingleAsync();
            Assert.Equal((int)DocumentOcrExecutionPhase.Settled, request.ExecutionPhase);
            Assert.Null(request.NativeCleanupConfirmedAtUtc);
            Assert.Null(request.CleanupProofKind);
            Assert.Empty(await verify.GpuExecutorEvidence.Where(row =>
                row.EvidenceClass == (int)GpuExecutorEvidenceClass.CapacityReleaseConfirmed).ToListAsync());
            var detached = await verify.GpuModelDetachmentReceipts.SingleAsync();
            Assert.Equal(handle.DispatchId, detached.DispatchId);
            Assert.Equal(lease.ExecutionInstanceId, detached.ExecutionInstanceId);
            Assert.Equal(idle.Version, detached.Version);
            var residency = await verify.GpuModelResidencies.SingleAsync();
            Assert.Equal((int)GpuModelResidencyState.Idle, residency.State);
            Assert.Equal(clock.Now.AddMinutes(2), residency.IdleUntilUtc);
            var root = await verify.SourceRootConfigurations.SingleAsync();
            Assert.False(await SqlSourceDeletionOwnership.HasUnconfirmedWorkAsync(verify, root.Id, default));
            var blockedSql = $$"""
                SELECT 1 AS [Value] FROM Jobs job JOIN PipelineRecords record ON record.Id = job.PipelineRecordId
                WHERE {{SqlRemovedSourceWork.HasUnconfirmedGpuRequests("job", "record")}}
                """;
            Assert.Empty(await verify.Database.SqlQueryRaw<int>(blockedSql).ToArrayAsync());
        }

        var restarted = CreateStore(factory);
        var recovered = Assert.Single(await restarted.ReadRecoveryCandidatesAsync(default));
        Assert.True(recovered.Detached);
        Assert.Null(recovered.ResidentBorrow);
        var saved = Assert.IsType<DocumentOcrPendingCompletion>(await restarted.ReadPendingCompletionAsync(handle, default));
        Assert.Equal(DocumentOcrLifecycleDisposition.AlreadyApplied,
            (await restarted.SettleCompletionAsync(handle, lease.MiniTaskId, saved.ResultDigest, default)).Disposition);
        var signal = new RecordingOutboxWakeSignal();
        await using var provider = new ServiceCollection().AddSingleton(restarted).BuildServiceProvider();
        var recovery = new PaddleOcrVlmCompletionCoordinator(provider.GetRequiredService<IServiceScopeFactory>(),
            signal, new UnexpectedLegacyRecovery());
        await recovery.RecoverAsync(default);
        await recovery.RecoverAsync(default);
        Assert.Equal(1, signal.Notifications);
        Assert.False(await restarted.RequeueCompletedAsync(handle, lease.MiniTaskId, default));
    }

    [NativeSqlServerFact]
    public async Task Native_release_of_the_exact_OCR_resident_job_allows_later_ordinary_settlement()
    {
        var (factory, store, lease, borrow, process, clock) = await CreateBorrowedOcrAsync();
        Assert.True((await store.PermitResidentNativeStartAsync(lease, borrow, process, default)).Accepted);
        var proof = new DocumentOcrResidentReleaseProof(borrow.ResidencyId, lease.ExecutionInstanceId, process, clock.Now);
        Assert.False((await store.ConfirmResidentCleanupAsync(lease, borrow,
            proof with { Process = process with { ProcessId = process.ProcessId + 1 } }, default)).Accepted);
        Assert.True((await store.ConfirmResidentCleanupAsync(lease, borrow, proof, default)).Accepted);
        Assert.Equal(DocumentOcrLifecycleDisposition.AlreadyApplied,
            (await store.ConfirmResidentCleanupAsync(lease, borrow, proof, default)).Disposition);
        await using (var verify = await factory.CreateDbContextAsync())
        {
            var resident = await verify.GpuModelResidencies.SingleAsync();
            Assert.Equal((int)GpuModelResidencyState.Released, resident.State);
            Assert.Equal((int)GpuResidentReleaseKind.ContainedJobEmpty, resident.ReleaseKind);
            var slot = await verify.GpuCapacitySlots.SingleAsync();
            Assert.Equal((int)GpuCapacitySlotState.Reserved, slot.State);
            Assert.Equal(lease.Handle.BatchId, slot.ActiveBatchId);
            var evidence = await verify.GpuExecutorEvidence.SingleAsync(row =>
                row.EvidenceClass == (int)GpuExecutorEvidenceClass.CapacityReleaseConfirmed);
            Assert.Equal("ocr-resident-job-empty-v1", evidence.VerifierKey);
        }

        var saved = await store.StoreResultAsync(lease, new(false, "document-ocr-runtime-timeout", []), default);
        Assert.True((await store.SettleCompletionAsync(lease.Handle, lease.MiniTaskId, saved.ResultDigest, default)).Accepted);
        await using var after = await factory.CreateDbContextAsync();
        Assert.Equal((int)GpuCapacitySlotState.Available, (await after.GpuCapacitySlots.SingleAsync()).State);
        Assert.Empty(await after.GpuModelDetachmentReceipts.ToArrayAsync());
    }

    [NativeSqlServerFact]
    public async Task OCR_idle_proof_at_the_deadline_cannot_park_and_paused_source_cannot_requeue()
    {
        var (factory, store, lease, borrow, process, clock) = await CreateBorrowedOcrAsync();
        Assert.True((await store.PermitResidentNativeStartAsync(lease, borrow, process, default)).Accepted);
        var result = new DocumentOcrExecutionResult(true, "document-ocr-complete", [new(0, 0, [])]);
        var proof = new DocumentOcrResidentIdleProof(borrow.ResidencyId, lease.ExecutionInstanceId, process, "ocr",
            lease.DeadlineAtUtc);
        Assert.False((await store.SettleDetachedCompletionAsync(lease, result, borrow, proof, default)).Settlement.Accepted);
        await using (var update = await factory.CreateDbContextAsync())
        {
            var root = await update.SourceRootConfigurations.SingleAsync();
            root.State = (int)SourceRootState.Paused;
            await update.SaveChangesAsync();
        }
        Assert.Equal(DocumentOcrLifecycleDisposition.SourceWithdrawn,
            (await store.PermitResidentNativeStartAsync(lease, borrow, process, default)).Disposition);
        Assert.True((await store.SettleDetachedCompletionAsync(lease, result, borrow,
            proof with { ObservedAtUtc = clock.Now }, default)).Settlement.Accepted);
        Assert.False(await store.RequeueCompletedAsync(lease.Handle, lease.MiniTaskId, default));
        Assert.Empty(await store.ReadRecoveryCandidatesAsync(default));
    }

    [NativeSqlServerFact]
    public async Task Withdrawn_preassigned_OCR_is_claimed_for_exact_helper_cleanup_and_cannot_start_after_resume()
    {
        var (factory, claim, revision, hash) = await CreateClaimedPdfAsync();
        var store = CreateStore(factory);
        var task = Assert.IsType<Guid>((await store.HandoffAsync(new(claim, revision, hash, [0]), default)).MiniTaskId);
        var handle = await AdmitDocumentOcrTaskAsync(factory);
        var supervisor = SyntheticSupervisor();
        var process = new DocumentOcrProcessIdentity(8124, DateTimeOffset.UtcNow, supervisor.Process.MachineFingerprint);
        var id = Guid.NewGuid();
        var borrow = new GpuResidentModelFence(id, handle.CapacitySlotKey, 3, supervisor.Process,
            new(id, process.ProcessId, process.StartedAtUtc));
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.GpuModelResidencies.Add(new GpuModelResidencyEntity
            {
                SlotKey = handle.CapacitySlotKey, ResidencyId = id, Version = borrow.Version,
                RuntimeKey = PaddleOcrVlmRuntimeContract.ModelRuntimeKey,
                SettingsFingerprint = PaddleOcrVlmRuntimeContract.SettingsFingerprint, ModelKey = "ocr",
                OwnerProcessId = supervisor.Process.ProcessId, OwnerStartedAtUtc = supervisor.Process.StartedAtUtc,
                MachineFingerprint = supervisor.Process.MachineFingerprint, ChildJobId = id,
                ChildProcessId = process.ProcessId, ChildStartedAtUtc = process.StartedAtUtc,
                State = (int)GpuModelResidencyState.Borrowed, BorrowedDispatchId = handle.DispatchId,
                BorrowedExecutionInstanceId = handle.DispatchId,
            });
            await db.SaveChangesAsync();
        }

        var candidate = Assert.Single(await store.ReadRecoveryCandidatesAsync(default));
        Assert.Null(candidate.Lease);
        Assert.Equal(borrow, candidate.ResidentBorrow);
        Assert.False((await store.ClaimAbandonedResidentAsync(handle, borrow, supervisor, ownerExited: false, default)).Accepted);
        Assert.False((await store.TryClaimExecutionAsync(handle, task, Guid.NewGuid(), supervisor, default)).Accepted);
        await using (var db = await factory.CreateDbContextAsync())
        {
            (await db.SourceRootConfigurations.SingleAsync()).State = (int)SourceRootState.Paused;
            await db.SaveChangesAsync();
        }

        var cleanup = await store.ClaimAbandonedResidentAsync(handle, borrow, supervisor, ownerExited: false, default);
        var lease = Assert.IsType<DocumentOcrExecutionLease>(cleanup.Lease);
        Assert.Equal(handle.DispatchId, lease.ExecutionInstanceId);
        Assert.Equal("document-ocr-resident-source-withdrawn", cleanup.ReasonCode);
        await using (var db = await factory.CreateDbContextAsync())
        {
            (await db.SourceRootConfigurations.SingleAsync()).State = (int)SourceRootState.Enabled;
            await db.SaveChangesAsync();
        }
        Assert.False((await store.TryClaimExecutionAsync(handle, task, handle.DispatchId, supervisor, default)).Accepted);
        Assert.False((await store.PermitResidentNativeStartAsync(lease, borrow, process, default)).Accepted);
        Assert.True((await store.ConfirmResidentCleanupAsync(lease, borrow,
            new(id, lease.ExecutionInstanceId, process, DateTimeOffset.UtcNow), default)).Accepted);
        await using var verify = await factory.CreateDbContextAsync();
        var request = await verify.DocumentOcrRequests.SingleAsync();
        Assert.Null(request.NativeProcessId);
        Assert.Equal((int)DocumentOcrCleanupKind.VerifiedUnstarted, request.CleanupProofKind);
        Assert.Equal((int)GpuModelResidencyState.Released, (await verify.GpuModelResidencies.SingleAsync()).State);
    }

    private sealed class UnexpectedLegacyRecovery : IDocumentOcrProcessRecovery
    {
        public ValueTask<DocumentOcrRecoveryObservation> InspectAsync(DocumentOcrExecutionLease lease,
            DocumentOcrProcessIdentity? process, bool hasStoredResult, bool cleanupConfirmed, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Detached OCR recovery must not inspect a job scoped to the execution.");
    }

    private async Task<(IDbContextFactory<FluxKnowledgeDbContext> Factory, SqlDocumentOcrStore Store,
        DocumentOcrExecutionLease Lease, GpuResidentModelFence Borrow, DocumentOcrProcessIdentity Process, OcrClock Clock)>
        CreateBorrowedOcrAsync()
    {
        var (factory, claim, revision, hash) = await CreateClaimedPdfAsync();
        var clock = new OcrClock(DateTimeOffset.UtcNow);
        var store = new SqlDocumentOcrStore(factory, CreateCoordinator(factory), clock,
            sourceAdmission: new FixtureSourceAdmission(factory));
        var task = Assert.IsType<Guid>((await store.HandoffAsync(new(claim, revision, hash, [0]), default)).MiniTaskId);
        var handle = await AdmitDocumentOcrTaskAsync(factory);
        var lease = Assert.IsType<DocumentOcrExecutionLease>((await store.TryClaimExecutionAsync(
            handle, task, Guid.NewGuid(), SyntheticSupervisor(), default)).Lease);
        var process = new DocumentOcrProcessIdentity(8123, clock.Now, lease.Supervisor.Process.MachineFingerprint);
        var residentId = Guid.NewGuid();
        var model = new GpuResidentModelKey(PaddleOcrVlmRuntimeContract.ModelRuntimeKey,
            PaddleOcrVlmRuntimeContract.SettingsFingerprint, "ocr");
        var borrow = Assert.IsType<GpuResidentModelFence>(await new SqlGpuModelResidencyStore(factory, clock)
            .BeginBorrowAsync(handle, residentId, lease.ExecutionInstanceId, model, lease.Supervisor.Process,
                new(residentId, process.ProcessId, process.StartedAtUtc), default));
        return (factory, store, lease, borrow, process, clock);
    }
}
