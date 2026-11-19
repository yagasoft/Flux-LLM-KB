using System.Data.Common;
using FluxKnowledge.Application.Documents;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Sources;
using FluxKnowledge.Domain.Gpu;
using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using FluxKnowledge.Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Documents;

public sealed partial class SqlDocumentOcrHandoffTests
{
    [NativeSqlServerTheory]
    [InlineData("closed")]
    [InlineData("stale")]
    [InlineData("removal-pending")]
    [InlineData("paused")]
    public async Task Owned_OCR_result_survives_temporary_admission_loss_without_authorising_continuation(string mode)
    {
        var (factory, claim, revision, hash) = await CreateClaimedPdfAsync();
        var original = CreateStore(factory);
        var taskId = (await original.HandoffAsync(new(claim, revision, hash, [0]), default)).MiniTaskId!.Value;
        var handle = await AdmitDocumentOcrTaskAsync(factory);
        var lease = Assert.IsType<DocumentOcrExecutionLease>((await original.TryClaimExecutionAsync(handle, taskId,
            Guid.NewGuid(), SyntheticSupervisor(), default)).Lease);
        var admission = new SourceWorkAdmission();
        await using (var change = factory.CreateDbContext())
        {
            var root = await change.SourceRootConfigurations.SingleAsync();
            if (mode == "stale") admission.Admit(root.Id, root.ConfigurationRevision - 1);
            if (mode == "removal-pending")
            {
                admission.Admit(root.Id, root.ConfigurationRevision);
                change.SourcePendingChanges.Add(new SourcePendingChangeEntity { Id = Guid.NewGuid(), SourceRootId = root.Id,
                    RelativePath = ".", PathFingerprint = new string('b', 64), IsSubtree = true,
                    ReasonFlags = SourcePendingReasons.RemovalPending, Generation = 1 });
            }
            if (mode == "paused") { admission.Admit(root.Id, root.ConfigurationRevision); root.State = (int)SourceRootState.Paused; }
            await change.SaveChangesAsync();
        }
        var blocked = new SqlDocumentOcrStore(factory, scheduler: null, sourceAdmission: admission);
        Assert.False((await blocked.PermitNativeStartAsync(lease,
            new(12345, DateTimeOffset.UtcNow, lease.Supervisor.Process.MachineFingerprint), default)).Accepted);
        Assert.True((await original.ConfirmCleanupAsync(lease, new(lease.ExecutionInstanceId,
            DocumentOcrCleanupKind.VerifiedUnstarted, null, DateTimeOffset.UtcNow), default)).Accepted);
        var saved = await blocked.StoreResultAsync(lease, new(true, "document-ocr-complete",
            [new(0, 0, [new("text", 1, 2, 3, 4, "Admission-independent saved result.")])]), default);
        var restarted = new SqlDocumentOcrStore(factory, scheduler: null, sourceAdmission: admission);
        Assert.Equal(saved.ResultDigest, (await restarted.ReadPendingCompletionAsync(handle, default))!.ResultDigest);
        Assert.True((await restarted.SettleCompletionAsync(handle, taskId, saved.ResultDigest, default)).Accepted);
        Assert.False(await restarted.RequeueCompletedAsync(handle, taskId, default));
        await using (var held = factory.CreateDbContext())
        {
            Assert.Equal((int)DocumentOcrRequestState.ResultStored, (await held.DocumentOcrRequests.SingleAsync()).State);
            Assert.Equal((int)GpuMiniTaskExecutionState.Completed, (await held.GpuMiniTasks.SingleAsync()).ExecutionState);
            Assert.Single(await held.GpuExecutorResultReceipts.ToArrayAsync());
            Assert.Empty(await held.Artifacts.ToArrayAsync());
            Assert.Null((await held.GpuCapacitySlots.SingleAsync()).ActiveBatchId);
        }
        await using (var resume = factory.CreateDbContext())
        {
            (await resume.SourceRootConfigurations.SingleAsync()).State = (int)SourceRootState.Enabled;
            await resume.SourcePendingChanges.ExecuteDeleteAsync();
            await resume.SaveChangesAsync();
        }
        Assert.True(await CreateStore(factory).RequeueCompletedAsync(handle, taskId, default));
        await using var verify = factory.CreateDbContext();
        Assert.Single(await verify.DocumentOcrRequests.ToArrayAsync());
        Assert.Single(await verify.GpuMiniTasks.ToArrayAsync());
    }

    [NativeSqlServerTheory]
    [InlineData("closed")]
    [InlineData("stale")]
    public async Task Admission_loss_is_not_withdrawal_or_an_unpublishable_result(string mode)
    {
        var (factory, claim, revision, hash) = await CreateClaimedPdfAsync();
        var original = CreateStore(factory);
        var taskId = (await original.HandoffAsync(new(claim, revision, hash, [0]), default)).MiniTaskId!.Value;
        var handle = await AdmitDocumentOcrTaskAsync(factory);
        var lease = Assert.IsType<DocumentOcrExecutionLease>((await original.TryClaimExecutionAsync(handle, taskId,
            Guid.NewGuid(), SyntheticSupervisor(), default)).Lease);
        Assert.True((await original.ConfirmCleanupAsync(lease, new(lease.ExecutionInstanceId,
            DocumentOcrCleanupKind.VerifiedUnstarted, null, DateTimeOffset.UtcNow), default)).Accepted);
        var admission = new SourceWorkAdmission();
        if (mode == "stale")
        {
            await using var read = factory.CreateDbContext();
            var root = await read.SourceRootConfigurations.SingleAsync(); admission.Admit(root.Id, root.ConfigurationRevision - 1);
        }
        var blocked = new SqlDocumentOcrStore(factory, scheduler: null, sourceAdmission: admission);
        Assert.False((await blocked.ReleaseWithdrawnAsync(lease, default)).Accepted);
        Assert.False((await blocked.ReleaseUnpublishableAsync(lease, "document-ocr-result-persistence-failed", default)).Accepted);
        await using var verify = factory.CreateDbContext();
        Assert.Equal((int)GpuMiniTaskExecutionState.Active, (await verify.GpuMiniTasks.SingleAsync()).ExecutionState);
        Assert.Equal(handle.BatchId, (await verify.GpuCapacitySlots.SingleAsync()).ActiveBatchId);
        Assert.NotEqual((int)DocumentOcrExecutionPhase.Settled, (await verify.DocumentOcrRequests.SingleAsync()).ExecutionPhase);
    }
}
