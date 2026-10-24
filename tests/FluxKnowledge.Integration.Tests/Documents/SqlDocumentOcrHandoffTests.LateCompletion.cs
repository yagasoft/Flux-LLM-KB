using FluxKnowledge.Application.Documents;
using FluxKnowledge.Application.Gpu;
using FluxKnowledge.Domain.Gpu;
using FluxKnowledge.Domain.Jobs;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.SqlServer.Workers;
using FluxKnowledge.Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Documents;

public sealed partial class SqlDocumentOcrHandoffTests
{
    [NativeSqlServerFact]
    public async Task Ordinary_OCR_callback_cannot_release_capacity_without_verified_cleanup()
    {
        var (factory, claim, revisionId, hash) = await CreateClaimedPdfAsync();
        var store = CreateStore(factory);
        var handoff = await store.HandoffAsync(new(claim, revisionId, hash, [0]), CancellationToken.None);
        var miniTaskId = Assert.IsType<Guid>(handoff.MiniTaskId);
        var handle = await AdmitDocumentOcrTaskAsync(factory);
        var lifecycle = CreateLifecycle(factory);
        Assert.True((await lifecycle.AcknowledgeAsync(new(Guid.NewGuid(), handle), CancellationToken.None)).Accepted);
        var stored = await store.StoreResultAsync(handle, miniTaskId,
            new(true, "document-ocr-complete", [new(0, 0, [new("text", 1, 2, 3, 4, "retained OCR")])]),
            CancellationToken.None);
        Assert.True((await lifecycle.RecordReceiptAsync(new(Guid.NewGuid(), handle, miniTaskId,
            GpuMiniTaskBoundaryDisposition.Completed, stored.ResultDigest, GpuExecutorEvidenceClass.TaskOutcomeConfirmed),
            CancellationToken.None)).Accepted);

        var completion = await lifecycle.HandleCallbackAsync(Guid.NewGuid(), new(handle,
            GpuBatchCallbackKind.Completed, [new(miniTaskId, GpuMiniTaskBoundaryDisposition.Completed)], true),
            CancellationToken.None);

        Assert.False(completion.Accepted);
        await using var verify = await factory.CreateDbContextAsync();
        Assert.Equal((int)GpuCapacitySlotState.Reserved, await verify.GpuCapacitySlots.Select(s => s.State).SingleAsync());
        Assert.Equal((int)GpuMiniTaskExecutionState.Active, await verify.GpuMiniTasks.Select(t => t.ExecutionState).SingleAsync());
    }

