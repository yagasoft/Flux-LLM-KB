using System.Data.Common;
using FluxKnowledge.Application.Documents;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Sources;
using FluxKnowledge.Domain.Gpu;
using FluxKnowledge.Domain.Jobs;
using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Integrations.Documents;
using FluxKnowledge.Integrations.Windows;
using FluxKnowledge.Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Documents;

public sealed partial class SqlDocumentOcrHandoffTests
{
    [NativeSqlServerTheory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Settled_no_result_recovery_creates_one_successor_and_fences_both_old_generations(bool expiredLeases, bool pdf)
    {
        var seed = await CreateSettledNoResultImageAsync(pdf);
        var descriptor = pdf ? PdfDocumentProcessor.Capability : ImageDocumentInputProcessor.Capability;
        var request = new DocumentReprocessRequest(new(seed.ParentId), seed.Hash, descriptor.ProcessorFingerprint, seed.FailedJobId, 1);
        long jobGeneration, deliveryGeneration;
        await using (var before = seed.Factory.CreateDbContext())
        {
            var job = await before.Jobs.SingleAsync(value => value.Id == seed.FailedJobId);
            var delivery = await before.OutboxMessages.SingleAsync(value => value.JobId == job.Id);
            jobGeneration = job.LeaseGeneration; deliveryGeneration = delivery.LeaseGeneration;
            if (expiredLeases)
            {
                job.LeaseOwner = "expired-job"; job.LeaseExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1);
                delivery.LeaseOwner = "expired-delivery"; delivery.LeaseExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1);
                await before.SaveChangesAsync();
            }
        }
        var store = new SqlRetainedProcessorBranchStore(seed.Factory, TimeProvider.System, sourceAdmission: new FixtureSourceAdmission(seed.Factory));
        var created = await store.RequestDocumentReprocessAsync(request, default);
        Assert.True(created.Created);
        var replay = await store.RequestDocumentReprocessAsync(request, default);
        Assert.True(replay.WasReplay); Assert.Equal(created.SuccessorBranchId, replay.SuccessorBranchId);
        await using (var terminal = seed.Factory.CreateDbContext())
        {
            var job = await terminal.Jobs.SingleAsync(value => value.Id == seed.FailedJobId);
            var delivery = await terminal.OutboxMessages.SingleAsync(value => value.JobId == job.Id);
            Assert.Equal((int)PublicJobState.Failed, job.PublicState);
            Assert.Equal("document-ocr-settled-without-result", job.Reason);
            Assert.Equal(jobGeneration + 1, job.LeaseGeneration); Assert.Equal(deliveryGeneration + 1, delivery.LeaseGeneration);
            Assert.Null(job.LeaseOwner); Assert.Null(job.LeaseExpiresAtUtc); Assert.Null(delivery.LeaseOwner); Assert.Null(delivery.LeaseExpiresAtUtc);
            Assert.NotNull(delivery.DispatchedAtUtc);
            Assert.Equal((int)SourceActivityState.FailedTerminal, (await terminal.SourceActivities.SingleAsync(value => value.Id == seed.FailedChildActivityId)).State);
            Assert.Equal((int)GpuMiniTaskExecutionState.OutcomeUncertain, (await terminal.GpuMiniTasks.SingleAsync()).ExecutionState);
            var old = await terminal.DocumentOcrRequests.SingleAsync();
            Assert.Null(old.ResultJson); Assert.Null(old.ResultDigest); Assert.Equal((int)DocumentOcrExecutionPhase.Settled, old.ExecutionPhase);
            Assert.Equal((int)DocumentOcrCleanupKind.OwnedJobEmpty, old.CleanupProofKind);
            Assert.Empty(await terminal.GpuExecutorResultReceipts.ToArrayAsync());
        }
        var claim = await store.ClaimDocumentBranchAsync(created.SuccessorBranchId!.Value, new(seed.ParentId), seed.Hash,
            descriptor.ProcessorFingerprint, "no-result-successor", default);
        Assert.NotNull(claim);
        var input = new FluxKnowledge.Application.Ports.RetainedSourceBytes(new(seed.ParentId), seed.Bytes, seed.Hash, seed.Bytes.Length);
        var completion = pdf
            ? await new PdfDocumentProcessor(new SqlRetainedArtifactWriter(seed.Factory, seed.ArtifactRoot)).ProcessAsync(claim, input, new(), default)
            : await new ImageDocumentInputProcessor().ProcessAsync(claim, input, new(), default);
        Assert.True(await store.CommitAsync(claim, completion, default));
        Assert.Equal(1, await new SqlRetainedTextRegistrationStore(seed.Factory, TimeProvider.System, seed.ArtifactRoot,
            sourceAdmission: new FixtureSourceAdmission(seed.Factory)).OfferUnlinkedInProcessActivitiesAsync(default));
        Assert.False(await CreateStore(seed.Factory).RequeueCompletedAsync(seed.Handle!, seed.MiniTaskId, default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => CreateStore(seed.Factory).StoreResultAsync(seed.Handle!, seed.MiniTaskId,
            new(true, "document-ocr-complete", [new(0, 0, [])]), default).AsTask());
        await using var verify = seed.Factory.CreateDbContext();
        var child = await verify.SourceProcessorBranchMembers.SingleAsync(value => value.BranchId == created.SuccessorBranchId);
        Assert.NotEqual(seed.ChildId, child.ChildSourceRevisionId);
        var newRecord = await verify.PipelineRecords.SingleAsync(value => value.SourceRevisionId == child.ChildSourceRevisionId);
        Assert.NotEqual(seed.RecordId, newRecord.Id);
        var next = await new SqlJobClaimStore(seed.Factory, sourceAdmission: new FixtureSourceAdmission(seed.Factory)).ClaimNextDueAsync(
            "new-no-result-extract", DateTimeOffset.UtcNow, TimeSpan.FromMinutes(5), default);
        Assert.NotNull(next); Assert.NotEqual(seed.FailedJobId, next.JobId.Value);
        var handoff = await CreateStore(seed.Factory).HandoffAsync(new(next, new(child.ChildSourceRevisionId!.Value), seed.Hash, [0]), default);
        Assert.NotEqual(seed.MiniTaskId, handoff.MiniTaskId);
        Assert.Equal(2, await verify.DocumentOcrRequests.CountAsync());
        Assert.Equal(2, await verify.GpuMiniTasks.CountAsync());
        Assert.Equal(2, await verify.SourceActivityRelations.CountAsync());
    }

