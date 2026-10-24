using FluxKnowledge.Application.Documents;
using FluxKnowledge.Application.Gpu;
using FluxKnowledge.Domain.Gpu;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Integration.Tests.Support;
using FluxKnowledge.Infrastructure.SqlServer.Workers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Documents;

public sealed partial class SqlDocumentOcrHandoffTests
{
    [NativeSqlServerFact]
    public async Task OCR_claim_is_atomic_unique_and_idempotent_replay_does_not_grant_a_new_launch()
    {
        var (factory, claim, revision, hash) = await CreateClaimedPdfAsync();
        var store = CreateStore(factory);
        var task = Assert.IsType<Guid>((await store.HandoffAsync(new(claim, revision, hash, [0]), CancellationToken.None)).MiniTaskId);
        var handle = await AdmitDocumentOcrTaskAsync(factory);
        var supervisor = SyntheticSupervisor();
        var execution = Guid.NewGuid();
        var attempts = await Task.WhenAll(store.TryClaimExecutionAsync(handle, task, execution, supervisor, CancellationToken.None).AsTask(),
            store.TryClaimExecutionAsync(handle, task, Guid.NewGuid(), SyntheticSupervisor(), CancellationToken.None).AsTask());
        Assert.Single(attempts, a => a.Disposition == DocumentOcrLifecycleDisposition.Applied);
        Assert.Single(attempts, a => a.Disposition == DocumentOcrLifecycleDisposition.StaleOwner);
        var lease = Assert.IsType<DocumentOcrExecutionLease>(attempts.Single(a => a.Accepted).Lease);
        var replay = await store.TryClaimExecutionAsync(handle, task, lease.ExecutionInstanceId, lease.Supervisor, CancellationToken.None);
        Assert.Equal(DocumentOcrLifecycleDisposition.AlreadyApplied, replay.Disposition);
        await using var verify = await factory.CreateDbContextAsync();
        Assert.Equal((int)GpuExecutorDispatchState.Acknowledged, await verify.GpuExecutorDispatches.Select(d => d.State).SingleAsync());
        Assert.Equal(lease.ExecutionInstanceId, await verify.DocumentOcrRequests.Select(r => r.ExecutionInstanceId).SingleAsync());
    }

    [NativeSqlServerFact]
    public async Task Healthy_fourteen_minute_OCR_heartbeat_preserves_active_state_and_does_not_extend_absolute_deadline()
    {
        var (factory, claim, revision, hash) = await CreateClaimedPdfAsync();
        var clock = new OcrClock(DateTimeOffset.UtcNow);
        var store = new SqlDocumentOcrStore(factory, CreateCoordinator(factory), clock);
        var task = Assert.IsType<Guid>((await store.HandoffAsync(new(claim, revision, hash, [0]), CancellationToken.None)).MiniTaskId);
        var handle = await AdmitDocumentOcrTaskAsync(factory);
        var lease = Assert.IsType<DocumentOcrExecutionLease>((await store.TryClaimExecutionAsync(handle, task,
            Guid.NewGuid(), SyntheticSupervisor(), CancellationToken.None)).Lease);
        clock.Now = clock.Now.AddMinutes(14);
        Assert.True((await store.HeartbeatAsync(lease, CancellationToken.None)).Accepted);
        Assert.Empty(await new SqlGpuSchedulerStore(factory).ReadStaleCapacityReservationsAsync(clock.Now.AddMinutes(-10), CancellationToken.None));
        await using var verify = await factory.CreateDbContextAsync();
        Assert.Equal((int)GpuBatchState.Active, await verify.GpuBatches.Select(b => b.State).SingleAsync());
        Assert.Equal(lease.DeadlineAtUtc, await verify.DocumentOcrRequests.Select(r => r.ExecutionDeadlineAtUtc).SingleAsync());
        clock.Now = lease.DeadlineAtUtc;
        Assert.False((await store.HeartbeatAsync(lease, CancellationToken.None)).Accepted);
    }

    [NativeSqlServerFact]
    public async Task Native_start_and_unstarted_cleanup_are_mutually_exclusive_under_concurrent_requests()
    {
        var (factory, claim, revision, hash) = await CreateClaimedPdfAsync();
        var store = CreateStore(factory);
        var task = Assert.IsType<Guid>((await store.HandoffAsync(new(claim, revision, hash, [0]), CancellationToken.None)).MiniTaskId);
        var handle = await AdmitDocumentOcrTaskAsync(factory);
        var lease = Assert.IsType<DocumentOcrExecutionLease>((await store.TryClaimExecutionAsync(handle, task,
            Guid.NewGuid(), SyntheticSupervisor(), CancellationToken.None)).Lease);
        var process = new DocumentOcrProcessIdentity(1234, DateTimeOffset.UtcNow, lease.Supervisor.Process.MachineFingerprint);
        var results = await Task.WhenAll(store.PermitNativeStartAsync(lease, process, CancellationToken.None).AsTask(),
            store.ConfirmCleanupAsync(lease, new(lease.ExecutionInstanceId, DocumentOcrCleanupKind.VerifiedUnstarted,
                null, DateTimeOffset.UtcNow), CancellationToken.None).AsTask());
        Assert.Single(results, r => r.Accepted);
        await using var verify = await factory.CreateDbContextAsync();
        Assert.Equal((int)GpuCapacitySlotState.Reserved, await verify.GpuCapacitySlots.Select(s => s.State).SingleAsync());
        var request = await verify.DocumentOcrRequests.SingleAsync();
        Assert.True(request.ExecutionPhase == (int)DocumentOcrExecutionPhase.NativeStartPermitted && request.NativeCleanupConfirmedAtUtc is null ||
            request.ExecutionPhase == (int)DocumentOcrExecutionPhase.CleanupConfirmed && request.NativeProcessId is null);
    }

