using System.Security.Cryptography;
using FluxKnowledge.Application.Documents;
using FluxKnowledge.Application.Gpu;
using FluxKnowledge.Application.Pipeline;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Sources;
using FluxKnowledge.Application.Workers;
using FluxKnowledge.Domain.Gpu;
using FluxKnowledge.Domain.Common;
using FluxKnowledge.Domain.Jobs;
using FluxKnowledge.Domain.Pipeline;
using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using FluxKnowledge.Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Documents;

public sealed partial class SqlDocumentOcrHandoffTests
{
    [NativeSqlServerTheory]
    [InlineData(false, "configuration")]
    [InlineData(true, "configuration")]
    [InlineData(false, "suppressed")]
    [InlineData(true, "suppressed")]
    [InlineData(false, "path-escape")]
    [InlineData(true, "path-escape")]
    [InlineData(false, "uncertain-dispatch")]
    [InlineData(true, "uncertain-dispatch")]
    [InlineData(false, "result-tampered")]
    [InlineData(true, "result-tampered")]
    public async Task Recovery_registration_rechecks_the_original_settlement_and_source_binding(bool pdf, string change)
    {
        var seed = await CreateSettledImageFailureAsync(pdf, "document-ocr-runtime-failed");
        var recovery = await CreateCommittedRecoveryAsync(seed, pdf);
        await ChangeImageRecoveryBindingAsync(seed, change);

        Assert.Equal(0, await new SqlRetainedTextRegistrationStore(seed.Factory, TimeProvider.System, seed.ArtifactRoot)
            .OfferUnlinkedInProcessActivitiesAsync(CancellationToken.None));

        await using var verify = await seed.Factory.CreateDbContextAsync();
        Assert.False(await verify.PipelineRecords.AnyAsync(value => value.SourceRevisionId == recovery.ChildId));
        Assert.Contains(seed.FailedChildActivityId, await SqlCurrentSourceActivities.Query(verify).Select(value => value.Id).ToArrayAsync());
        Assert.Equal((int)PublicJobState.Failed, (await verify.Jobs.SingleAsync(value => value.Id == seed.FailedJobId)).PublicState);
        Assert.Empty(await verify.DocumentPublications.ToArrayAsync());
    }

    [NativeSqlServerTheory]
    [InlineData(false, "configuration")]
    [InlineData(true, "configuration")]
    [InlineData(false, "suppressed")]
    [InlineData(true, "suppressed")]
    [InlineData(false, "path-escape")]
    [InlineData(true, "path-escape")]
    [InlineData(false, "uncertain-dispatch")]
    [InlineData(true, "uncertain-dispatch")]
    [InlineData(false, "result-tampered")]
    [InlineData(true, "result-tampered")]
    public async Task Recovery_publication_rechecks_drift_after_successful_registration(bool pdf, string change)
    {
        var seed = await CreateSettledImageFailureAsync(pdf, "document-ocr-runtime-failed");
        var recovery = await CreateCommittedRecoveryAsync(seed, pdf);
        var publication = await PrepareRecoveryPublicationAsync(seed, recovery.ChildId);
        await ChangeImageRecoveryBindingAsync(seed, change);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new SqlStageTransitionStore(seed.Factory).TransitionAsync(publication, CancellationToken.None).AsTask());