    [NativeSqlServerTheory]
    [InlineData("live-job")]
    [InlineData("live-delivery")]
    [InlineData("cleanup")]
    [InlineData("result")]
    [InlineData("capacity")]
    [InlineData("binding")]
    [InlineData("competing-work")]
    [InlineData("admission")]
    public async Task Settled_no_result_recovery_refuses_changed_or_unproved_ownership(string change)
    {
        var seed = await CreateSettledNoResultImageAsync();
        await using (var mutate = seed.Factory.CreateDbContext())
        {
            var request = await mutate.DocumentOcrRequests.SingleAsync();
            if (change == "live-job") { var job = await mutate.Jobs.SingleAsync(value => value.Id == seed.FailedJobId); job.LeaseOwner="live"; job.LeaseExpiresAtUtc=DateTimeOffset.UtcNow.AddMinutes(5); }
            if (change == "live-delivery") { var delivery = await mutate.OutboxMessages.SingleAsync(); delivery.LeaseOwner="live"; delivery.LeaseExpiresAtUtc=DateTimeOffset.UtcNow.AddMinutes(5); }
            if (change == "cleanup") { request.NativeCleanupConfirmedAtUtc=null; request.CleanupProofKind=null; }
            if (change == "result") { request.ResultJson="{}"; request.ResultDigest=new byte[32]; request.State=(int)DocumentOcrRequestState.ResultStored; }
            if (change == "binding") request.ExecutionAdmissionGeneration++;
            if (change == "capacity") { var slot=await mutate.GpuCapacitySlots.SingleAsync(); slot.ActiveBatchId=seed.Handle!.BatchId; slot.OwnerKey=(await mutate.GpuBatches.SingleAsync()).OwnerKey; slot.State=(int)GpuCapacitySlotState.Reserved; }
            if (change == "competing-work") mutate.Jobs.Add(new() { Id=Guid.NewGuid(),PipelineRecordId=seed.RecordId,SourceRevision=1,Stage=2,Operation="normalise text",PublicState=(int)PublicJobState.Failed,DueAtUtc=DateTimeOffset.UtcNow });
            await mutate.SaveChangesAsync();
        }
        var store = new SqlRetainedProcessorBranchStore(seed.Factory, TimeProvider.System,
            sourceAdmission: change == "admission" ? SourceWorkAdmission.Closed : new FixtureSourceAdmission(seed.Factory));
        Assert.False((await store.RequestDocumentReprocessAsync(ImageRecoveryRequest(seed), default)).Accepted);
        await using var verify = seed.Factory.CreateDbContext();
        Assert.Equal((int)PublicJobState.GpuProcessing, (await verify.Jobs.SingleAsync(value => value.Id == seed.FailedJobId)).PublicState);
        Assert.Single(await verify.SourceProcessorBranches.ToArrayAsync());
        Assert.Empty(await verify.SourceActivityRelations.ToArrayAsync());
    }