    [NativeSqlServerFact]
    public async Task Generic_capacity_evidence_does_not_authorise_OCR_reconciliation_without_bound_cleanup()
    {
        var (factory, claim, revision, hash) = await CreateClaimedPdfAsync();
        var store = CreateStore(factory);
        var task = Assert.IsType<Guid>((await store.HandoffAsync(new(claim, revision, hash, [0]), CancellationToken.None)).MiniTaskId);
        var handle = await AdmitDocumentOcrTaskAsync(factory);
        var scheduler = new SqlGpuSchedulerStore(factory);
        Assert.True((await scheduler.AcknowledgeAsync(new(Guid.NewGuid(), handle), CancellationToken.None)).Accepted);
        var reservation = Assert.Single(await scheduler.ReadStaleCapacityReservationsAsync(DateTimeOffset.UtcNow.AddDays(1), CancellationToken.None));
        Assert.True((await scheduler.MarkCapacityUncertainAsync(Guid.NewGuid(), reservation, CancellationToken.None)).Committed);
        var evidence = Guid.NewGuid();
        Assert.True((await scheduler.RecordTrustedEvidenceAsync(new(evidence, handle, "synthetic-driver-absence",
            DateTimeOffset.UtcNow, GpuExecutorEvidenceClass.CapacityReleaseConfirmed), CancellationToken.None)).Accepted);
        Assert.False((await scheduler.ReconcileCapacityAsync(Guid.NewGuid(), new(handle, evidence), CancellationToken.None)).Committed);
        await using var verify = await factory.CreateDbContextAsync();
        Assert.Equal((int)GpuCapacitySlotState.Uncertain, await verify.GpuCapacitySlots.Select(s => s.State).SingleAsync());
    }

    private sealed class OcrClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    [NativeSqlServerFact]
    public async Task Lost_claim_cleanup_and_settlement_responses_are_read_back_without_second_execution_or_continuation()
    {
        var (factory, claim, revision, hash) = await CreateClaimedPdfAsync();
        await CreateStore(factory).HandoffAsync(new(claim, revision, hash, [0]), CancellationToken.None);
        var handle = await AdmitDocumentOcrTaskAsync(factory);
        var lost = new HashSet<string>(StringComparer.Ordinal);
        var signal = new RecordingOutboxWakeSignal();
        var executor = new RecordingDocumentOcrExecutor(new(true, "document-ocr-complete",
            [new(0, 0, [new("text", 1, 2, 3, 4, "result after lost responses")])]));
        await using var provider = CreateLocalExecutorProvider(factory, revision, hash, executor, signal,
            new TestHostApplicationLifetime(), storeFactory: () => new SqlDocumentOcrStore(factory,
                CreateCoordinator(factory), afterLifecycleCommitted: (result, _) =>
                {
                    if (result.Disposition == DocumentOcrLifecycleDisposition.Applied && result.ReasonCode is
                        "document-ocr-claimed" or "document-ocr-cleanup-confirmed" or "document-ocr-settled" && lost.Add(result.ReasonCode))
                        throw new TimeoutException("synthetic commit response loss");
                    return ValueTask.CompletedTask;
                }));
        await provider.GetRequiredService<PaddleOcrVlmExecutorAdapter>().DeliverAsync(handle, CancellationToken.None);
        await provider.GetRequiredService<PaddleOcrVlmCompletionCoordinator>().RecoverAsync(CancellationToken.None);
        Assert.Equal(3, lost.Count);
        Assert.Single(executor.Calls);
        Assert.Equal(1, signal.Notifications);
        Assert.True(Assert.IsType<DocumentOcrExecutionResult>(await CreateStore(factory).ReadCompletedAsync(claim, hash, CancellationToken.None)).Succeeded);
    }

