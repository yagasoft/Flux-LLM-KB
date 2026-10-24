using FluxKnowledge.Application.Documents;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Domain.Gpu;
using FluxKnowledge.Infrastructure.SqlServer.Workers;
using FluxKnowledge.Integrations.Documents;
using FluxKnowledge.Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Documents;

public sealed partial class SqlDocumentOcrHandoffTests
{
    [NativeSqlServerFact]
    public async Task Recovery_preserves_live_supervisors_result_write_window_after_native_cleanup()
    {
        var (factory, claim, revision, hash) = await CreateClaimedPdfAsync();
        var store = CreateStore(factory);
        await store.HandoffAsync(new(claim, revision, hash, [0]), CancellationToken.None);
        var handle = await AdmitDocumentOcrTaskAsync(factory);
        var executor = new CleanupBeforeResultExecutor();
        var signal = new RecordingOutboxWakeSignal();
        await using var provider = CreateLocalExecutorProvider(factory, revision, hash, executor, signal,
            new TestHostApplicationLifetime(), new DocumentOcrProcessRecovery());
        var delivery = provider.GetRequiredService<PaddleOcrVlmExecutorAdapter>().DeliverAsync(handle, CancellationToken.None).AsTask();
        try
        {
            await executor.CleanupSaved.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await provider.GetRequiredService<PaddleOcrVlmCompletionCoordinator>().RecoverAsync(CancellationToken.None);
            Assert.Null(await store.ReadPendingCompletionAsync(handle, CancellationToken.None));
            Assert.Equal(0, signal.Notifications);
        }
        finally { executor.Release.TrySetResult(); await AwaitIgnoringFailureAsync(delivery); }
        await delivery;
        var result = Assert.IsType<DocumentOcrExecutionResult>(await store.ReadCompletedAsync(claim, hash, CancellationToken.None));
        Assert.True(result.Succeeded);
        Assert.Equal("original successful output", Assert.Single(Assert.Single(result.Pages).Blocks).Text);
        Assert.Equal(1, signal.Notifications);
    }

    [NativeSqlServerFact]
    public async Task No_result_release_cannot_terminalise_a_result_committed_since_the_callers_empty_read()
    {
        var (factory, claim, revision, hash) = await CreateClaimedPdfAsync();
        var store = CreateStore(factory);
        var task = Assert.IsType<Guid>((await store.HandoffAsync(new(claim, revision, hash, [0]), CancellationToken.None)).MiniTaskId);
        var handle = await AdmitDocumentOcrTaskAsync(factory);
        var lease = Assert.IsType<DocumentOcrExecutionLease>((await store.TryClaimExecutionAsync(handle, task,
            Guid.NewGuid(), SyntheticSupervisor(), CancellationToken.None)).Lease);
        await store.ConfirmCleanupAsync(lease, new(lease.ExecutionInstanceId,
            DocumentOcrCleanupKind.VerifiedUnstarted, null, DateTimeOffset.UtcNow), CancellationToken.None);
        Assert.Null(await store.ReadPendingCompletionAsync(handle, CancellationToken.None));
        var stored = await store.StoreResultAsync(lease, new(true, "document-ocr-complete",
            [new(0, 0, [new("text", 1, 2, 3, 4, "concurrent success")])]), CancellationToken.None);
        Assert.False((await store.ReleaseUnpublishableAsync(lease, "document-ocr-result-persistence-failed", CancellationToken.None)).Accepted);
        Assert.True((await store.SettleCompletionAsync(handle, task, stored.ResultDigest, CancellationToken.None)).Accepted);
        await using var verify = await factory.CreateDbContextAsync();
        Assert.Equal((int)GpuMiniTaskExecutionState.Completed, await verify.GpuMiniTasks.Select(t => t.ExecutionState).SingleAsync());
    }

    [NativeSqlServerFact]
    public async Task Corrupt_stored_result_is_preserved_without_publication_and_cannot_monopolise_proved_clean_capacity()
    {
        var (factory, claim, revision, hash) = await CreateClaimedPdfAsync();
        var store = CreateStore(factory);
        var task = Assert.IsType<Guid>((await store.HandoffAsync(new(claim, revision, hash, [0]), CancellationToken.None)).MiniTaskId);
        var handle = await AdmitDocumentOcrTaskAsync(factory);
        var lease = Assert.IsType<DocumentOcrExecutionLease>((await store.TryClaimExecutionAsync(handle, task,
            Guid.NewGuid(), SyntheticSupervisor(), CancellationToken.None)).Lease);
        var stored = await store.StoreResultAsync(lease, new(false, "document-ocr-runtime-timeout", []), CancellationToken.None);
        await store.ConfirmCleanupAsync(lease, new(lease.ExecutionInstanceId,
            DocumentOcrCleanupKind.VerifiedUnstarted, null, DateTimeOffset.UtcNow), CancellationToken.None);
        await using (var corrupt = await factory.CreateDbContextAsync())
        {
            (await corrupt.DocumentOcrRequests.SingleAsync()).ResultJson = "{corrupt";
            await corrupt.SaveChangesAsync();
        }
        var signal = new RecordingOutboxWakeSignal();
        await using var provider = CreateLocalExecutorProvider(factory, revision, hash,
            new BlockingCountingDocumentOcrExecutor(), signal, new TestHostApplicationLifetime());
        await provider.GetRequiredService<PaddleOcrVlmCompletionCoordinator>().CompleteAsync(handle, new(task, stored.ResultDigest), CancellationToken.None);
        await using var verify = await factory.CreateDbContextAsync();
        Assert.Equal((int)GpuCapacitySlotState.Available, await verify.GpuCapacitySlots.Select(s => s.State).SingleAsync());
        Assert.Equal((int)GpuMiniTaskExecutionState.OutcomeUncertain, await verify.GpuMiniTasks.Select(t => t.ExecutionState).SingleAsync());
        Assert.Equal("{corrupt", await verify.DocumentOcrRequests.Select(r => r.ResultJson).SingleAsync());
        Assert.Equal(0, signal.Notifications);
        Assert.Empty(await store.ReadRecoveryCandidatesAsync(CancellationToken.None));
    }

    private sealed class CleanupBeforeResultExecutor : IDocumentOcrExecutor
    {
        public TaskCompletionSource CleanupSaved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask<DocumentOcrExecutionResult> ExecuteAsync(RetainedSourceBytes retained,
            IReadOnlyList<int> pageIndexes, DocumentOcrExecutionContext execution, CancellationToken cancellationToken)
        {
            Assert.True((await execution.Observer.ConfirmCleanupAsync(execution.Lease,
                new(execution.Lease.ExecutionInstanceId, DocumentOcrCleanupKind.VerifiedUnstarted, null, DateTimeOffset.UtcNow), cancellationToken)).Accepted);
            CleanupSaved.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            return new(true, "document-ocr-complete", [new(0, 0, [new("text", 1, 2, 3, 4, "original successful output")])]);
        }
    }
}