    [NativeSqlServerTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Settled_no_result_recovery_reconciles_commit_loss_and_rolls_back_a_failed_commit(bool loseAck)
    {
        var seed = await CreateSettledNoResultImageAsync();
        var fault = new NoResultCommitFault(loseAck);
        var factory = new OcrOutboxFaultFactory(new DbContextOptionsBuilder<FluxKnowledgeDbContext>().UseSqlServer(_fixture.ConnectionString).AddInterceptors(fault).Options);
        var store = new SqlRetainedProcessorBranchStore(factory, TimeProvider.System, sourceAdmission: new FixtureSourceAdmission(factory));
        await Assert.ThrowsAsync<IOException>(() => store.RequestDocumentReprocessAsync(ImageRecoveryRequest(seed), default).AsTask());
        Assert.True(fault.Fired);
        await using var verify = seed.Factory.CreateDbContext();
        Assert.Equal(loseAck ? 2 : 1, await verify.SourceProcessorBranches.CountAsync());
        Assert.Equal(loseAck ? 1 : 0, await verify.SourceActivityRelations.CountAsync());
        Assert.Equal((int)(loseAck ? PublicJobState.Failed : PublicJobState.GpuProcessing), (await verify.Jobs.SingleAsync()).PublicState);
        if (loseAck)
        {
            var ordinary = new SqlRetainedProcessorBranchStore(seed.Factory, TimeProvider.System, sourceAdmission: new FixtureSourceAdmission(seed.Factory));
            Assert.True((await ordinary.RequestDocumentReprocessAsync(ImageRecoveryRequest(seed), default)).WasReplay);
            Assert.Equal(2, await verify.SourceProcessorBranches.CountAsync());
        }
    }

    [NativeSqlServerTheory]
    [InlineData("result")]
    [InlineData("binding")]
    [InlineData("generation")]
    public async Task Settled_no_result_successor_refuses_a_late_result_or_changed_native_binding(string change)
    {
        var seed = await CreateSettledNoResultImageAsync();
        var store = new SqlRetainedProcessorBranchStore(seed.Factory, TimeProvider.System, sourceAdmission: new FixtureSourceAdmission(seed.Factory));
        var created=await store.RequestDocumentReprocessAsync(ImageRecoveryRequest(seed), default);
        Assert.True(created.Created);
        var claim=await store.ClaimDocumentBranchAsync(created.SuccessorBranchId!.Value, new(seed.ParentId), seed.Hash, ImageDocumentInputProcessor.Capability.ProcessorFingerprint,"late-no-result",default);
        Assert.NotNull(claim);
        var completion=await new ImageDocumentInputProcessor().ProcessAsync(claim,new(new(seed.ParentId),seed.Bytes,seed.Hash,seed.Bytes.Length),new(),default);
        await using (var changeContext=seed.Factory.CreateDbContext())
        {
            var request=await changeContext.DocumentOcrRequests.SingleAsync();
            if(change=="result"){request.ResultJson="{}";request.ResultDigest=new byte[32];request.State=(int)DocumentOcrRequestState.ResultStored;}
            else if(change=="binding") request.ExecutionAdmissionGeneration++;
            else (await changeContext.Jobs.SingleAsync(value=>value.Id==seed.FailedJobId)).LeaseGeneration++;
            await changeContext.SaveChangesAsync();
        }
        Assert.False(await store.CommitAsync(claim,completion,default));
        await using var verify=seed.Factory.CreateDbContext();
        Assert.Single(await verify.PipelineRecords.ToArrayAsync());
        Assert.Single(await verify.GpuMiniTasks.ToArrayAsync());
    }

    private async Task<ImageFailureSeed> CreateSettledNoResultImageAsync(bool pdf = false)
    {
        var seed = await CreateSettledImageFailureAsync(pdf: pdf, native: false);
        await using (var reset=seed.Factory.CreateDbContext())
        {
            var job=await reset.Jobs.SingleAsync(); job.PublicState=(int)PublicJobState.WorkerQueued;job.Reason=null;job.DueAtUtc=DateTimeOffset.UtcNow;
            var activity=await reset.SourceActivities.SingleAsync(value=>value.Id==seed.FailedChildActivityId);activity.State=(int)SourceActivityState.Pending;activity.Reason=null;
            (await reset.OutboxMessages.SingleAsync()).DispatchedAtUtc=null;
            await reset.SaveChangesAsync();
        }
        var claim=await new SqlJobClaimStore(seed.Factory,sourceAdmission:new FixtureSourceAdmission(seed.Factory)).ClaimNextDueAsync("no-result-fixture",DateTimeOffset.UtcNow,TimeSpan.FromMinutes(5),default);
        Assert.NotNull(claim);
        var ocr=CreateStore(seed.Factory);
        var taskId=(await ocr.HandoffAsync(new(claim,new(seed.ChildId),seed.Hash,[0]),default)).MiniTaskId!.Value;
        var handle=await AdmitDocumentOcrTaskAsync(seed.Factory);
        var supervisor=new DocumentOcrSupervisorIdentity(Guid.NewGuid(),new WindowsInteractiveGpuOwnerProbe().Current);
        var lease=Assert.IsType<DocumentOcrExecutionLease>((await ocr.TryClaimExecutionAsync(handle,taskId,Guid.NewGuid(),supervisor,default)).Lease);
        Assert.True((await OwnedOcrProcessSupervisor.RunAsync(NativeCleanupChild(),new(lease,ocr),default)).CleanupConfirmed);
        await using(var withdraw=seed.Factory.CreateDbContext()){(await withdraw.SourceRevisions.SingleAsync(value=>value.Id==seed.ChildId)).SuppressedAtUtc=DateTimeOffset.UtcNow;await withdraw.SaveChangesAsync();}
        Assert.True((await ocr.ReleaseWithdrawnAsync(lease,default)).Accepted);
        await using(var restore=seed.Factory.CreateDbContext()){(await restore.SourceRevisions.SingleAsync(value=>value.Id==seed.ChildId)).SuppressedAtUtc=null;await restore.SaveChangesAsync();}
        return seed with { MiniTaskId=taskId,DispatchId=handle.DispatchId,Handle=handle };
    }

    private sealed class NoResultCommitFault(bool loseAck) : DbTransactionInterceptor
    {
        public bool Fired { get; private set; }
        private bool IsRecovery(DbContext? db)=>db is FluxKnowledgeDbContext context&&context.ChangeTracker.Entries<FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities.JobEntity>().Any(value=>value.Entity.Reason=="document-ocr-settled-without-result");
        public override ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction,TransactionEventData eventData,InterceptionResult result,CancellationToken ct=default)
        {if(!loseAck&&!Fired&&IsRecovery(eventData.Context)){Fired=true;throw new IOException("Synthetic no-result commit failure.");}return ValueTask.FromResult(result);}
        public override Task TransactionCommittedAsync(DbTransaction transaction,TransactionEndEventData eventData,CancellationToken ct=default)
        {if(loseAck&&!Fired&&IsRecovery(eventData.Context)){Fired=true;throw new IOException("Synthetic no-result acknowledgement loss.");}return Task.CompletedTask;}
    }
}