    [NativeSqlServerFact]
    public async Task Successful_OCR_arriving_after_capacity_uncertainty_settles_with_verified_cleanup_and_resumes_original_job()
    {
        var (factory, claim, revisionId, hash) = await CreateClaimedPdfAsync();
        var ocrStore = CreateStore(factory);
        var handoff = await ocrStore.HandoffAsync(new(claim, revisionId, hash, [0]), CancellationToken.None);
        var miniTaskId = Assert.IsType<Guid>(handoff.MiniTaskId);
        var handle = await AdmitDocumentOcrTaskAsync(factory);
        var owned = await ocrStore.TryClaimExecutionAsync(handle, miniTaskId, Guid.NewGuid(), SyntheticSupervisor(), CancellationToken.None);
        var lease = Assert.IsType<DocumentOcrExecutionLease>(owned.Lease);
        Assert.Equal(DocumentOcrLifecycleDisposition.Applied, owned.Disposition);
        var schedulerStore = new SqlGpuSchedulerStore(factory);
        var reservation = Assert.Single(await schedulerStore.ReadStaleCapacityReservationsAsync(
            DateTimeOffset.UtcNow.AddDays(1), CancellationToken.None));
        Assert.True((await schedulerStore.MarkCapacityUncertainAsync(Guid.NewGuid(), reservation, CancellationToken.None)).Committed);
        await ocrStore.StoreResultAsync(lease,
            new(true, "document-ocr-complete", [new(0, 0, [new("text", 1, 2, 3, 4, "late stored OCR")])]),
            CancellationToken.None);

        // The synthetic executor never starts a native process; start permission and this proof are mutually exclusive.
        Assert.True((await ocrStore.ConfirmCleanupAsync(lease, new(lease.ExecutionInstanceId,
            DocumentOcrCleanupKind.VerifiedUnstarted, null, DateTimeOffset.UtcNow), CancellationToken.None)).Accepted);

        var signal = new RecordingOutboxWakeSignal();
        await using var provider = CreateLocalExecutorProvider(factory, revisionId, hash,
            new BlockingCountingDocumentOcrExecutor(), signal, new TestHostApplicationLifetime());
        var coordinator = provider.GetRequiredService<PaddleOcrVlmCompletionCoordinator>();
        await coordinator.RecoverAsync(CancellationToken.None);

        await using var verify = await factory.CreateDbContextAsync();
        Assert.Equal((int)GpuCapacitySlotState.Available, await verify.GpuCapacitySlots.Select(s => s.State).SingleAsync());
        Assert.Equal((int)GpuMiniTaskExecutionState.Completed, await verify.GpuMiniTasks.Select(t => t.ExecutionState).SingleAsync());
        Assert.Equal((int)GpuBatchState.Completed, await verify.GpuBatches.Select(b => b.State).SingleAsync());
        Assert.Equal((int)GpuExecutorDispatchState.Terminal, await verify.GpuExecutorDispatches.Select(d => d.State).SingleAsync());
        Assert.Equal((int)PublicJobState.WorkerQueued, await verify.Jobs.Where(j => j.Id == claim.JobId.Value).Select(j => j.PublicState).SingleAsync());
        Assert.Equal((int)DocumentOcrRequestState.Requeued, await verify.DocumentOcrRequests.Select(r => r.State).SingleAsync());
        var result = await ocrStore.ReadCompletedAsync(claim, hash, CancellationToken.None);
        Assert.Equal("late stored OCR", Assert.Single(Assert.Single(Assert.IsType<DocumentOcrExecutionResult>(result).Pages).Blocks).Text);
        await coordinator.RecoverAsync(CancellationToken.None);
        Assert.Equal(1, signal.Notifications);

        var resumed = await new SqlJobClaimStore(factory).ClaimNextDueAsync("resumed-ocr-consumer",
            DateTimeOffset.UtcNow, TimeSpan.FromMinutes(5), CancellationToken.None);
        Assert.NotNull(resumed);
        Assert.Equal(claim.JobId, resumed.JobId);
        var (_, nextClaim, nextRevision, nextHash) = await CreateClaimedPdfAsync(clearData: false);
        var nextTask = Assert.IsType<Guid>((await ocrStore.HandoffAsync(new(nextClaim, nextRevision, nextHash, [0]), CancellationToken.None)).MiniTaskId);
        var nextHandle = await AdmitDocumentOcrTaskAsync(factory);
        Assert.NotEqual(handle.BatchId, nextHandle.BatchId);
        Assert.Equal(DocumentOcrLifecycleDisposition.Applied, (await ocrStore.TryClaimExecutionAsync(nextHandle,
            nextTask, Guid.NewGuid(), SyntheticSupervisor(), CancellationToken.None)).Disposition);
        var saved = Assert.IsType<DocumentOcrPendingCompletion>(await ocrStore.ReadPendingCompletionAsync(handle, CancellationToken.None));
        Assert.Equal(DocumentOcrLifecycleDisposition.AlreadyApplied,
            (await ocrStore.SettleCompletionAsync(handle, miniTaskId, saved.ResultDigest, CancellationToken.None)).Disposition);
        await using var afterReplay = await factory.CreateDbContextAsync();
        var nextSlot = await afterReplay.GpuCapacitySlots.SingleAsync();
        Assert.Equal(nextHandle.BatchId, nextSlot.ActiveBatchId);
        Assert.Equal((int)GpuCapacitySlotState.Reserved, nextSlot.State);
    }

    private static DocumentOcrSupervisorIdentity SyntheticSupervisor() => new(Guid.NewGuid(),
        new(4242, DateTimeOffset.Parse("2026-01-01T00:00:00Z"), new string('a', 64)));
}
