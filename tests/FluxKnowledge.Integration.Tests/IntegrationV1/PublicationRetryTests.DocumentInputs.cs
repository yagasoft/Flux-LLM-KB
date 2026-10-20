using System.Security.Cryptography;
using System.Text;
using FluxKnowledge.Application.Documents;
using FluxKnowledge.Application.Gpu;
using FluxKnowledge.Application.Indexing;
using FluxKnowledge.Application.IntegrationV1;
using FluxKnowledge.Application.Pipeline;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Sources;
using FluxKnowledge.Application.Workers;
using FluxKnowledge.Domain.Gpu;
using FluxKnowledge.Domain.Jobs;
using FluxKnowledge.Domain.Pipeline;
using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using FluxKnowledge.Infrastructure.SqlServer.Search;
using FluxKnowledge.Integration.Tests.Indexing;
using FluxKnowledge.Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FluxKnowledge.Integration.Tests.IntegrationV1;

public sealed partial class PublicationRetryTests
{
    [NativeSqlServerTheory]
    [InlineData("office")]
    [InlineData("office-v3")]
    [InlineData("pdf")]
    public async Task Derived_document_publish_retry_reuses_completed_work_and_publishes_cited_text(string kind)
    {
        await using var seed = await CreateDerivedFailureAsync(kind);
        var environment = seed.Environment;
        var command = Mutation(seed.Failed.Job.JobId.Value);
        var service = Service(environment);
        var preview = await service.PreviewAsync(command, "test", CancellationToken.None);

        var receipt = await service.CommitAsync(command, preview.ConfirmationId, "derived-" + kind, "test", CancellationToken.None);

        Assert.False(receipt.WasReplay);
        await AssertOldLeaseRefusedAsync(environment, seed.Failed);
        var recovered = await ClaimAsync(environment, seed.Failed.Job.PipelineRecordId.Value);
        var transitions = new StageTransitionService(new SqlStageTransitionStore(environment.Factory), new StatusPublisher(), new WakeSignal(), TimeProvider.System);
        await new PublishStageWorker(environment.Store, environment.Store, environment.Builder, transitions, TimeProvider.System)
            .ExecuteAsync(recovered, CancellationToken.None);

        await using var db = await environment.Factory.CreateDbContextAsync();
        var record = await db.PipelineRecords.SingleAsync(value => value.Id == seed.Failed.Job.PipelineRecordId.Value);
        Assert.True(record.CompletionCriteriaMet);
        var publication = await db.DocumentPublications.SingleAsync(value => value.OwnerSourceRevisionId == seed.OwnerId);
        Assert.Equal(seed.ChildId, publication.DocumentInputSourceRevisionId);
        Assert.Equal(seed.BranchId, publication.SourceProcessorBranchId);
        Assert.Equal(record.Id, publication.PipelineRecordId);
        Assert.Equal(5, await db.Jobs.CountAsync(value => value.PipelineRecordId == record.Id && value.PublicState == (int)PublicJobState.Completed));
        Assert.Equal(1, await db.OutboxMessages.CountAsync(value => value.JobId == seed.Failed.Job.JobId.Value));
        Assert.Equal(seed.Failed.Job.AttemptCount + 1, recovered.Job.AttemptCount);
        Assert.Equal(0, await db.GpuMiniTasks.CountAsync()); // Synthetic Embed; Publish creates no GPU work.
        var chunk = await db.TextChunks.Include(value => value.Artifact).SingleAsync(value => value.Artifact.PipelineRecordId == record.Id);
        // Public citations identify the physical owner, while the pipeline retains its exact child.
        var source = await db.SourceRevisions.SingleAsync(value => value.Id == seed.OwnerId);
        var epoch = (await db.IndexState.SingleAsync()).CorpusEpoch;
        var identityHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(source.CanonicalPath)));
        var binding = new CorpusEvidenceBinding(1, source.SourceRootId, source.Id, identityHash, record.Id, record.Revision,
            chunk.ArtifactId, chunk.Artifact.ContentHash, chunk.Id, chunk.ContentHash, chunk.StartOffset, chunk.Length, epoch);
        var readback = await new SqlCorpusRetrievalReader(environment.Factory).ReadAsync(binding, 0, CancellationToken.None);
        Assert.NotNull(readback);
        Assert.Contains("Derived document recovery marker", readback.Text);
    }

    [NativeSqlServerTheory]
    [InlineData("owner-suppressed")]
    [InlineData("root-paused")]
    [InlineData("owner-bytes-changed")]
    [InlineData("child-artifact-changed")]
    [InlineData("member-changed")]
    [InlineData("branch-leased")]
    [InlineData("physical-owner-conflict")]
    [InlineData("competing-publication")]
    [InlineData("unknown-processor")]
    [InlineData("unknown-origin")]
    [InlineData("ambiguous-member")]
    [InlineData("native-uncertain")]
    [InlineData("prior-stage-failed")]
    [InlineData("prior-stage-leased")]
    public async Task Derived_document_recovery_refuses_changed_or_ambiguous_inputs_without_scheduling(string change)
    {
        await using var seed = await CreateDerivedFailureAsync("office");
        var command = Mutation(seed.Failed.Job.JobId.Value);
        var service = Service(seed.Environment);
        var preview = await service.PreviewAsync(command, "test", CancellationToken.None);
        await using (var db = await seed.Environment.Factory.CreateDbContextAsync())
        {
            var owner = await db.SourceRevisions.SingleAsync(value => value.Id == seed.OwnerId);
            var branch = await db.SourceProcessorBranches.SingleAsync(value => value.Id == seed.BranchId);
            switch (change)
            {
                case "owner-suppressed": owner.SuppressedAtUtc = DateTimeOffset.UtcNow; break;
                case "root-paused": (await db.SourceRootConfigurations.SingleAsync()).State = (int)SourceRootState.Paused; break;
                case "owner-bytes-changed": await db.SourceRevisions.Where(value => value.Id == owner.Id)
                    .ExecuteUpdateAsync(set => set.SetProperty(value => value.ContentSha256, new string('b', 64))); break;
                case "child-artifact-changed": await db.SourceArtifacts.Where(value => value.SourceRevisionId == seed.ChildId)
                    .ExecuteUpdateAsync(set => set.SetProperty(value => value.ContentSha256, new string('b', 64))); break;
                case "member-changed": await db.SourceProcessorBranchMembers.Where(value => value.BranchId == seed.BranchId)
                    .ExecuteUpdateAsync(set => set.SetProperty(value => value.ByteLength, value => value.ByteLength + 1)); break;
                case "branch-leased": branch.LeaseOwner = "other-worker"; branch.LeaseExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(1); break;
                case "physical-owner-conflict":
                    db.SourceRevisions.Add(new SourceRevisionEntity { Id = Guid.NewGuid(), SourceRootId = owner.SourceRootId,
                        StableSourceIdentity = owner.StableSourceIdentity, Revision = 2, ContentSha256 = owner.ContentSha256,
                        CanonicalPath = owner.CanonicalPath + ".other", Classification = owner.Classification, Extension = owner.Extension,
                        ByteLength = owner.ByteLength, DiscoveredAtUtc = DateTimeOffset.UtcNow });
                    break;
                case "competing-publication":
                    db.DocumentPublications.Add(new DocumentPublicationEntity { OwnerSourceRevisionId = owner.Id,
                        DocumentInputSourceRevisionId = seed.ChildId, SourceProcessorBranchId = branch.Id,
                        PipelineRecordId = (await db.PipelineRecords.FirstAsync(value => value.Id != seed.Failed.Job.PipelineRecordId.Value)).Id,
                        PipelineRecordRevision = 1, ProcessorFingerprint = branch.ProcessorFingerprint, PublishedAtUtc = DateTimeOffset.UtcNow });
                    break;
                case "unknown-processor": await db.SourceProcessorBranches.Where(value => value.Id == seed.BranchId)
                    .ExecuteUpdateAsync(set => set.SetProperty(value => value.ProcessorVersion, "unknown-v9")); break;
                case "unknown-origin": await db.SourceRevisions.Where(value => value.Id == seed.ChildId)
                    .ExecuteUpdateAsync(set => set.SetProperty(value => value.OriginKind, 1)); break;
                case "ambiguous-member":
                    db.SourceProcessorBranchMembers.Add(new SourceProcessorBranchMemberEntity { Id = Guid.NewGuid(), BranchId = branch.Id,
                        MemberFingerprint = new string('e', 64), Disposition = "completed", ByteLength = 1, CreatedAtUtc = DateTimeOffset.UtcNow });
                    break;
                case "native-uncertain":
                    db.GpuMiniTasks.Add(new GpuMiniTaskEntity { Id = Guid.NewGuid(), ParentJobId =
                        (await db.Jobs.SingleAsync(value => value.PipelineRecordId == seed.Failed.Job.PipelineRecordId.Value && value.Stage == (int)PipelineStage.Embed)).Id,
                        SourceRevision = 1, ModelRuntimeKey = "synthetic", SettingsFingerprint = "synthetic", IdempotencyKey = Guid.NewGuid().ToString("N"),
                        ExecutionState = (int)GpuMiniTaskExecutionState.OutcomeUncertain, CreatedAtUtc = DateTimeOffset.UtcNow });
                    break;
                case "prior-stage-failed":
                    (await db.Jobs.SingleAsync(value => value.PipelineRecordId == seed.Failed.Job.PipelineRecordId.Value && value.Stage == (int)PipelineStage.Normalise))
                        .PublicState = (int)PublicJobState.Failed;
                    break;
                case "prior-stage-leased":
                    (await db.Jobs.SingleAsync(value => value.PipelineRecordId == seed.Failed.Job.PipelineRecordId.Value && value.Stage == (int)PipelineStage.Normalise))
                        .LeaseOwner = "stale-or-live-owner";
                    break;
                default: throw new InvalidOperationException(change);
            }
            await db.SaveChangesAsync();
        }
        var before = await SnapshotAsync(seed.Environment, seed.Failed.Job.JobId.Value);

        await Assert.ThrowsAsync<NativeOperationException>(() => service.CommitAsync(command, preview.ConfirmationId,
            "derived-refusal-" + change, "test", CancellationToken.None).AsTask());
        await Assert.ThrowsAsync<NativeOperationException>(() => service.PreviewAsync(command, "test", CancellationToken.None).AsTask());

        Assert.Equal(before, await SnapshotAsync(seed.Environment, seed.Failed.Job.JobId.Value));
    }

    [NativeSqlServerTheory]
    [InlineData("metadata")]
    [InlineData("batch-active")]
    [InlineData("dispatch-unconfirmed")]
    [InlineData("capacity-owned")]
    public async Task Derived_document_recovery_binds_large_settled_native_metadata_without_exceeding_confirmation_limits(string change)
    {
        await using var seed = await CreateDerivedFailureAsync("office");
        var batchId = Guid.NewGuid();
        await using (var db = await seed.Environment.Factory.CreateDbContextAsync())
        {
            var embed = await db.Jobs.SingleAsync(value => value.PipelineRecordId == seed.Failed.Job.PipelineRecordId.Value && value.Stage == (int)PipelineStage.Embed);
            var now = DateTimeOffset.UtcNow;
            db.GpuCapacitySlots.Add(new GpuCapacitySlotEntity { SlotKey = "synthetic", UpdatedAtUtc = now });
            db.GpuBatches.Add(new GpuBatchEntity { Id = batchId, CapacitySlotKey = "synthetic", OwnerKey = "synthetic",
                ModelRuntimeKey = "synthetic", SettingsFingerprint = "synthetic", ItemCount = 130, AdmissionGeneration = 1,
                State = (int)GpuBatchState.Completed, CreatedAtUtc = now, UpdatedAtUtc = now });
            db.GpuExecutorDispatches.Add(new GpuExecutorDispatchEntity { DispatchId = Guid.NewGuid(), BatchId = batchId,
                CapacitySlotKey = "synthetic", OwnerKey = "synthetic", ExecutorKey = "synthetic", AdmissionGeneration = 1,
                State = (int)GpuExecutorDispatchState.Terminal, UpdatedAtUtc = now });
            db.GpuMiniTasks.AddRange(Enumerable.Range(0, 130).Select(_ => new GpuMiniTaskEntity
            {
                Id = Guid.NewGuid(), ParentJobId = embed.Id, SourceRevision = 1, BatchId = batchId,
                ModelRuntimeKey = "synthetic", SettingsFingerprint = "synthetic", IdempotencyKey = Guid.NewGuid().ToString("N"),
                AdmissionGeneration = 1, ExecutionState = (int)GpuMiniTaskExecutionState.Completed, CreatedAtUtc = now
            }));
            await db.SaveChangesAsync();
        }
        var command = Mutation(seed.Failed.Job.JobId.Value);
        var service = Service(seed.Environment);
        var stalePreview = await service.PreviewAsync(command, "test", CancellationToken.None);
        Assert.InRange(stalePreview.Targets.Count, 1, 128);
        await using (var db = await seed.Environment.Factory.CreateDbContextAsync())
        {
            switch (change)
            {
                case "metadata": (await db.GpuMiniTasks.FirstAsync(value => value.BatchId == batchId)).EstimatedBytes++; break;
                case "batch-active": (await db.GpuBatches.SingleAsync(value => value.Id == batchId)).State = (int)GpuBatchState.Active; break;
                case "dispatch-unconfirmed": (await db.GpuExecutorDispatches.SingleAsync(value => value.BatchId == batchId)).State = (int)GpuExecutorDispatchState.DeliveryUncertain; break;
                case "capacity-owned": (await db.GpuCapacitySlots.SingleAsync()).ActiveBatchId = batchId; break;
                default: throw new InvalidOperationException(change);
            }
            await db.SaveChangesAsync();
        }
        await Assert.ThrowsAsync<NativeOperationException>(() => service.CommitAsync(command, stalePreview.ConfirmationId,
            "changed-native-binding", "test", CancellationToken.None).AsTask());
        if (change != "metadata")
        {
            await Assert.ThrowsAsync<NativeOperationException>(() => service.PreviewAsync(command, "test", CancellationToken.None).AsTask());
            return;
        }
        var preview = await service.PreviewAsync(command, "test", CancellationToken.None);
        await service.CommitAsync(command, preview.ConfirmationId, "current-native-binding", "test", CancellationToken.None);
        await using var verification = await seed.Environment.Factory.CreateDbContextAsync();
        Assert.Equal(130, await verification.GpuMiniTasks.CountAsync(value => value.BatchId == batchId && value.ExecutionState == (int)GpuMiniTaskExecutionState.Completed));
        Assert.Equal((int)PublicJobState.WorkerQueued, (await verification.Jobs.SingleAsync(value => value.Id == seed.Failed.Job.JobId.Value)).PublicState);
    }

    private async Task<DerivedFailure> CreateDerivedFailureAsync(string kind)
    {
        await SqlTestData.ClearPhase3SourceDataAsync(fixture);
        var environment = await SqlToUsearchRebuildTests.PipelineEnvironment.CreateAsync(fixture, "Unrooted control.", publish: false);
        try
        {
            var recordId = await environment.AddRetainedAndPumpAsync("Derived document recovery marker has current cited evidence.");
            var failed = await FailPublishAsync(environment, recordId);
            await using var db = await environment.Factory.CreateDbContextAsync();
            var record = await db.PipelineRecords.SingleAsync(value => value.Id == recordId);
            var child = await db.SourceRevisions.SingleAsync(value => value.Id == record.SourceRevisionId);
            var childActivity = await db.SourceActivities.SingleAsync(value => value.SourceRevisionId == child.Id);
            var artifact = await db.SourceArtifacts.SingleAsync(value => value.SourceRevisionId == child.Id);
            var ownerId = Guid.NewGuid();
            var parentActivityId = Guid.NewGuid();
            var branchId = Guid.NewGuid();
            var pdf = kind == "pdf";
            var descriptor = pdf ? PdfDocumentProcessor.Capability : OoxmlStructuralTextProcessor.Capability;
            var version = kind == "office-v3" ? OoxmlStructuralTextProcessor.Version3ProcessorVersion : descriptor.ProcessorVersion;
            var fingerprint = kind == "office-v3" ? OoxmlStructuralTextProcessor.Version3ProcessorFingerprint : descriptor.ProcessorFingerprint;
            var now = DateTimeOffset.UtcNow;
            db.SourceRevisions.Add(new SourceRevisionEntity { Id = ownerId, SourceRootId = child.SourceRootId,
                StableSourceIdentity = "physical-owner:" + ownerId.ToString("N"), Revision = 1, ContentSha256 = child.ContentSha256,
                CanonicalPath = (await db.SourceRootConfigurations.SingleAsync()).CanonicalPath + "\\owner" + (pdf ? ".pdf" : ".docx"),
                Classification = pdf ? "PdfDocumentContainer" : "OoxmlDocumentContainer", Extension = pdf ? ".pdf" : ".docx",
                ByteLength = child.ByteLength, DiscoveredAtUtc = now });
            db.SourceArtifacts.Add(new SourceArtifactEntity { Id = Guid.NewGuid(), SourceRevisionId = ownerId,
                ContentSha256 = artifact.ContentSha256, StoreRelativePath = artifact.StoreRelativePath,
                ByteLength = artifact.ByteLength, ChecksumVerifiedAtUtc = now, ReferenceCount = 1 });
            db.SourceActivities.Add(new SourceActivityEntity { Id = parentActivityId, SourceRevisionId = ownerId,
                ActivityKind = (int)SourceActivityKind.TextExtraction, ExecutionClass = (int)ExecutionClass.InProcess,
                ProcessorVersion = version, InputFingerprint = child.ContentSha256, DescriptorFingerprint = fingerprint,
                State = (int)SourceActivityState.Pending, CreatedAtUtc = now, UpdatedAtUtc = now });
            db.SourceProcessorBranches.Add(new SourceProcessorBranchEntity { Id = branchId, SourceActivityId = parentActivityId,
                SourceRevisionId = ownerId, InputSha256 = child.ContentSha256, ProcessorVersion = version, ProcessorFingerprint = fingerprint,
                State = (int)RetainedProcessorBranchState.Completed, CompletedMemberCount = 1,
                CompletionReceiptFingerprint = new string('f', 64), CreatedAtUtc = now, UpdatedAtUtc = now });
            db.SourceProcessorBranchMembers.Add(new SourceProcessorBranchMemberEntity { Id = Guid.NewGuid(), BranchId = branchId,
                MemberFingerprint = new string('d', 64), ChildSourceRevisionId = child.Id, ChildSourceActivityId = childActivity.Id,
                Disposition = "completed", ByteLength = child.ByteLength, CreatedAtUtc = now });
            if (!pdf && !await db.SourceCapabilities.AnyAsync(value => value.Id == descriptor.Id))
                db.SourceCapabilities.Add(new SourceCapabilityEntity { Id = descriptor.Id, ProcessorKind = descriptor.ProcessorKind,
                    ProcessorVersion = descriptor.ProcessorVersion, ProcessorFingerprint = descriptor.ProcessorFingerprint,
                    ExecutionClass = (int)ExecutionClass.InProcess, OutputContract = descriptor.OutputContract,
                    IsRunnable = true, RegisteredBy = "test", RegisteredAtUtc = now });
            await db.SaveChangesAsync();
            // Arrange only the retained lineage metadata after synthetic extraction/Embed.
            // No document parser or real model is part of this publication recovery fixture.
            await db.SourceRevisions.Where(value => value.Id == child.Id).ExecuteUpdateAsync(set => set
                .SetProperty(value => value.ParentSourceRevisionId, ownerId)
                .SetProperty(value => value.OriginKind, 2)
                .SetProperty(value => value.Classification, pdf ? DocumentProcessingInput.Pdf.Classification : "AcceptedUtf8Text")
                .SetProperty(value => value.Extension, pdf ? ".pdf" : ".txt"));
            if (pdf)
                await db.SourceActivities.Where(value => value.Id == childActivity.Id).ExecuteUpdateAsync(set => set
                    .SetProperty(value => value.ActivityKind, (int)SourceActivityKind.DocumentParsing)
                    .SetProperty(value => value.ProcessorVersion, DocumentProcessingInput.Pdf.ProcessorVersion)
                    .SetProperty(value => value.DescriptorFingerprint, DocumentProcessingInput.Pdf.ProcessorFingerprint));
            return new(environment, failed, ownerId, child.Id, branchId);
        }
        catch { await environment.DisposeAsync(); throw; }
    }

    private sealed record DerivedFailure(SqlToUsearchRebuildTests.PipelineEnvironment Environment, StageWorkItem Failed,
        Guid OwnerId, Guid ChildId, Guid BranchId) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => Environment.DisposeAsync();
    }
}