        await using var verify = await seed.Factory.CreateDbContextAsync();
        Assert.Empty(await verify.DocumentPublications.ToArrayAsync());
        Assert.False((await verify.PipelineRecords.SingleAsync(value => value.Id == publication.CurrentJob.PipelineRecordId.Value)).CompletionCriteriaMet);
        Assert.Contains(seed.FailedChildActivityId, await SqlCurrentSourceActivities.Query(verify).Select(value => value.Id).ToArrayAsync());
        Assert.Equal((int)PublicJobState.Failed, (await verify.Jobs.SingleAsync(value => value.Id == seed.FailedJobId)).PublicState);
    }

    [NativeSqlServerTheory]
    [InlineData("different-publication")]
    [InlineData("deleted-record")]
    [InlineData("incomplete-stage")]
    [InlineData("unrecognised-relation")]
    public async Task Current_issue_selection_requires_the_exact_selected_success_and_preserves_independent_failures(string change)
    {
        var seed = await CreateSettledImageFailureAsync(failureReason: "document-ocr-runtime-failed");
        var recovery = await CreateCommittedRecoveryAsync(seed, false);
        var publication = await PrepareRecoveryPublicationAsync(seed, recovery.ChildId);
        await new SqlStageTransitionStore(seed.Factory).TransitionAsync(publication, CancellationToken.None);
        var independentId = Guid.NewGuid();
        await using (var alter = await seed.Factory.CreateDbContextAsync())
        {
            Assert.DoesNotContain(seed.FailedChildActivityId, await SqlCurrentSourceActivities.Query(alter).Select(value => value.Id).ToArrayAsync());
            alter.SourceActivities.Add(new() { Id = independentId, SourceRevisionId = seed.ParentId,
                ActivityKind = (int)SourceActivityKind.MetadataExtraction, ExecutionClass = (int)ExecutionClass.InProcess,
                ProcessorVersion = "source-access-test", DescriptorFingerprint = "source-access-test", InputFingerprint = seed.Hash,
                State = (int)SourceActivityState.FailedTerminal, Reason = "source-access-denied", CreatedAtUtc = DateTimeOffset.UtcNow, UpdatedAtUtc = DateTimeOffset.UtcNow });
            var record = await alter.PipelineRecords.SingleAsync(value => value.Id == publication.CurrentJob.PipelineRecordId.Value);
            switch (change)
            {
                case "different-publication": (await alter.DocumentPublications.SingleAsync()).DocumentInputSourceRevisionId = seed.ChildId; break;
                case "deleted-record": record.IsDeleted = true; break;
                case "incomplete-stage": (await alter.Jobs.SingleAsync(value => value.PipelineRecordId == record.Id && value.Stage == (int)PipelineStage.Embed)).PublicState = (int)PublicJobState.Failed; break;
                case "unrecognised-relation": (await alter.SourceActivityRelations.SingleAsync(value => value.PredecessorActivityId == seed.FailedChildActivityId)).RelationshipKind = "unrecognised"; break;
            }
            await alter.SaveChangesAsync();
        }
        await using var verify = await seed.Factory.CreateDbContextAsync();
        var current = await SqlCurrentSourceActivities.Query(verify).Select(value => value.Id).ToArrayAsync();
        Assert.Contains(seed.FailedChildActivityId, current);
        Assert.Contains(independentId, current);
        Assert.Single(await verify.GpuExecutorResultReceipts.ToArrayAsync());
        Assert.Equal((int)GpuMiniTaskExecutionState.Completed, (await verify.GpuMiniTasks.SingleAsync()).ExecutionState);
    }

    [NativeSqlServerFact]
    public async Task A_recovery_publication_cannot_be_rebound_to_a_nonexistent_pipeline_revision()
    {
        var seed = await CreateSettledImageFailureAsync(failureReason: "document-ocr-runtime-failed");
        var recovery = await CreateCommittedRecoveryAsync(seed, false);
        var publication = await PrepareRecoveryPublicationAsync(seed, recovery.ChildId);
        await new SqlStageTransitionStore(seed.Factory).TransitionAsync(publication, CancellationToken.None);
        await using (var alter = await seed.Factory.CreateDbContextAsync())
        {
            (await alter.DocumentPublications.SingleAsync()).PipelineRecordRevision++;
            var refused = await Assert.ThrowsAsync<DbUpdateException>(() => alter.SaveChangesAsync());
            Assert.Contains("FK_DocumentPublications_PipelineRecords_PipelineRecordId_PipelineRecordRevision", refused.InnerException!.Message, StringComparison.Ordinal);
        }
        await using var verify = await seed.Factory.CreateDbContextAsync();
        Assert.Equal(1, (await verify.DocumentPublications.SingleAsync()).PipelineRecordRevision);
        Assert.DoesNotContain(seed.FailedChildActivityId, await SqlCurrentSourceActivities.Query(verify).Select(value => value.Id).ToArrayAsync());
        Assert.Equal((int)PublicJobState.Failed, (await verify.Jobs.SingleAsync(value => value.Id == seed.FailedJobId)).PublicState);
    }

    [NativeSqlServerFact]
    public async Task Job_owned_ocr_task_cannot_be_rebound_to_an_interactive_executor()
    {
        var seed = await CreateSettledImageFailureAsync();
        await using (var change = await seed.Factory.CreateDbContextAsync())
        {
            (await change.GpuMiniTasks.SingleAsync()).RequiredExecutorKey = "different-required-executor";
            var refusal = await Assert.ThrowsAsync<DbUpdateException>(() => change.SaveChangesAsync());
            Assert.Contains("CK_GpuMiniTasks_ExclusiveOwner", refusal.InnerException!.Message, StringComparison.Ordinal);
        }
        await using var verify = await seed.Factory.CreateDbContextAsync();
        Assert.Null((await verify.GpuMiniTasks.SingleAsync()).RequiredExecutorKey);
        Assert.Equal(seed.FailedJobId, (await verify.GpuMiniTasks.SingleAsync()).ParentJobId);
    }

    [NativeSqlServerTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_recovered_failure_stays_current_until_its_exact_successor_publication(bool pdf)
    {
        var seed = await CreateSettledImageFailureAsync(pdf, "document-ocr-runtime-failed");
        var descriptor = pdf ? PdfDocumentProcessor.Capability : ImageDocumentInputProcessor.Capability;
        var store = new SqlRetainedProcessorBranchStore(seed.Factory, TimeProvider.System);
        var request = new DocumentReprocessRequest(new(seed.ParentId), seed.Hash, descriptor.ProcessorFingerprint, seed.FailedJobId, 1);
        var created = await store.RequestDocumentReprocessAsync(request, CancellationToken.None);
        Assert.True(created.Created);
        var claim = Assert.Single(await store.ClaimAsync("publication-recovery-test", 1, descriptor.ProcessorFingerprint, CancellationToken.None));
        var input = new RetainedSourceBytes(new(seed.ParentId), seed.Bytes, seed.Hash, seed.Bytes.Length);
        var completion = pdf
            ? await new PdfDocumentProcessor(new SqlRetainedArtifactWriter(seed.Factory, seed.ArtifactRoot)).ProcessAsync(claim, input, new(), CancellationToken.None)
            : await new ImageDocumentInputProcessor().ProcessAsync(claim, input, new(), CancellationToken.None);
        Assert.True(await store.CommitAsync(claim, completion, CancellationToken.None));
        Assert.Equal(1, await new SqlRetainedTextRegistrationStore(seed.Factory, TimeProvider.System, seed.ArtifactRoot)
            .OfferUnlinkedInProcessActivitiesAsync(CancellationToken.None));
        var now = DateTimeOffset.UtcNow;
        var jobId = Guid.NewGuid(); var deliveryId = Guid.NewGuid();
        Guid childId; Guid recordId;
        await using (var arrange = await seed.Factory.CreateDbContextAsync())
        {
            Assert.Contains(seed.FailedChildActivityId, await SqlCurrentSourceActivities.Query(arrange).Select(value => value.Id).ToArrayAsync());
            childId = Assert.IsType<Guid>((await arrange.SourceProcessorBranchMembers.SingleAsync(value => value.BranchId == claim.BranchId)).ChildSourceRevisionId);
            var record = await arrange.PipelineRecords.SingleAsync(value => value.SourceRevisionId == childId);
            recordId = record.Id; record.CurrentStage = (int)PipelineStage.Publish;
            var extract = await arrange.Jobs.SingleAsync(value => value.PipelineRecordId == recordId);
            extract.PublicState = (int)PublicJobState.Completed;
            foreach (var stage in new[] { PipelineStage.Normalise, PipelineStage.CanonicalIndex, PipelineStage.Embed })
                arrange.Jobs.Add(new() { Id = Guid.NewGuid(), PipelineRecordId = recordId, SourceRevision = 1,
                    Stage = (int)stage, Operation = "synthetic-prerequisite", PublicState = (int)PublicJobState.Completed, DueAtUtc = now });
            foreach (var delivery in await arrange.OutboxMessages.Where(value => value.PipelineRecordId == recordId).ToArrayAsync()) delivery.DispatchedAtUtc = now;
            arrange.Jobs.Add(new() { Id = jobId, PipelineRecordId = recordId, SourceRevision = 1,
                Stage = (int)PipelineStage.Publish, Operation = PipelineOperations.Publish, PublicState = (int)PublicJobState.WorkerProcessing,
                DueAtUtc = now, LeaseOwner = "recovery-publish-worker", LeaseGeneration = 1, LeaseExpiresAtUtc = now.AddMinutes(5) });
            arrange.OutboxMessages.Add(new() { Id = deliveryId, JobId = jobId, PipelineRecordId = recordId, SourceRevision = 1,
                Stage = (int)PipelineStage.Publish, Operation = PipelineOperations.Publish, IdempotencyKey = "exact-recovery-publication",
                DueAtUtc = now, CreatedAtUtc = now, LeaseOwner = "recovery-publish-delivery", LeaseGeneration = 1, LeaseExpiresAtUtc = now.AddMinutes(5) });
            await arrange.SaveChangesAsync();
        }
        await new SqlStageTransitionStore(seed.Factory).TransitionAsync(new(
            new ClaimedDispatchMessage(new(deliveryId), new(recordId), 1, PipelineStage.Publish, PipelineOperations.Publish, 0,
                "exact-recovery-publication", now, "recovery-publish-delivery", now.AddMinutes(5), 1),
            new ClaimedJob(new(jobId), new(recordId), 1, PipelineStage.Publish, PipelineOperations.Publish,
                PublicJobState.WorkerProcessing, now, 0, "recovery-publish-worker", now.AddMinutes(5), 1),
            new StageArtifact(Guid.NewGuid(), PipelineStage.Publish, new string('a', 64), "text/plain; charset=utf-8", "recovered text", now),
            null, null, "exact-recovery-publication-test"), CancellationToken.None);

        await using var verify = await seed.Factory.CreateDbContextAsync();
        var publication = Assert.Single(await verify.DocumentPublications.ToArrayAsync());
        Assert.Equal((seed.ParentId, childId, claim.BranchId, recordId),
            (publication.OwnerSourceRevisionId, publication.DocumentInputSourceRevisionId, publication.SourceProcessorBranchId, publication.PipelineRecordId));
        Assert.True((await verify.PipelineRecords.SingleAsync(value => value.Id == recordId)).CompletionCriteriaMet);
        Assert.DoesNotContain(seed.FailedChildActivityId, await SqlCurrentSourceActivities.Query(verify).Select(value => value.Id).ToArrayAsync());
        Assert.Equal((int)PublicJobState.Failed, (await verify.Jobs.SingleAsync(value => value.Id == seed.FailedJobId)).PublicState);
        Assert.Equal("document-ocr-runtime-failed", (await verify.SourceActivities.SingleAsync(value => value.Id == seed.FailedChildActivityId)).Reason);
        Assert.Single(await verify.GpuExecutorResultReceipts.ToArrayAsync());
    }

    [NativeSqlServerTheory]
    [InlineData(false, "document-ocr-runtime-failed", true)]
    [InlineData(true, "document-ocr-runtime-failed", true)]
    [InlineData(true, "pdf-document-encrypted", false)]
    public async Task Exact_document_recovery_preserves_the_input_contract_and_replaces_only_a_proved_failure(bool pdf, string reason, bool native)
    {
        var seed = await CreateSettledImageFailureAsync(pdf, reason, native);
        var descriptor = pdf ? PdfDocumentProcessor.Capability : ImageDocumentInputProcessor.Capability;
        var store = new SqlRetainedProcessorBranchStore(seed.Factory, TimeProvider.System);
        var request = new DocumentReprocessRequest(new(seed.ParentId), seed.Hash, descriptor.ProcessorFingerprint, seed.FailedJobId, 1);

        var created = await store.RequestDocumentReprocessAsync(request, CancellationToken.None);
        Assert.True(created.Created);
        Assert.True((await store.RequestDocumentReprocessAsync(request, CancellationToken.None)).WasReplay);
        var claim = Assert.Single(await store.ClaimAsync("common-document-test", 1, descriptor.ProcessorFingerprint, CancellationToken.None));
        Assert.Equal(created.SuccessorBranchId, claim.BranchId);
        Assert.NotNull(claim.DocumentRecoveryId);
        var input = new RetainedSourceBytes(new(seed.ParentId), seed.Bytes, seed.Hash, seed.Bytes.Length);
        var completion = pdf
            ? await new PdfDocumentProcessor(new SqlRetainedArtifactWriter(seed.Factory, seed.ArtifactRoot)).ProcessAsync(claim, input, new(), CancellationToken.None)
            : await new ImageDocumentInputProcessor().ProcessAsync(claim, input, new(), CancellationToken.None);
        Assert.True(await store.CommitAsync(claim, completion, CancellationToken.None));

        await using var verify = await seed.Factory.CreateDbContextAsync();
        var branch = await verify.SourceProcessorBranches.SingleAsync(value => value.Id == created.SuccessorBranchId);
        Assert.Equal(descriptor.ProcessorVersion, branch.ProcessorVersion);
        Assert.Equal(descriptor.ProcessorFingerprint, branch.ProcessorFingerprint);
        var member = await verify.SourceProcessorBranchMembers.SingleAsync(value => value.BranchId == branch.Id);
        var child = await verify.SourceRevisions.SingleAsync(value => value.Id == member.ChildSourceRevisionId);
        Assert.NotEqual(seed.ChildId, child.Id);
        Assert.Equal(seed.Hash, child.ContentSha256);
        Assert.Equal(seed.Bytes.Length, child.ByteLength);
        Assert.Equal(pdf ? DocumentProcessingInput.PdfClassification : DocumentProcessingInput.ImageClassification, child.Classification);
        Assert.Equal((int)PublicJobState.Failed, (await verify.Jobs.SingleAsync(value => value.Id == seed.FailedJobId)).PublicState);
        Assert.Equal(reason, (await verify.Jobs.SingleAsync(value => value.Id == seed.FailedJobId)).Reason);
        Assert.Equal(native ? 1 : 0, await verify.DocumentOcrRequests.CountAsync());
        Assert.Equal(2, await verify.SourceActivityRelations.CountAsync());
    }

    [NativeSqlServerTheory]
    [InlineData("document-ocr-runtime-timeout")]
    [InlineData("document-ocr-runtime-launch-failed")]
    [InlineData("provider-execution-failed")]
    [InlineData("document-ocr-cancelled")]
    public async Task Exact_document_recovery_does_not_infer_execution_settlement_from_an_unrelated_failure(string reason)
    {
        var seed = await CreateSettledImageFailureAsync(failureReason: reason);
        Assert.False((await new SqlRetainedProcessorBranchStore(seed.Factory, TimeProvider.System)
            .RequestDocumentReprocessAsync(ImageRecoveryRequest(seed), CancellationToken.None)).Accepted);
    }

    [NativeSqlServerTheory]
    [InlineData("document-ocr-runtime-failed", false, false)]
    [InlineData("pdf-document-encrypted", true, false)]
    [InlineData("pdf-document-encrypted", true, true)]
    public async Task Exact_pdf_recovery_requires_the_correct_native_or_no_native_history(string reason, bool native, bool removeRequest)
    {
        var seed = await CreateSettledImageFailureAsync(pdf: true, failureReason: reason, native: native);
        if (removeRequest)
        {
            await using var change = await seed.Factory.CreateDbContextAsync();
            Assert.Equal(1, await change.DocumentOcrRequests.ExecuteDeleteAsync());
            Assert.Equal(1, await change.GpuMiniTasks.CountAsync());
        }
        Assert.False((await new SqlRetainedProcessorBranchStore(seed.Factory, TimeProvider.System)
            .RequestDocumentReprocessAsync(new(new(seed.ParentId), seed.Hash, PdfDocumentProcessor.Capability.ProcessorFingerprint,
                seed.FailedJobId, 1), CancellationToken.None)).Accepted);
    }

    [NativeSqlServerFact]
    public async Task Late_old_results_callbacks_and_requeue_cannot_reopen_the_failed_job_or_change_the_recovery()
    {
        var seed = await CreateSettledImageFailureAsync();
        var store = new SqlRetainedProcessorBranchStore(seed.Factory, TimeProvider.System);
        var created = await store.RequestDocumentReprocessAsync(ImageRecoveryRequest(seed), CancellationToken.None);
        Assert.True(created.Created);
        var claim = await store.ClaimDocumentBranchAsync(created.SuccessorBranchId!.Value, new(seed.ParentId), seed.Hash,
            ImageDocumentInputProcessor.Capability.ProcessorFingerprint, "late-image-test", CancellationToken.None);
        Assert.NotNull(claim);
        var completion = await new ImageDocumentInputProcessor().ProcessAsync(claim,
            new(new(seed.ParentId), seed.Bytes, seed.Hash, seed.Bytes.Length), new(), CancellationToken.None);
        Assert.True(await store.CommitAsync(claim, completion, CancellationToken.None));
        string proof; Guid childId; byte[] digest;
        await using (var before = await seed.Factory.CreateDbContextAsync())
        {
            proof = (await before.SourceActivities.SingleAsync(value => value.Id == claim.DocumentRecoveryId)).AttemptEvidenceJson!;
            childId = Assert.IsType<Guid>((await before.SourceProcessorBranchMembers.SingleAsync(value => value.BranchId == claim.BranchId)).ChildSourceRevisionId);
            digest = (await before.DocumentOcrRequests.SingleAsync()).ResultDigest!;
        }
        var ocr = CreateStore(seed.Factory);
        foreach (var reason in new[] { "image-document-dimensions-exceeded", "different-conflicting-failure" })
            await Assert.ThrowsAsync<InvalidOperationException>(() => ocr.StoreResultAsync(seed.Handle!, seed.MiniTaskId,
                new(false, reason, []), CancellationToken.None).AsTask());
        Assert.False((await CreateLifecycle(seed.Factory).HandleCallbackAsync(Guid.NewGuid(), new(seed.Handle!, GpuBatchCallbackKind.Completed,
            [new(seed.MiniTaskId, GpuMiniTaskBoundaryDisposition.Completed)], true), CancellationToken.None)).Accepted);
        Assert.False(await ocr.RequeueCompletedAsync(seed.Handle!, seed.MiniTaskId, CancellationToken.None));
        await using var verify = await seed.Factory.CreateDbContextAsync();
        Assert.Equal(proof, (await verify.SourceActivities.SingleAsync(value => value.Id == claim.DocumentRecoveryId)).AttemptEvidenceJson);
        Assert.Equal(childId, (await verify.SourceProcessorBranchMembers.SingleAsync(value => value.BranchId == claim.BranchId)).ChildSourceRevisionId);
        Assert.Equal(digest, (await verify.DocumentOcrRequests.SingleAsync()).ResultDigest);
        Assert.Equal(2, await verify.SourceActivityRelations.CountAsync());
        Assert.Equal((int)PublicJobState.Failed, (await verify.Jobs.SingleAsync(value => value.Id == seed.FailedJobId)).PublicState);
        Assert.Equal((int)GpuMiniTaskExecutionState.Completed, (await verify.GpuMiniTasks.SingleAsync()).ExecutionState);
    }

    [NativeSqlServerTheory]
    [InlineData("exact-claim", false)]
    [InlineData("exact-claim", true)]
    [InlineData("generic-claim", false)]
    [InlineData("generic-claim", true)]
    [InlineData("commit", false)]
    [InlineData("commit", true)]
    public async Task Marked_image_recovery_never_falls_back_to_ordinary_processing_when_lineage_is_missing_or_wrong(string phase, bool wrongKind)
    {
        var seed = await CreateSettledImageFailureAsync();
        var store = new SqlRetainedProcessorBranchStore(seed.Factory, TimeProvider.System);
        var created = await store.RequestDocumentReprocessAsync(ImageRecoveryRequest(seed), CancellationToken.None);
        Assert.True(created.Created);
        RetainedProcessorClaim? claim = null;
        RetainedProcessorCompletion? completion = null;
        if (phase == "commit")
        {
            claim = await store.ClaimDocumentBranchAsync(created.SuccessorBranchId!.Value, new(seed.ParentId), seed.Hash,
                ImageDocumentInputProcessor.Capability.ProcessorFingerprint, "lineage-image-test", CancellationToken.None);
            Assert.NotNull(claim);
            completion = await new ImageDocumentInputProcessor().ProcessAsync(claim,
                new(new(seed.ParentId), seed.Bytes, seed.Hash, seed.Bytes.Length), new(), CancellationToken.None);
        }
        await using (var change = await seed.Factory.CreateDbContextAsync())
        {
            var relation = await change.SourceActivityRelations.SingleAsync();
            if (wrongKind) relation.RelationshipKind = "unrecognised-recovery-lineage";
            else change.SourceActivityRelations.Remove(relation);
            await change.SaveChangesAsync();
        }
        if (phase == "generic-claim")
            Assert.Empty(await store.ClaimAsync("lineage-image-test", 1, ImageDocumentInputProcessor.Capability.ProcessorFingerprint, CancellationToken.None));
        else if (phase == "exact-claim")
            Assert.Null(await store.ClaimDocumentBranchAsync(created.SuccessorBranchId!.Value, new(seed.ParentId), seed.Hash,
                ImageDocumentInputProcessor.Capability.ProcessorFingerprint, "lineage-image-test", CancellationToken.None));
        else Assert.False(await store.CommitAsync(claim!, completion!, CancellationToken.None));
        await using var verify = await seed.Factory.CreateDbContextAsync();
        Assert.Single(await verify.SourceProcessorBranchMembers.ToArrayAsync());
        Assert.Equal((int)PublicJobState.Failed, (await verify.Jobs.SingleAsync(value => value.Id == seed.FailedJobId)).PublicState);
    }

    [NativeSqlServerTheory]
    [InlineData("admission", GpuCapacitySlotState.Reserved)]
    [InlineData("admission", GpuCapacitySlotState.Uncertain)]
    [InlineData("exact-claim", GpuCapacitySlotState.Reserved)]
    [InlineData("exact-claim", GpuCapacitySlotState.Uncertain)]
    [InlineData("generic-claim", GpuCapacitySlotState.Reserved)]
    [InlineData("generic-claim", GpuCapacitySlotState.Uncertain)]
    [InlineData("commit", GpuCapacitySlotState.Reserved)]
    [InlineData("commit", GpuCapacitySlotState.Uncertain)]
    public async Task Exact_image_recovery_refuses_a_terminal_batch_that_still_owns_capacity(string phase, GpuCapacitySlotState state)
    {
        var seed = await CreateSettledImageFailureAsync();
        var store = new SqlRetainedProcessorBranchStore(seed.Factory, TimeProvider.System);
        DocumentReprocessRequestResult? created = null;
        RetainedProcessorClaim? claim = null;
        RetainedProcessorCompletion? completion = null;
        if (phase != "admission") created = await store.RequestDocumentReprocessAsync(ImageRecoveryRequest(seed), CancellationToken.None);
        if (phase == "commit")
        {
            claim = await store.ClaimDocumentBranchAsync(created!.SuccessorBranchId!.Value, new(seed.ParentId), seed.Hash,
                ImageDocumentInputProcessor.Capability.ProcessorFingerprint, "capacity-image-test", CancellationToken.None);
            Assert.NotNull(claim);
            completion = await new ImageDocumentInputProcessor().ProcessAsync(claim,
                new(new(seed.ParentId), seed.Bytes, seed.Hash, seed.Bytes.Length), new(), CancellationToken.None);
        }
        await using (var change = await seed.Factory.CreateDbContextAsync())
        {
            var slot = await change.GpuCapacitySlots.SingleAsync();
            slot.ActiveBatchId = (await change.GpuMiniTasks.SingleAsync()).BatchId;
            slot.State = (int)state;
            slot.OwnerKey = "contradictory-old-batch-owner";
            await change.SaveChangesAsync();
        }
        if (phase == "admission") Assert.False((await store.RequestDocumentReprocessAsync(ImageRecoveryRequest(seed), CancellationToken.None)).Accepted);
        else if (phase == "generic-claim")
            Assert.Empty(await store.ClaimAsync("capacity-image-test", 1, ImageDocumentInputProcessor.Capability.ProcessorFingerprint, CancellationToken.None));
        else if (phase == "exact-claim")
            Assert.Null(await store.ClaimDocumentBranchAsync(created!.SuccessorBranchId!.Value, new(seed.ParentId), seed.Hash,
                ImageDocumentInputProcessor.Capability.ProcessorFingerprint, "capacity-image-test", CancellationToken.None));
        else Assert.False(await store.CommitAsync(claim!, completion!, CancellationToken.None));
        await using var verify = await seed.Factory.CreateDbContextAsync();
        Assert.Single(await verify.SourceProcessorBranchMembers.ToArrayAsync());
        Assert.Equal((int)state, (await verify.GpuCapacitySlots.SingleAsync()).State);
    }

    [NativeSqlServerFact]
    public async Task Exact_image_recovery_does_not_treat_unrelated_reuse_of_the_same_slot_as_old_batch_ownership()
    {
        var seed = await CreateSettledImageFailureAsync();
        await using (var change = await seed.Factory.CreateDbContextAsync())
        {
            var slot = await change.GpuCapacitySlots.SingleAsync();
            var unrelated = new GpuBatchEntity { Id = Guid.NewGuid(), CapacitySlotKey = slot.SlotKey,
                ModelRuntimeKey = "unrelated-runtime", SettingsFingerprint = "unrelated-settings", OwnerKey = "unrelated-owner",
                ItemCount = 1, AdmissionGeneration = 1, State = (int)GpuBatchState.Active,
                CreatedAtUtc = DateTimeOffset.UtcNow, UpdatedAtUtc = DateTimeOffset.UtcNow };
            change.GpuBatches.Add(unrelated);
            slot.ActiveBatchId = unrelated.Id; slot.State = (int)GpuCapacitySlotState.Reserved; slot.OwnerKey = unrelated.OwnerKey;
            await change.SaveChangesAsync();
        }
        Assert.True((await new SqlRetainedProcessorBranchStore(seed.Factory, TimeProvider.System)
            .RequestDocumentReprocessAsync(ImageRecoveryRequest(seed), CancellationToken.None)).Created);
    }

    [NativeSqlServerFact]
    public async Task Exact_image_recovery_creates_one_fenced_successor_and_distinct_child_without_resetting_old_work()
    {
        var seed = await CreateSettledImageFailureAsync();
        var store = new SqlRetainedProcessorBranchStore(seed.Factory, TimeProvider.System);
        var request = ImageRecoveryRequest(seed);
        var created = await store.RequestDocumentReprocessAsync(request, CancellationToken.None);
        Assert.True(created.Created);
        var replay = await store.RequestDocumentReprocessAsync(request, CancellationToken.None);
        Assert.True(replay.WasReplay);
        Assert.Equal(created.SuccessorBranchId, replay.SuccessorBranchId);
        var claim = await store.ClaimDocumentBranchAsync(created.SuccessorBranchId!.Value, new(seed.ParentId), seed.Hash,
            ImageDocumentInputProcessor.Capability.ProcessorFingerprint, "exact-image-test", CancellationToken.None);
        Assert.NotNull(claim);
        Assert.NotNull(claim.DocumentRecoveryId);
        var completion = await new ImageDocumentInputProcessor().ProcessAsync(claim,
            new(new(seed.ParentId), seed.Bytes, seed.Hash, seed.Bytes.Length), new(), CancellationToken.None);
        Assert.True(await store.CommitAsync(claim, completion, CancellationToken.None));
        await using var verify = await seed.Factory.CreateDbContextAsync();
        var successor = await verify.SourceProcessorBranches.SingleAsync(value => value.Id == created.SuccessorBranchId);
        var member = await verify.SourceProcessorBranchMembers.SingleAsync(value => value.BranchId == successor.Id);
        Assert.NotEqual(seed.ChildId, member.ChildSourceRevisionId);
        Assert.Equal(successor.SourceActivityId, claim.DocumentRecoveryId);
        var parent = await verify.SourceActivities.SingleAsync(value => value.Id == successor.SourceActivityId);
        Assert.Null(parent.ResultingPipelineRecordId);
        Assert.Null(parent.ResultingPipelineRecordRevision);
        Assert.Contains(seed.FailedJobId.ToString("D"), parent.AttemptEvidenceJson!, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, await verify.SourceActivityRelations.CountAsync());
        Assert.Equal(2, await verify.SourceActivityRelations.Select(value => value.SuccessorActivityId).Distinct().CountAsync());
        Assert.Contains(seed.FailedChildActivityId, await verify.SourceActivityRelations.Select(value => value.PredecessorActivityId).ToArrayAsync());
        Assert.Contains(seed.FailedChildActivityId, await SqlCurrentSourceActivities.Query(verify).Select(value => value.Id).ToArrayAsync());
        Assert.Equal((int)PublicJobState.Failed, (await verify.Jobs.SingleAsync(value => value.Id == seed.FailedJobId)).PublicState);
        Assert.Equal((int)GpuMiniTaskExecutionState.Completed, (await verify.GpuMiniTasks.SingleAsync()).ExecutionState);
        Assert.Single(await verify.GpuExecutorResultReceipts.ToArrayAsync());
        Assert.Single(await verify.DocumentOcrRequests.ToArrayAsync());
    }

    [NativeSqlServerTheory]
    [InlineData("paused")]
    [InlineData("configuration")]
    [InlineData("suppressed")]
    [InlineData("uncertain-dispatch")]
    [InlineData("uncertain-task")]
    [InlineData("result-tampered")]
    [InlineData("different-failure")]
    [InlineData("unfinished-delivery")]
    [InlineData("unfinished-attempt")]
    [InlineData("ambiguous-source")]
    [InlineData("already-published")]
    [InlineData("wrong-executor")]
    [InlineData("batch-runtime")]
    [InlineData("batch-settings")]
    [InlineData("path-escape")]
    [InlineData("root-links")]
    [InlineData("member-fingerprint")]
    [InlineData("completion-receipt")]
    [InlineData("child-contract")]
    public async Task Exact_image_recovery_refuses_unsettled_stale_or_independent_bindings(string change)
    {
        var seed = await CreateSettledImageFailureAsync();
        await ChangeImageRecoveryBindingAsync(seed, change);
        var result = await new SqlRetainedProcessorBranchStore(seed.Factory, TimeProvider.System)
            .RequestDocumentReprocessAsync(ImageRecoveryRequest(seed), CancellationToken.None);
        Assert.False(result.Accepted);
        await using var verify = await seed.Factory.CreateDbContextAsync();
        Assert.Single(await verify.SourceProcessorBranches.ToArrayAsync());
        Assert.Empty(await verify.SourceActivityRelations.ToArrayAsync());
    }

    [NativeSqlServerTheory]
    [InlineData("claim", "configuration")]
    [InlineData("claim", "uncertain-dispatch")]
    [InlineData("commit", "configuration")]
    [InlineData("commit", "suppressed")]
    [InlineData("commit", "uncertain-dispatch")]
    [InlineData("commit", "result-tampered")]
    [InlineData("commit", "forged-discriminator")]
    [InlineData("commit", "already-published")]
    public async Task Exact_image_recovery_rechecks_the_durable_proof_at_claim_and_commit(string phase, string change)
    {
        var seed = await CreateSettledImageFailureAsync();
        var store = new SqlRetainedProcessorBranchStore(seed.Factory, TimeProvider.System);
        var created = await store.RequestDocumentReprocessAsync(ImageRecoveryRequest(seed), CancellationToken.None);
        Assert.True(created.Created);
        if (phase == "claim") await ChangeImageRecoveryBindingAsync(seed, change);
        var claim = await store.ClaimDocumentBranchAsync(created.SuccessorBranchId!.Value, new(seed.ParentId), seed.Hash,
            ImageDocumentInputProcessor.Capability.ProcessorFingerprint, "exact-image-test", CancellationToken.None);
        if (phase == "claim") { Assert.Null(claim); return; }
        Assert.NotNull(claim);
        var completion = await new ImageDocumentInputProcessor().ProcessAsync(claim,
            new(new(seed.ParentId), seed.Bytes, seed.Hash, seed.Bytes.Length), new(), CancellationToken.None);
        if (change == "forged-discriminator") claim = claim with { DocumentRecoveryId = Guid.NewGuid() };
        else await ChangeImageRecoveryBindingAsync(seed, change);
        Assert.False(await store.CommitAsync(claim, completion, CancellationToken.None));
        await using var verify = await seed.Factory.CreateDbContextAsync();
        Assert.Single(await verify.SourceProcessorBranchMembers.ToArrayAsync());
        Assert.Single(await verify.SourceActivityRelations.ToArrayAsync());
    }

    [NativeSqlServerFact]
    public async Task Exact_image_recovery_requires_both_explicit_job_and_root_revision_and_deduplicates_concurrent_requests()
    {
        var seed = await CreateSettledImageFailureAsync();
        var store = new SqlRetainedProcessorBranchStore(seed.Factory, TimeProvider.System);
        var request = ImageRecoveryRequest(seed);
        Assert.False((await store.RequestDocumentReprocessAsync(request with { FailedExtractJobId = null }, CancellationToken.None)).Accepted);
        Assert.False((await store.RequestDocumentReprocessAsync(request with { ExpectedRootConfigurationRevision = null }, CancellationToken.None)).Accepted);
        var results = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => store.RequestDocumentReprocessAsync(request, CancellationToken.None).AsTask()));
        Assert.Single(results, value => value.Created);
        Assert.Single(results, value => value.WasReplay);
        Assert.Single(results.Select(value => value.SuccessorBranchId).Distinct());
    }

    [NativeSqlServerTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Exact_image_recovery_generic_claim_derives_its_identity_and_commit_refuses_a_forged_child(bool forgeChild)
    {
        var seed = await CreateSettledImageFailureAsync();
        var store = new SqlRetainedProcessorBranchStore(seed.Factory, TimeProvider.System);
        var created = await store.RequestDocumentReprocessAsync(ImageRecoveryRequest(seed), CancellationToken.None);
        Assert.True(created.Created);
        var claim = Assert.Single(await store.ClaimAsync("generic-image-test", 1,
            ImageDocumentInputProcessor.Capability.ProcessorFingerprint, CancellationToken.None));
        Assert.Equal(created.SuccessorBranchId, claim.BranchId);
        Assert.NotNull(claim.DocumentRecoveryId);
        var completion = await new ImageDocumentInputProcessor().ProcessAsync(claim,
            new(new(seed.ParentId), seed.Bytes, seed.Hash, seed.Bytes.Length), new(), CancellationToken.None);
        if (forgeChild) completion = completion with { Members = [completion.Members[0] with { MemberFingerprint = new string('b', 64) }] };
        Assert.Equal(!forgeChild, await store.CommitAsync(claim, completion, CancellationToken.None));
    }

    private static DocumentReprocessRequest ImageRecoveryRequest(ImageFailureSeed seed) =>
        new(new(seed.ParentId), seed.Hash, ImageDocumentInputProcessor.Capability.ProcessorFingerprint, seed.FailedJobId, 1);

    private static async Task<(Guid BranchId, Guid ChildId)> CreateCommittedRecoveryAsync(ImageFailureSeed seed, bool pdf)
    {
        var descriptor = pdf ? PdfDocumentProcessor.Capability : ImageDocumentInputProcessor.Capability;
        var store = new SqlRetainedProcessorBranchStore(seed.Factory, TimeProvider.System);
        var created = await store.RequestDocumentReprocessAsync(new(new(seed.ParentId), seed.Hash,
            descriptor.ProcessorFingerprint, seed.FailedJobId, 1), CancellationToken.None);
        Assert.True(created.Created);
        var claim = Assert.Single(await store.ClaimAsync("recovery-binding-test", 1, descriptor.ProcessorFingerprint, CancellationToken.None));
        var input = new RetainedSourceBytes(new(seed.ParentId), seed.Bytes, seed.Hash, seed.Bytes.Length);
        var completion = pdf
            ? await new PdfDocumentProcessor(new SqlRetainedArtifactWriter(seed.Factory, seed.ArtifactRoot)).ProcessAsync(claim, input, new(), CancellationToken.None)
            : await new ImageDocumentInputProcessor().ProcessAsync(claim, input, new(), CancellationToken.None);
        Assert.True(await store.CommitAsync(claim, completion, CancellationToken.None));
        await using var verify = await seed.Factory.CreateDbContextAsync();
        return (claim.BranchId, Assert.IsType<Guid>((await verify.SourceProcessorBranchMembers.SingleAsync(value => value.BranchId == claim.BranchId)).ChildSourceRevisionId));
    }

    private static async Task<StageTransitionRequest> PrepareRecoveryPublicationAsync(ImageFailureSeed seed, Guid childId)
    {
        Assert.Equal(1, await new SqlRetainedTextRegistrationStore(seed.Factory, TimeProvider.System, seed.ArtifactRoot)
            .OfferUnlinkedInProcessActivitiesAsync(CancellationToken.None));
        var now = DateTimeOffset.UtcNow;
        var jobId = Guid.NewGuid(); var deliveryId = Guid.NewGuid();
        await using var arrange = await seed.Factory.CreateDbContextAsync();
        var record = await arrange.PipelineRecords.SingleAsync(value => value.SourceRevisionId == childId);
        record.CurrentStage = (int)PipelineStage.Publish;
        (await arrange.Jobs.SingleAsync(value => value.PipelineRecordId == record.Id)).PublicState = (int)PublicJobState.Completed;
        foreach (var stage in new[] { PipelineStage.Normalise, PipelineStage.CanonicalIndex, PipelineStage.Embed })
            arrange.Jobs.Add(new() { Id = Guid.NewGuid(), PipelineRecordId = record.Id, SourceRevision = 1,
                Stage = (int)stage, Operation = "synthetic-prerequisite", PublicState = (int)PublicJobState.Completed, DueAtUtc = now });
        foreach (var delivery in await arrange.OutboxMessages.Where(value => value.PipelineRecordId == record.Id).ToArrayAsync()) delivery.DispatchedAtUtc = now;
        arrange.Jobs.Add(new() { Id = jobId, PipelineRecordId = record.Id, SourceRevision = 1,
            Stage = (int)PipelineStage.Publish, Operation = PipelineOperations.Publish, PublicState = (int)PublicJobState.WorkerProcessing,
            DueAtUtc = now, LeaseOwner = "recovery-publish-worker", LeaseGeneration = 1, LeaseExpiresAtUtc = now.AddMinutes(5) });
        arrange.OutboxMessages.Add(new() { Id = deliveryId, JobId = jobId, PipelineRecordId = record.Id, SourceRevision = 1,
            Stage = (int)PipelineStage.Publish, Operation = PipelineOperations.Publish, IdempotencyKey = "exact-recovery-publication",
            DueAtUtc = now, CreatedAtUtc = now, LeaseOwner = "recovery-publish-delivery", LeaseGeneration = 1, LeaseExpiresAtUtc = now.AddMinutes(5) });
        await arrange.SaveChangesAsync();
        return new(new ClaimedDispatchMessage(new(deliveryId), new(record.Id), 1, PipelineStage.Publish, PipelineOperations.Publish, 0,
                "exact-recovery-publication", now, "recovery-publish-delivery", now.AddMinutes(5), 1),
            new ClaimedJob(new(jobId), new(record.Id), 1, PipelineStage.Publish, PipelineOperations.Publish,
                PublicJobState.WorkerProcessing, now, 0, "recovery-publish-worker", now.AddMinutes(5), 1),
            new StageArtifact(Guid.NewGuid(), PipelineStage.Publish, new string('a', 64), "text/plain; charset=utf-8", "recovered text", now),
            null, null, "exact-recovery-publication-test");
    }

    private async Task<ImageFailureSeed> CreateSettledImageFailureAsync(bool pdf = false,
        string failureReason = "image-document-dimensions-exceeded", bool native = true,
        Func<RetainedProcessorDerivedChild, string>? predecessorLocator = null, bool withPhysicalHistory = false,
        bool suppressPrevious = true, bool differentHistoryIdentity = false, int parentOriginKind = 0)
    {
        await SqlTestData.ClearPhase3SourceDataAsync(_fixture);
        var factory = SqlTestData.CreateFactory(_fixture);
        var now = DateTimeOffset.UtcNow;
        byte[] bytes = pdf ? "%PDF-1.4\nsynthetic retained input"u8.ToArray() : [0xff, 0xd8, 0xff, 1, 2, 3, 4];
        var descriptor = pdf ? PdfDocumentProcessor.Capability : ImageDocumentInputProcessor.Capability;
        var contract = pdf ? DocumentProcessingInput.Pdf : DocumentProcessingInput.Jpeg;
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var rootId = Guid.NewGuid(); var parentId = Guid.NewGuid(); var childId = Guid.NewGuid();
        var parentActivityId = Guid.NewGuid(); var childActivityId = Guid.NewGuid(); var branchId = Guid.NewGuid();
        var recordId = Guid.NewGuid(); var identityId = Guid.NewGuid(); var jobId = Guid.NewGuid();
        var artifactRoot = Path.Combine(Path.GetTempPath(), "image-recovery", Guid.NewGuid().ToString("N"));
        var sourceRoot = Path.Combine(artifactRoot, "original-never-read");
        var relative = Path.Combine("sha256", hash[..2], hash + ".bin");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(artifactRoot, relative))!);
        await File.WriteAllBytesAsync(Path.Combine(artifactRoot, relative), bytes);
        var parentStable = "image-owner:" + parentId.ToString("N");
        var parentClaim = new RetainedProcessorClaim(branchId, new(parentId), parentStable, hash, "seed", 1, now.AddMinutes(1));
        var retained = new RetainedSourceBytes(new(parentId), bytes, hash, bytes.Length);
        var completion = pdf
            ? await new PdfDocumentProcessor(new SqlRetainedArtifactWriter(factory, artifactRoot)).ProcessAsync(parentClaim, retained, new(), CancellationToken.None)
            : await new ImageDocumentInputProcessor().ProcessAsync(parentClaim, retained, new(), CancellationToken.None);
        var child = Assert.Single(completion.Members);
        var childLocator = predecessorLocator?.Invoke(child) ?? child.SyntheticLocator;
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.SourceRootConfigurations.Add(new() { Id = rootId, CanonicalPath = sourceRoot, DisplayName = "Image recovery test", State = 0,
                Recursive = true, MaximumFileBytes = 1_000_000, AllowedClassificationsJson = "[]", ConfigurationRevision = 1,
                ReconciliationCadenceSeconds = 900, CreatedAtUtc = now, UpdatedAtUtc = now });
            var parent = new FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities.SourceRevisionEntity
            { Id = parentId, SourceRootId = rootId, StableSourceIdentity = parentStable, CanonicalPath = Path.Combine(sourceRoot, pdf ? "document.pdf" : "diagram.jpg"),
                Revision = 1, ContentSha256 = hash, ByteLength = bytes.Length, Classification = descriptor.AcceptedClassification, Extension = contract.Extension, DiscoveredAtUtc = now };
            if (withPhysicalHistory) SqlTestData.AddPhysicalRevisionHistory(db, parent, suppressPrevious, differentHistoryIdentity);
            parent.OriginKind = parentOriginKind;
            db.SourceRevisions.Add(parent);
            await db.SaveChangesAsync();
            db.SourceRevisions.Add(new() { Id = childId, SourceRootId = rootId, StableSourceIdentity = child.StableSourceIdentity, CanonicalPath = childLocator,
                Revision = 1, ContentSha256 = hash, ByteLength = bytes.Length, Classification = child.Classification, Extension = child.Extension,
                OriginKind = 2, ParentSourceRevisionId = parentId, DiscoveredAtUtc = now });
            db.SourceActivities.Add(new() { Id = parentActivityId, SourceRevisionId = parentId, ActivityKind = (int)SourceActivityKind.TextExtraction,
                ExecutionClass = (int)ExecutionClass.InProcess, ProcessorVersion = descriptor.ProcessorVersion,
                DescriptorFingerprint = descriptor.ProcessorFingerprint, InputFingerprint = hash, State = (int)SourceActivityState.Pending,
                CreatedAtUtc = now, UpdatedAtUtc = now });
            db.SourceProcessorBranches.Add(new() { Id = branchId, SourceActivityId = parentActivityId, SourceRevisionId = parentId, InputSha256 = hash,
                ProcessorVersion = descriptor.ProcessorVersion, ProcessorFingerprint = descriptor.ProcessorFingerprint,
                State = (int)RetainedProcessorBranchState.Completed, LeaseGeneration = 1, AttemptCount = 1, CompletedMemberCount = 1,
                CompletionReceiptFingerprint = completion.ReceiptFingerprint, CreatedAtUtc = now, UpdatedAtUtc = now });
            db.SourceProcessorAttempts.Add(new() { Id = Guid.NewGuid(), BranchId = branchId, LeaseGeneration = 1, StartedAtUtc = now, FinishedAtUtc = now, OutcomeCode = "completed" });
            db.SourceIdentities.Add(new() { Id = identityId, SourceKind = "retained document", StableKey = childLocator, CreatedAtUtc = now });
            db.PipelineRecords.Add(new() { Id = recordId, SourceIdentityId = identityId, SourceRevisionId = childId, Revision = 1, ContentHash = hash,
                RootLineageRecordId = recordId, CurrentStage = (int)PipelineStage.Extract, RegisteredAtUtc = now });
            db.SourceActivities.Add(new() { Id = childActivityId, SourceRevisionId = childId, ActivityKind = (int)SourceActivityKind.DocumentParsing,
                ExecutionClass = (int)ExecutionClass.InProcess, ProcessorVersion = contract.ProcessorVersion,
                DescriptorFingerprint = contract.ProcessorFingerprint, InputFingerprint = hash, State = (int)SourceActivityState.Pending,
                ResultingPipelineRecordId = recordId, ResultingPipelineRecordRevision = 1, CreatedAtUtc = now, UpdatedAtUtc = now });
            db.SourceProcessorBranchMembers.Add(new() { Id = Guid.NewGuid(), BranchId = branchId, MemberFingerprint = child.MemberFingerprint,
                ChildSourceRevisionId = childId, ChildSourceActivityId = childActivityId, Disposition = "completed", ByteLength = bytes.Length, CreatedAtUtc = now });
            foreach (var revisionId in new[] { parentId, childId }) db.SourceArtifacts.Add(new() { Id = Guid.NewGuid(), SourceRevisionId = revisionId,
                ContentSha256 = hash, StoreRelativePath = relative, ByteLength = bytes.Length, ReferenceCount = 1, ChecksumVerifiedAtUtc = now });
            db.Jobs.Add(new() { Id = jobId, PipelineRecordId = recordId, SourceRevision = 1, Stage = (int)PipelineStage.Extract,
                Operation = PipelineOperations.ExtractDocument, PublicState = (int)PublicJobState.WorkerQueued, DueAtUtc = now });
            db.OutboxMessages.Add(new() { Id = Guid.NewGuid(), JobId = jobId, PipelineRecordId = recordId, SourceRevision = 1,
                Stage = (int)PipelineStage.Extract, Operation = PipelineOperations.ExtractDocument, IdempotencyKey = recordId.ToString("N") + ":1:image", DueAtUtc = now, CreatedAtUtc = now });
            await db.SaveChangesAsync();
        }
        await new SourceCapabilityService(new SqlSourceActivityStore(factory, TimeProvider.System),
            new LocalSourceCapabilityHandlerRegistry([new ImageDocumentInputCapabilityHandler(), new PdfDocumentCapabilityHandler()])).RegisterAsync(descriptor, CancellationToken.None);
        var claimed = await new SqlJobClaimStore(factory).ClaimNextDueAsync("image-failure-fixture", now, TimeSpan.FromMinutes(5), CancellationToken.None);
        Assert.NotNull(claimed);
        var miniTask = Guid.Empty;
        GpuExecutorBatchHandle? handle = null;
        if (native)
        {
            var ocr = CreateStore(factory);
            var handoff = await ocr.HandoffAsync(new(claimed, new(childId), hash, [0]), CancellationToken.None);
            miniTask = handoff.MiniTaskId!.Value;
            handle = await AdmitDocumentOcrTaskAsync(factory);
            var ownership = await ocr.TryClaimExecutionAsync(handle, miniTask, Guid.NewGuid(), SyntheticSupervisor(), CancellationToken.None);
            var lease = Assert.IsType<DocumentOcrExecutionLease>(ownership.Lease);
            var stored = await ocr.StoreResultAsync(lease, new(false, failureReason, []), CancellationToken.None);
            Assert.True((await ocr.ConfirmCleanupAsync(lease, new(lease.ExecutionInstanceId,
                DocumentOcrCleanupKind.VerifiedUnstarted, null, DateTimeOffset.UtcNow), CancellationToken.None)).Accepted);
            Assert.True((await ocr.SettleCompletionAsync(handle, miniTask, stored.ResultDigest, CancellationToken.None)).Accepted);
            Assert.True(await ocr.RequeueCompletedAsync(handle, miniTask, CancellationToken.None));
        }
        await using (var db = await factory.CreateDbContextAsync())
        {
            var job = await db.Jobs.SingleAsync(value => value.Id == jobId);
            job.PublicState = (int)PublicJobState.Failed; job.Reason = failureReason; job.LeaseOwner = null; job.LeaseExpiresAtUtc = null;
            var activity = await db.SourceActivities.SingleAsync(value => value.Id == childActivityId);
            activity.State = (int)SourceActivityState.FailedTerminal; activity.Reason = job.Reason; activity.UpdatedAtUtc = DateTimeOffset.UtcNow;
            foreach (var delivery in await db.OutboxMessages.ToArrayAsync()) { delivery.DispatchedAtUtc = DateTimeOffset.UtcNow; delivery.LeaseOwner = null; delivery.LeaseExpiresAtUtc = null; }
            foreach (var attempt in await db.JobAttempts.Where(value => value.JobId == jobId).ToArrayAsync()) attempt.CompletedAtUtc ??= DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
        }
        return new(factory, rootId, parentId, childId, childActivityId, branchId, recordId, jobId, miniTask, handle?.DispatchId ?? Guid.Empty, hash, bytes, artifactRoot, handle);
    }

    private static async Task ChangeImageRecoveryBindingAsync(ImageFailureSeed seed, string change)
    {
        await using var db = await seed.Factory.CreateDbContextAsync();
        switch (change)
        {
            case "paused": (await db.SourceRootConfigurations.SingleAsync()).State = (int)SourceRootState.Paused; break;
            case "configuration": (await db.SourceRootConfigurations.SingleAsync()).ConfigurationRevision++; break;
            case "suppressed": (await db.SourceRevisions.SingleAsync(value => value.Id == seed.ParentId)).SuppressedAtUtc = DateTimeOffset.UtcNow; break;
            case "root-links": (await db.SourceRootConfigurations.SingleAsync()).FollowLinks = true; break;
            case "path-escape":
                // Deliberately seed a persisted scope contradiction; the normal EF contract correctly forbids rewriting a captured path.
                await db.SourceRevisions.Where(value => value.Id == seed.ParentId).ExecuteUpdateAsync(setters =>
                    setters.SetProperty(value => value.CanonicalPath, Path.Combine(seed.ArtifactRoot, "outside.jpg")));
                break;
            case "member-fingerprint": (await db.SourceProcessorBranchMembers.SingleAsync()).MemberFingerprint = new string('c', 64); break;
            case "completion-receipt": (await db.SourceProcessorBranches.SingleAsync()).CompletionReceiptFingerprint = new string('d', 64); break;
            case "child-contract": (await db.SourceActivities.SingleAsync(value => value.Id == seed.FailedChildActivityId)).DescriptorFingerprint = "wrong-document-input-contract"; break;
            case "uncertain-dispatch": (await db.GpuExecutorDispatches.SingleAsync()).State = (int)GpuExecutorDispatchState.DeliveryUncertain; break;
            case "uncertain-task": (await db.GpuMiniTasks.SingleAsync()).ExecutionState = (int)GpuMiniTaskExecutionState.OutcomeUncertain; break;
            case "result-tampered": (await db.DocumentOcrRequests.SingleAsync()).ResultJson = "{}"; break;
            case "wrong-executor":
                (await db.GpuExecutorDispatches.SingleAsync()).ExecutorKey = "different-confirmed-executor";
                foreach (var receipt in await db.GpuExecutorResultReceipts.ToArrayAsync()) receipt.ExecutorKey = "different-confirmed-executor";
                break;
            case "batch-runtime": (await db.GpuBatches.SingleAsync()).ModelRuntimeKey = "different-runtime"; break;
            case "batch-settings": (await db.GpuBatches.SingleAsync()).SettingsFingerprint = "different-settings"; break;
            case "different-failure": (await db.Jobs.SingleAsync(value => value.Id == seed.FailedJobId)).Reason = "provider-execution-failed"; break;
            case "unfinished-delivery": (await db.OutboxMessages.FirstAsync()).DispatchedAtUtc = null; break;
            case "unfinished-attempt": db.JobAttempts.Add(new() { JobId = seed.FailedJobId, AttemptNumber = 1, LeaseGeneration = 1,
                LeaseOwner = "unfinished-native-test", StartedAtUtc = DateTimeOffset.UtcNow }); break;
            case "ambiguous-source":
                var original = await db.SourceRevisions.SingleAsync(value => value.Id == seed.ParentId);
                db.SourceRevisions.Add(new() { Id = Guid.NewGuid(), SourceRootId = seed.RootId, StableSourceIdentity = original.StableSourceIdentity,
                    Revision = 2, CanonicalPath = Path.Combine(Path.GetDirectoryName(original.CanonicalPath)!, "other.jpg"), ContentSha256 = new string('b', 64),
                    Classification = original.Classification, Extension = original.Extension, ByteLength = 1, DiscoveredAtUtc = DateTimeOffset.UtcNow }); break;
            case "already-published": db.DocumentPublications.Add(new() { OwnerSourceRevisionId = seed.ParentId, DocumentInputSourceRevisionId = seed.ChildId,
                SourceProcessorBranchId = seed.OldBranchId, PipelineRecordId = seed.RecordId, PipelineRecordRevision = 1,
                ProcessorFingerprint = ImageDocumentInputProcessor.Capability.ProcessorFingerprint, PublishedAtUtc = DateTimeOffset.UtcNow }); break;
            default: throw new ArgumentOutOfRangeException(nameof(change));
        }
        await db.SaveChangesAsync();
    }

    private sealed record ImageFailureSeed(IDbContextFactory<FluxKnowledgeDbContext> Factory, Guid RootId, Guid ParentId, Guid ChildId,
        Guid FailedChildActivityId, Guid OldBranchId, Guid RecordId, Guid FailedJobId, Guid MiniTaskId, Guid DispatchId, string Hash, byte[] Bytes,
        string ArtifactRoot, GpuExecutorBatchHandle? Handle);
}
