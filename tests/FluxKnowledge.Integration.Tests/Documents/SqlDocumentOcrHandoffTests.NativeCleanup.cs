using System.Diagnostics;
using FluxKnowledge.Application.Documents;
using FluxKnowledge.Application.Gpu;
using FluxKnowledge.Domain.Gpu;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Integrations.Documents;
using FluxKnowledge.Integrations.Windows;
using FluxKnowledge.Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Documents;

public sealed partial class SqlDocumentOcrHandoffTests
{
    [NativeSqlServerTheory]
    [InlineData("none")]
    [InlineData("transient")]
    [InlineData("lost-response")]
    [InlineData("stale-proof")]
    public async Task Native_gated_child_records_cleanup_through_the_real_SQL_observer(string fault)
    {
        var (factory, claim, revision, hash) = await CreateClaimedPdfAsync();
        var store = CreateStore(factory);
        var task = Assert.IsType<Guid>((await store.HandoffAsync(new(claim, revision, hash, [0]), default)).MiniTaskId);
        var handle = await AdmitDocumentOcrTaskAsync(factory);
        var supervisor = new DocumentOcrSupervisorIdentity(Guid.NewGuid(), new WindowsInteractiveGpuOwnerProbe().Current);
        var lease = Assert.IsType<DocumentOcrExecutionLease>((await store.TryClaimExecutionAsync(handle, task,
            Guid.NewGuid(), supervisor, default)).Lease);
        var observer = new NativeSqlOcrObserver(store, fault);
        var outcome = await OwnedOcrProcessSupervisor.RunAsync(NativeCleanupChild(), new(lease, observer), default);
        Assert.Equal(0, outcome.ExitCode);
        await using var db = await factory.CreateDbContextAsync();
        var request = await db.DocumentOcrRequests.SingleAsync();
        if (fault == "stale-proof")
        {
            Assert.False(outcome.CleanupConfirmed);
            Assert.Null(request.NativeCleanupConfirmedAtUtc);
            Assert.Equal((int)GpuMiniTaskExecutionState.Active, (await db.GpuMiniTasks.SingleAsync()).ExecutionState);
            Assert.Equal((int)GpuCapacitySlotState.Reserved, (await db.GpuCapacitySlots.SingleAsync()).State);
        }
        else
        {
            Assert.True(outcome.CleanupConfirmed, string.Join(",", observer.Reasons));
            Assert.NotNull(request.NativeCleanupConfirmedAtUtc);
            Assert.Equal((int)DocumentOcrCleanupKind.OwnedJobEmpty, request.CleanupProofKind);
            var result = await store.StoreResultAsync(lease, new(true, "document-ocr-complete",
                [new(0, 0, [new("text", 1, 2, 3, 4, "Native cleanup SQL integration.")])]), default);
            Assert.True((await store.SettleCompletionAsync(handle, task, result.ResultDigest, default)).Accepted);
        }
        Assert.Null(OwnedOcrJob.TryOpen(lease.ExecutionInstanceId));
    }