    [NativeSqlServerFact]
    public async Task Cleanup_proof_and_result_writes_reject_stale_identity_and_maintenance_hash_without_authority()
    {
        var (factory, claim, revision, hash) = await CreateClaimedPdfAsync();
        var store = CreateStore(factory);
        var task = Assert.IsType<Guid>((await store.HandoffAsync(new(claim, revision, hash, [0]), CancellationToken.None)).MiniTaskId);
        var handle = await AdmitDocumentOcrTaskAsync(factory);
        var lease = Assert.IsType<DocumentOcrExecutionLease>((await store.TryClaimExecutionAsync(handle, task,
            Guid.NewGuid(), SyntheticSupervisor(), CancellationToken.None)).Lease);
        var child = new DocumentOcrProcessIdentity(1234, DateTimeOffset.UtcNow, lease.Supervisor.Process.MachineFingerprint);
        Assert.True((await store.PermitNativeStartAsync(lease, child, CancellationToken.None)).Accepted);
        foreach (var stale in new[] { lease with { ExecutionInstanceId = Guid.NewGuid() },
            lease with { Supervisor = lease.Supervisor with { InstanceId = Guid.NewGuid() } },
            lease with { Handle = handle with { AdmissionGeneration = handle.AdmissionGeneration + 1 } } })
        {
            Assert.False((await store.ConfirmCleanupAsync(stale, new(stale.ExecutionInstanceId,
                DocumentOcrCleanupKind.OwnedJobEmpty, child, DateTimeOffset.UtcNow), CancellationToken.None)).Accepted);
            await Assert.ThrowsAsync<InvalidOperationException>(() => store.StoreResultAsync(stale,
                new(false, "document-ocr-owner-interrupted", []), CancellationToken.None).AsTask());
        }
        Assert.False((await store.ConfirmCleanupAsync(lease, new(lease.ExecutionInstanceId,
            DocumentOcrCleanupKind.OwnedJobEmpty, child with { ProcessId = 5678 }, DateTimeOffset.UtcNow), CancellationToken.None)).Accepted);
        Assert.False((await store.ConfirmCleanupAsync(lease, new(lease.ExecutionInstanceId,
            DocumentOcrCleanupKind.StoppedMaintenance, null, DateTimeOffset.UtcNow, new string('a', 64)), CancellationToken.None)).Accepted);
        Assert.True((await store.ConfirmCleanupAsync(lease, new(lease.ExecutionInstanceId,
            DocumentOcrCleanupKind.OwnedJobEmpty, child, DateTimeOffset.UtcNow), CancellationToken.None)).Accepted);
    }

    [NativeSqlServerFact]
    public async Task Recovery_budget_starts_once_and_unchanged_blocked_reason_does_not_write_each_poll()
    {
        var (factory, claim, revision, hash) = await CreateClaimedPdfAsync();
        var clock = new OcrClock(DateTimeOffset.UtcNow);
        var store = new SqlDocumentOcrStore(factory, CreateCoordinator(factory), clock);
        var task = Assert.IsType<Guid>((await store.HandoffAsync(new(claim, revision, hash, [0]), CancellationToken.None)).MiniTaskId);
        var handle = await AdmitDocumentOcrTaskAsync(factory);
        await store.RecordBlockedAsync(handle, task, "document-ocr-legacy-owner-unproven", CancellationToken.None);
        var started = clock.Now;
        clock.Now = clock.Now.AddMinutes(3);
        await store.RecordBlockedAsync(handle, task, "document-ocr-legacy-owner-unproven", CancellationToken.None);
        await using var firstRead = await factory.CreateDbContextAsync();
        var blocked = await firstRead.DocumentOcrRequests.AsNoTracking().SingleAsync();
        Assert.Equal(started, blocked.RecoveryStartedAtUtc);
        Assert.Equal(clock.Now, blocked.RecoveryUpdatedAtUtc);
        Assert.Equal((int)DocumentOcrExecutionPhase.Blocked, blocked.ExecutionPhase);
        clock.Now = clock.Now.AddMinutes(3);
        await store.RecordBlockedAsync(handle, task, "document-ocr-legacy-owner-unproven", CancellationToken.None);
        await using var secondRead = await factory.CreateDbContextAsync();
        Assert.Equal(blocked.RowVersion, await secondRead.DocumentOcrRequests.Select(r => r.RowVersion).SingleAsync());
    }

    [NativeSqlServerFact]
    public async Task Scheduler_status_identifies_legacy_capacity_owner_without_machine_process_or_private_output()
    {
        var (factory, claim, revision, hash) = await CreateClaimedPdfAsync();
        var store = CreateStore(factory);
        await store.HandoffAsync(new(claim, revision, hash, [0]), CancellationToken.None);
        var handle = await AdmitDocumentOcrTaskAsync(factory);
        var scheduler = new SqlGpuSchedulerStore(factory);
        var reservation = Assert.Single(await scheduler.ReadStaleCapacityReservationsAsync(DateTimeOffset.UtcNow.AddDays(1), CancellationToken.None));
        await scheduler.MarkCapacityUncertainAsync(Guid.NewGuid(), reservation, CancellationToken.None);
        var status = await scheduler.ReadGpuSchedulerStatusAsync(CancellationToken.None);
        var owner = Assert.IsType<FluxKnowledge.Application.Contracts.GpuOcrOwnerStatus>(status.OcrOwner);
        Assert.Equal(handle.BatchId, owner.BatchId);
        Assert.Null(owner.ExecutionInstanceId);
        Assert.Equal("Blocked", owner.State);
        Assert.Equal("document-ocr-legacy-owner-unproven", owner.ReasonCode);
        Assert.Contains("Operator verification", owner.NextAction, StringComparison.Ordinal);
    }
}