    [NativeSqlServerFact]
    public async Task Maintenance_admission_refusal_releases_publication_fence_for_native_OCR_cleanup_and_same_operation_retry()
    {
        var (factory, claim, revision, hash) = await CreateClaimedPdfAsync();
        var ocr = CreateStore(factory);
        var task = Assert.IsType<Guid>((await ocr.HandoffAsync(new(claim, revision, hash, [0]), default)).MiniTaskId);
        var handle = await AdmitDocumentOcrTaskAsync(factory);
        var supervisor = new DocumentOcrSupervisorIdentity(Guid.NewGuid(), new WindowsInteractiveGpuOwnerProbe().Current);
        var lease = Assert.IsType<DocumentOcrExecutionLease>((await ocr.TryClaimExecutionAsync(handle, task,
            Guid.NewGuid(), supervisor, default)).Lease);
        var scheduler = new SqlGpuSchedulerStore(factory, sourceAdmission: new FixtureSourceAdmission(factory));
        var operation = Guid.NewGuid();
        var options = new GpuSchedulerOptions(1, long.MaxValue, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(10));
        Task<GpuSchedulerAdmissionRoundResult>? attempt = null;
        await using (var drain = await SqlGpuMaintenanceDrainLease.AcquireAsync(factory, default))
        {
            try
            {
                attempt = scheduler.RunAdmissionRoundAsync(operation, GpuSchedulerWakeReason.WorkReady, options,
                    (_, _) => throw new InvalidOperationException("Maintenance must prevent admission decisions."), default).AsTask();
                await Assert.ThrowsAnyAsync<InvalidOperationException>(() => attempt.WaitAsync(TimeSpan.FromSeconds(1)));
                await using (var check = await factory.CreateDbContextAsync())
                {
                    Assert.Empty(await check.GpuSchedulerOperationReceipts.Where(value => value.OperationId == operation).ToArrayAsync());
                    Assert.Equal(handle.BatchId, (await check.GpuCapacitySlots.SingleAsync()).ActiveBatchId);
                }
                var outcome = await OwnedOcrProcessSupervisor.RunAsync(NativeCleanupChild(), new(lease, ocr), default)
                    .AsTask().WaitAsync(TimeSpan.FromSeconds(5));
                Assert.True(outcome.CleanupConfirmed);
                using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                var result = await ocr.StoreResultAsync(lease, new(true, "document-ocr-complete",
                    [new(0, 0, [new("text", 1, 2, 3, 4, "Cleanup while admission is fenced.")])]), budget.Token);
                Assert.True((await ocr.SettleCompletionAsync(handle, task, result.ResultDigest, budget.Token)).Accepted);
                Assert.True(await drain.IsDrainedAsync(handle.CapacitySlotKey, default));
            }
            finally
            {
                await drain.DisposeAsync();
                if (attempt is not null) try { await attempt; } catch (InvalidOperationException) { }
            }
        }
        var recovered = await scheduler.RunAdmissionRoundAsync(operation, GpuSchedulerWakeReason.WorkReady, options,
            (_, _) => throw new InvalidOperationException("Completed work must not be readmitted."), default);
        Assert.Equal(GpuAdmissionDisposition.Busy, recovered.Disposition);
        var replay = await scheduler.RunAdmissionRoundAsync(operation, GpuSchedulerWakeReason.WorkReady, options,
            (_, _) => throw new InvalidOperationException("A committed operation must replay."), default);
        Assert.True(replay.IsIdempotentReplay);
        await using var verify = await factory.CreateDbContextAsync();
        Assert.Single(await verify.GpuSchedulerOperationReceipts.Where(value => value.OperationId == operation).ToArrayAsync());
        Assert.Single(await verify.GpuBatches.ToArrayAsync());
    }

    private static ProcessStartInfo NativeCleanupChild()
    {
        var start = new ProcessStartInfo("powershell.exe");
        start.ArgumentList.Add("-NoProfile"); start.ArgumentList.Add("-NonInteractive"); start.ArgumentList.Add("-Command");
        start.ArgumentList.Add("if ([Console]::ReadLine() -ne 'fluxknowledge-ocr-go-v1') { exit 42 }; exit 0");
        return start;
    }

    private sealed class NativeSqlOcrObserver(SqlDocumentOcrStore store, string fault) : IDocumentOcrExecutionObserver
    {
        private int calls;
        internal List<string> Reasons { get; } = [];
        public ValueTask<DocumentOcrLifecycleResult> PermitNativeStartAsync(DocumentOcrExecutionLease lease,
            DocumentOcrProcessIdentity process, CancellationToken ct) => store.PermitNativeStartAsync(lease, process, ct);
        public async ValueTask<DocumentOcrLifecycleResult> ConfirmCleanupAsync(DocumentOcrExecutionLease lease,
            DocumentOcrCleanupProof proof, CancellationToken ct)
        {
            var first = Interlocked.Increment(ref calls) == 1;
            if (fault == "transient" && first) throw new IOException("Synthetic proof write interruption.");
            var bound = fault == "stale-proof" ? lease with { ExecutionInstanceId = Guid.NewGuid() } : lease;
            var result = await store.ConfirmCleanupAsync(bound, proof, ct);
            Reasons.Add(result.ReasonCode);
            if (fault == "lost-response" && first) throw new IOException("Synthetic proof commit acknowledgement loss.");
            return result;
        }
    }
}
