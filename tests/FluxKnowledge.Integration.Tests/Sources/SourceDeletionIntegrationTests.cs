using FluxKnowledge.Application.Sources;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Documents;
using FluxKnowledge.Application.Contracts;
using FluxKnowledge.Application.IntegrationV1;
using FluxKnowledge.Application.IntegrationV1.Corpus;
using FluxKnowledge.Infrastructure.SqlServer.Visibility;
using System.Text.Json;
using FluxKnowledge.Domain.Gpu;
using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using FluxKnowledge.Infrastructure.SqlServer.Workers;
using FluxKnowledge.Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Sources;

/// <summary>Source deletion is scoped to one root; a second root is the control for every purge assertion.</summary>
public sealed class SourceDeletionIntegrationTests(NativeSqlServerFixture fixture)
    : IClassFixture<NativeSqlServerFixture>, IAsyncLifetime
{
    private readonly NativeSqlServerFixture _fixture = fixture;

    public async Task InitializeAsync()
    {
        await using (var context = CreateContext())
        {
            await context.SourceDeletionCleanupItems.ExecuteDeleteAsync();
            await context.SourceDeletionOperations.ExecuteDeleteAsync();
        }

        await SqlTestData.ClearPhase3SourceDataAsync(_fixture);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [NativeSqlServerFact]
    public async Task Claim_next_allows_an_empty_queue_when_sql_retry_is_enabled()
    {
        var store = new SqlSourceDeletionStore(new RetryingContextFactory(_fixture.ConnectionString), TimeProvider.System);

        Assert.Null(await store.ClaimNextAsync(CancellationToken.None));
    }

    [NativeSqlServerFact]
    public async Task Recording_a_completed_cleanup_item_allows_sql_retry_strategy()
    {
        var now = DateTimeOffset.UtcNow;
        var deleting = await SeedRootAsync("retry-cleanup-receipt", SourceRootState.Deleting, now);
        var operationId = Guid.NewGuid();
        var cleanupItemId = Guid.NewGuid();
        var leaseId = Guid.NewGuid();
        await using (var setup = CreateContext())
        {
            setup.SourceDeletionOperations.Add(new SourceDeletionOperationEntity
            {
                Id = operationId,
                SourceRootId = deleting.RootId,
                State = 1,
                LeaseId = leaseId,
                LeaseExpiresAtUtc = now.AddMinutes(5),
                Phase = "cleanup-files",
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            });
            setup.SourceDeletionCleanupItems.Add(new SourceDeletionCleanupItemEntity
            {
                Id = cleanupItemId,
                SourceDeletionOperationId = operationId,
                StorageKind = 2,
                RelativePath = Guid.NewGuid().ToString("N"),
                State = 0,
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            });
            await setup.SaveChangesAsync();
        }

        var store = new SqlSourceDeletionStore(new RetryingContextFactory(_fixture.ConnectionString), TimeProvider.System);
        await store.RecordCleanupResultAsync(
            new SourceDeletionWorkItem(operationId, deleting.RootId, leaseId, "cleanup-files"),
            cleanupItemId,
            new SourceDeletionFileResult(true),
            CancellationToken.None);

        await using var verification = CreateContext();
        Assert.Equal(1, await verification.SourceDeletionCleanupItems
            .Where(item => item.Id == cleanupItemId)
            .Select(item => item.State)
            .SingleAsync());
        Assert.Equal(0, await verification.SourceDeletionOperations
            .Where(operation => operation.Id == operationId)
            .Select(operation => operation.State)
            .SingleAsync());
    }

    [NativeSqlServerFact]
    public async Task Coordinator_purges_only_the_deleting_root_and_retains_the_terminal_operation_receipt()
    {
        var now = DateTimeOffset.Parse("2026-09-18T11:00:00+00:00");
        var deleting = await SeedRootAsync("deleting", SourceRootState.Deleting, now);
        var control = await SeedRootAsync("control", SourceRootState.Enabled, now);
        var operationId = Guid.NewGuid();
        await using (var setup = CreateContext())
        {
            setup.SourceDeletionOperations.Add(new SourceDeletionOperationEntity
            {
                Id = operationId,
                SourceRootId = deleting.RootId,
                State = 0,
                Phase = "accepted",
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            });
            await setup.SaveChangesAsync();
        }

        var coordinator = new SourceDeletionCoordinator(new SqlSourceDeletionStore(
            SqlTestData.CreateFactory(_fixture), TimeProvider.System));

        Assert.True(await coordinator.RunOneAsync(CancellationToken.None));

        await using var verification = CreateContext();
        Assert.DoesNotContain(await verification.SourceRootConfigurations.ToListAsync(), row => row.Id == deleting.RootId);
        Assert.DoesNotContain(await verification.SourceRevisions.ToListAsync(), row => row.SourceRootId == deleting.RootId);
        Assert.DoesNotContain(await verification.PipelineRecords.ToListAsync(), row => row.SourceRevisionId == deleting.RevisionId);
        Assert.DoesNotContain(await verification.Jobs.ToListAsync(), row => row.PipelineRecordId == deleting.RecordId);
        Assert.DoesNotContain(await verification.OutboxMessages.ToListAsync(), row => row.PipelineRecordId == deleting.RecordId);
        Assert.Contains(await verification.SourceRootConfigurations.ToListAsync(), row => row.Id == control.RootId);
        Assert.Contains(await verification.PipelineRecords.ToListAsync(), row => row.Id == control.RecordId);
        var receipt = await verification.SourceDeletionOperations.SingleAsync(row => row.Id == operationId);
        Assert.Equal("completed", receipt.Phase);
        Assert.Equal(3, receipt.State);
        Assert.Null(receipt.ReasonCode);
    }

    [NativeSqlServerFact]
    public async Task Coordinator_removes_a_document_publication_before_its_owned_branch_record_and_revisions()
    {
        var now = DateTimeOffset.Parse("2026-09-18T11:02:00+00:00");
        var deleting = await SeedRootAsync("document-publication", SourceRootState.Deleting, now);
        var operationId = Guid.NewGuid();
        var documentInputRevisionId = Guid.NewGuid();
        var documentIdentityId = Guid.NewGuid();
        var documentRecordId = Guid.NewGuid();
        var ownerActivityId = Guid.NewGuid();
        var documentActivityId = Guid.NewGuid();
        var branchId = Guid.NewGuid();
        await using (var setup = CreateContext())
        {
            setup.SourceDeletionOperations.Add(new SourceDeletionOperationEntity
            {
                Id = operationId, SourceRootId = deleting.RootId, State = 0, Phase = "accepted",
                CreatedAtUtc = now, UpdatedAtUtc = now
            });
            setup.SourceIdentities.Add(new SourceIdentityEntity
            {
                Id = documentIdentityId, SourceKind = "retained local source", StableKey = "document-input",
                CreatedAtUtc = now
            });
            setup.SourceRevisions.Add(new SourceRevisionEntity
            {
                Id = documentInputRevisionId, SourceRootId = deleting.RootId, StableSourceIdentity = "document-input",
                Revision = 1, ContentSha256 = new string('a', 64),
                CanonicalPath = $"C:\\retained\\{documentInputRevisionId:N}.vsdx", ParentSourceRevisionId = deleting.RevisionId,
                Classification = "DocumentProcessingInput", Extension = ".vsdx", OriginKind = 2, ByteLength = 4,
                DiscoveredAtUtc = now
            });
            setup.PipelineRecords.Add(new PipelineRecordEntity
            {
                Id = documentRecordId, SourceIdentityId = documentIdentityId, SourceRevisionId = documentInputRevisionId,
                Revision = 1, ContentHash = new string('a', 64), RootLineageRecordId = documentRecordId,
                CurrentStage = 3, RegisteredAtUtc = now
            });
            setup.SourceActivities.AddRange(
                new SourceActivityEntity
                {
                    Id = ownerActivityId, SourceRevisionId = deleting.RevisionId,
                    ActivityKind = (int)SourceActivityKind.ArchiveExpansion, ExecutionClass = 1,
                    ProcessorVersion = "phase-6-vsdx-structural-v1", InputFingerprint = new string('a', 64),
                    State = (int)SourceActivityState.Completed, CreatedAtUtc = now, UpdatedAtUtc = now
                },
                new SourceActivityEntity
                {
                    Id = documentActivityId, SourceRevisionId = documentInputRevisionId,
                    ActivityKind = (int)SourceActivityKind.DocumentParsing, ExecutionClass = 1,
                    ProcessorVersion = "phase-6-vsdx-document-v1", InputFingerprint = new string('a', 64),
                    State = (int)SourceActivityState.Completed, ResultingPipelineRecordId = documentRecordId,
                    ResultingPipelineRecordRevision = 1, CreatedAtUtc = now, UpdatedAtUtc = now
                });
            setup.SourceProcessorBranches.Add(new SourceProcessorBranchEntity
            {
                Id = branchId, SourceActivityId = ownerActivityId, SourceRevisionId = deleting.RevisionId,
                InputSha256 = new string('a', 64), ProcessorVersion = "phase-6-vsdx-structural-v1",
                ProcessorFingerprint = "phase-6-vsdx-retained-structural-v1",
                State = (int)RetainedProcessorBranchState.Completed, CreatedAtUtc = now, UpdatedAtUtc = now
            });
            setup.SourceProcessorBranchMembers.Add(new SourceProcessorBranchMemberEntity
            {
                Id = Guid.NewGuid(), BranchId = branchId, MemberFingerprint = new string('b', 64),
                ChildSourceRevisionId = documentInputRevisionId, ChildSourceActivityId = documentActivityId,
                Disposition = "completed", ByteLength = 4, CreatedAtUtc = now
            });
            setup.DocumentPublications.Add(new DocumentPublicationEntity
            {
                OwnerSourceRevisionId = deleting.RevisionId, DocumentInputSourceRevisionId = documentInputRevisionId,
                SourceProcessorBranchId = branchId, PipelineRecordId = documentRecordId, PipelineRecordRevision = 1,
                ProcessorFingerprint = "phase-6-vsdx-retained-structural-v1", PublishedAtUtc = now
            });
            await setup.SaveChangesAsync();
        }

        var coordinator = new SourceDeletionCoordinator(new SqlSourceDeletionStore(
            SqlTestData.CreateFactory(_fixture), TimeProvider.System));

        Assert.True(await coordinator.RunOneAsync(CancellationToken.None));

        await using var verification = CreateContext();
        Assert.Null(await verification.SourceRootConfigurations.SingleOrDefaultAsync(value => value.Id == deleting.RootId));
        Assert.DoesNotContain(await verification.DocumentPublications.ToListAsync(), value =>
            value.OwnerSourceRevisionId == deleting.RevisionId || value.DocumentInputSourceRevisionId == documentInputRevisionId);
        Assert.DoesNotContain(await verification.SourceProcessorBranches.ToListAsync(), value => value.Id == branchId);
        Assert.DoesNotContain(await verification.PipelineRecords.ToListAsync(), value => value.Id == documentRecordId);
        Assert.Equal("completed", await verification.SourceDeletionOperations.Where(value => value.Id == operationId)
            .Select(value => value.Phase).SingleAsync());
    }

    [NativeSqlServerFact]
    public async Task Coordinator_clears_the_active_generation_before_removing_the_final_source()
    {
        var now = DateTimeOffset.Parse("2026-09-18T11:05:00+00:00");
        var deleting = await SeedRootAsync("final-source", SourceRootState.Deleting, now);
        var operationId = Guid.NewGuid();
        var generationId = Guid.NewGuid();
        await using (var setup = CreateContext())
        {
            setup.SourceDeletionOperations.Add(new SourceDeletionOperationEntity
            {
                Id = operationId,
                SourceRootId = deleting.RootId,
                State = 0,
                Phase = "accepted",
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            });
            setup.IndexGenerations.Add(new IndexGenerationEntity
            {
                Id = generationId,
                ModelFingerprint = "source-deletion-test:1",
                Dimensions = 1,
                IndexPath = "final-source-generation",
                MetadataChecksum = new string('a', 64),
                VectorCount = 1,
                CreatedAtUtc = now,
                ValidatedAtUtc = now
            });
            await setup.SaveChangesAsync();
        }

        var vector = await AddVectorAsync(deleting, generationId, "final source", now);
        await using (var setup = CreateContext())
        {
            setup.IndexGenerationVectors.Add(new IndexGenerationVectorEntity
            {
                GenerationId = generationId,
                VectorId = vector.VectorId
            });
            var indexState = await setup.IndexState.SingleAsync(value => value.Id == 1);
            indexState.ActiveIndexGenerationId = generationId;
            indexState.EmptyCatalogueValidatedAtUtc = null;
            await setup.SaveChangesAsync();
        }

        var coordinator = new SourceDeletionCoordinator(
            new SqlSourceDeletionStore(SqlTestData.CreateFactory(_fixture), TimeProvider.System),
            fileStore: new SuccessfulFileStore());

        Assert.True(await coordinator.RunOneAsync(CancellationToken.None));

        await using var verification = CreateContext();
        Assert.Null(await verification.SourceRootConfigurations.SingleOrDefaultAsync(value => value.Id == deleting.RootId));
        Assert.Null((await verification.IndexState.SingleAsync(value => value.Id == 1)).ActiveIndexGenerationId);
        Assert.Empty(await verification.IndexGenerations.ToListAsync());
        Assert.Equal("completed", await verification.SourceDeletionOperations
            .Where(value => value.Id == operationId)
            .Select(value => value.Phase)
            .SingleAsync());
    }

    [NativeSqlServerFact]
    public async Task Coordinator_allows_the_exact_deleting_root_to_remove_immutable_csharp_facts()
    {
        var now = DateTimeOffset.Parse("2026-09-18T11:15:00+00:00");
        var deleting = await SeedRootAsync("csharp", SourceRootState.Deleting, now);
        var operationId = Guid.NewGuid();
        var branchId = Guid.NewGuid();
        var hash = new string('c', 64);
        await using (var setup = CreateContext())
        {
            setup.SourceDeletionOperations.Add(new SourceDeletionOperationEntity
            {
                Id = operationId,
                SourceRootId = deleting.RootId,
                State = 0,
                Phase = "accepted",
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            });
            var activityId = Guid.NewGuid();
            setup.SourceActivities.Add(new SourceActivityEntity
            {
                Id = activityId,
                SourceRevisionId = deleting.RevisionId,
                ActivityKind = (int)SourceActivityKind.CodeParsing,
                ExecutionClass = (int)ExecutionClass.InProcess,
                ProcessorVersion = "source-deletion-test",
                InputFingerprint = hash,
                State = (int)SourceActivityState.Completed,
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            });
            setup.SourceProcessorBranches.Add(new SourceProcessorBranchEntity
            {
                Id = branchId,
                SourceActivityId = activityId,
                SourceRevisionId = deleting.RevisionId,
                InputSha256 = hash,
                ProcessorVersion = "source-deletion-test",
                ProcessorFingerprint = new string('a', 64),
                State = (int)RetainedProcessorBranchState.Completed,
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            });
            setup.SourceProcessorCodeDocuments.Add(new SourceProcessorCodeDocumentEntity
            {
                SourceProcessorBranchId = branchId,
                SourceRevisionId = deleting.RevisionId,
                RetainedArtifactSha256 = hash,
                DescriptorFingerprint = new string('b', 64),
                ParserFingerprint = new string('d', 64),
                HandlerImplementationId = "source-deletion-test",
                DecodedCharacterCount = 1,
                LineCount = 1,
                SymbolCount = 1,
                DocumentFingerprint = new string('e', 64),
                CompletionFingerprint = new string('f', 64)
            });
            setup.SourceProcessorCodeSymbols.Add(new SourceProcessorCodeSymbolEntity
            {
                DocumentId = branchId,
                Ordinal = 0,
                DeclarationKindCode = 1,
                LocalName = "Symbol",
                QualifiedName = "Symbol",
                RenderedSignature = "Symbol",
                Modifiers = "public",
                LexicalParentOrdinal = -1,
                SpanStartUtf16 = 0,
                SpanLengthUtf16 = 1,
                SymbolFingerprint = new string('e', 64)
            });
            await setup.SaveChangesAsync();
        }

        var coordinator = new SourceDeletionCoordinator(new SqlSourceDeletionStore(SqlTestData.CreateFactory(_fixture), TimeProvider.System));

        Assert.True(await coordinator.RunOneAsync(CancellationToken.None));

        await using var verification = CreateContext();
        Assert.Empty(await verification.SourceProcessorCodeDocuments.ToListAsync());
        Assert.Empty(await verification.SourceProcessorCodeSymbols.ToListAsync());
        Assert.Equal("completed", (await verification.SourceDeletionOperations.SingleAsync(row => row.Id == operationId)).Phase);
    }

    [NativeSqlServerFact]
    public async Task Coordinator_refuses_any_gpu_owned_source_before_removing_local_state()
    {
        var now = DateTimeOffset.Parse("2026-09-18T11:30:00+00:00");
        var deleting = await SeedRootAsync("gpu-owned", SourceRootState.Deleting, now);
        var operationId = Guid.NewGuid();
        await using (var setup = CreateContext())
        {
            setup.SourceDeletionOperations.Add(new SourceDeletionOperationEntity
            {
                Id = operationId,
                SourceRootId = deleting.RootId,
                State = 0,
                Phase = "accepted",
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            });
            setup.GpuMiniTasks.Add(new GpuMiniTaskEntity
            {
                Id = Guid.NewGuid(),
                ParentJobId = deleting.JobId,
                SourceRevision = 1,
                PriorityLane = 0,
                ModelRuntimeKey = "test-runtime",
                SettingsFingerprint = "test-settings",
                EstimatedBytes = 1,
                IdempotencyKey = $"source-delete:{deleting.RootId:N}",
                ExecutionState = 3,
                CreatedAtUtc = now
            });
            await setup.SaveChangesAsync();
        }

        var coordinator = new SourceDeletionCoordinator(new SqlSourceDeletionStore(SqlTestData.CreateFactory(_fixture), TimeProvider.System));

        Assert.False(await coordinator.RunOneAsync(CancellationToken.None));

        await using var verification = CreateContext();
        Assert.NotNull(await verification.SourceRootConfigurations.SingleOrDefaultAsync(row => row.Id == deleting.RootId));
        Assert.NotNull(await verification.Jobs.SingleOrDefaultAsync(row => row.Id == deleting.JobId));
        var operation = await verification.SourceDeletionOperations.SingleAsync(row => row.Id == operationId);
        Assert.Equal(4, operation.State);
        Assert.Equal("attention", operation.Phase);
        Assert.Equal("source-delete-external-execution-owned", operation.ReasonCode);
    }

    [NativeSqlServerTheory]
    [InlineData("matching")]
    [InlineData("missing")]
    [InlineData("other-source")]
    [InlineData("other-runtime")]
    public async Task Public_delete_accepts_only_exact_source_bound_local_ocr_work(string binding)
    {
        var now = DateTimeOffset.UtcNow;
        var deleting = await SeedRootAsync("public-ocr-deleting", SourceRootState.Enabled, now);
        var control = await SeedRootAsync("public-ocr-control", SourceRootState.Enabled, now);
        var taskId = Guid.NewGuid();
        await using (var setup = CreateContext())
        {
            AddLocalOcrTask(setup, deleting, taskId, GpuMiniTaskExecutionState.Completed, now);
            AddLocalOcrTask(setup, control, Guid.NewGuid(), GpuMiniTaskExecutionState.Completed, now);
            var request = setup.DocumentOcrRequests.Local.Single(value => value.MiniTaskId == taskId);
            if (binding == "missing") setup.DocumentOcrRequests.Remove(request);
            if (binding == "other-runtime") request.ModelRuntimeKey = "unknown-runtime";
            if (binding == "other-source")
            {
                request.PipelineRecordId = control.RecordId;
                request.RetainedSourceRevisionId = control.RevisionId;
            }
            await setup.SaveChangesAsync();
        }
        var factory = SqlTestData.CreateFactory(_fixture);
        var service = new NativeCorpusCommandService(
            new SqlNativeOperationStore(factory, TimeProvider.System),
            new SqlNativeCorpusActionStore(factory, new NoRootCreationPolicy(), new LocalPrivateContentDisclosure()));
        var mutation = new NativeCorpusMutation("root_delete", JsonSerializer.SerializeToElement(new { rootId = deleting.RootId }));
        var preview = await service.PreviewAsync(mutation, "test", CancellationToken.None);
        if (binding == "matching")
        {
            var receipt = await service.CommitAsync(mutation, preview.ConfirmationId, $"delete-ocr:{deleting.RootId:N}", "test", CancellationToken.None);
            Assert.Equal("completed", receipt.Outcome);
            Assert.True(await new SourceDeletionCoordinator(new SqlSourceDeletionStore(factory, TimeProvider.System)).RunOneAsync(CancellationToken.None));
        }
        else
        {
            var failure = await Assert.ThrowsAsync<NativeOperationException>(() => service.CommitAsync(
                mutation, preview.ConfirmationId, $"delete-ocr:{deleting.RootId:N}", "test", CancellationToken.None).AsTask());
            Assert.Equal("source-delete-external-execution-owned", failure.ReasonCode);
        }
        await using var verification = CreateContext();
        Assert.Equal(binding != "matching", await verification.SourceRootConfigurations.AnyAsync(value => value.Id == deleting.RootId));
        Assert.Equal(binding != "matching", await verification.GpuMiniTasks.AnyAsync(value => value.Id == taskId));
        Assert.True(await verification.SourceRootConfigurations.AnyAsync(value => value.Id == control.RootId));
        Assert.True(await verification.DocumentOcrRequests.AnyAsync(value => value.PipelineRecordId == control.RecordId));
        Assert.True(await verification.GpuMiniTasks.AnyAsync(value => value.ParentJobId == control.JobId));
    }

    private sealed class NoRootCreationPolicy : ISourceRootPathPolicy
    {
        public SourceRootPathValidation ValidateAndCanonicalise(SourceRootCreateRequest request) => throw new NotSupportedException();
    }

    [NativeSqlServerFact]
    public async Task Coordinator_purges_only_completed_local_ocr_execution_owned_by_the_deleting_source()
    {
        var now = DateTimeOffset.Parse("2026-09-20T15:30:00+00:00");
        var deleting = await SeedRootAsync("local-ocr-deleting", SourceRootState.Deleting, now);
        var control = await SeedRootAsync("local-ocr-control", SourceRootState.Enabled, now);
        var operationId = Guid.NewGuid();
        var deletingTaskId = Guid.NewGuid();
        var orphanedDeletingRequestId = Guid.NewGuid();
        var controlTaskId = Guid.NewGuid();
        await using (var setup = CreateContext())
        {
            setup.SourceDeletionOperations.Add(new SourceDeletionOperationEntity
            {
                Id = operationId,
                SourceRootId = deleting.RootId,
                State = 0,
                Phase = "accepted",
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            });
            AddLocalOcrTask(setup, deleting, deletingTaskId, GpuMiniTaskExecutionState.Completed, now);
            AddLocalOcrRequest(setup, deleting, orphanedDeletingRequestId, now);
            AddLocalOcrTask(setup, control, controlTaskId, GpuMiniTaskExecutionState.Completed, now);
            await setup.SaveChangesAsync();
        }

        var coordinator = new SourceDeletionCoordinator(
            new SqlSourceDeletionStore(SqlTestData.CreateFactory(_fixture), TimeProvider.System));

        Assert.True(await coordinator.RunOneAsync(CancellationToken.None));

        await using var verification = CreateContext();
        Assert.Null(await verification.SourceRootConfigurations.SingleOrDefaultAsync(value => value.Id == deleting.RootId));
        Assert.Null(await verification.DocumentOcrRequests.SingleOrDefaultAsync(value => value.MiniTaskId == deletingTaskId));
        Assert.Null(await verification.DocumentOcrRequests.SingleOrDefaultAsync(value => value.MiniTaskId == orphanedDeletingRequestId));
        Assert.Null(await verification.GpuMiniTasks.SingleOrDefaultAsync(value => value.Id == deletingTaskId));
        Assert.NotNull(await verification.SourceRootConfigurations.SingleOrDefaultAsync(value => value.Id == control.RootId));
        Assert.NotNull(await verification.DocumentOcrRequests.SingleOrDefaultAsync(value => value.MiniTaskId == controlTaskId));
        Assert.NotNull(await verification.GpuMiniTasks.SingleOrDefaultAsync(value => value.Id == controlTaskId));
        Assert.Equal("completed", await verification.SourceDeletionOperations.Where(value => value.Id == operationId)
            .Select(value => value.Phase).SingleAsync());
    }

    [NativeSqlServerFact]
    public async Task Coordinator_requests_cooperative_stop_before_deleting_an_active_local_ocr_execution()
    {
        var now = DateTimeOffset.Parse("2026-09-20T15:35:00+00:00");
        var deleting = await SeedRootAsync("local-ocr-active", SourceRootState.Deleting, now);
        var operationId = Guid.NewGuid();
        var miniTaskId = Guid.NewGuid();
        await using (var setup = CreateContext())
        {
            setup.SourceDeletionOperations.Add(new SourceDeletionOperationEntity
            {
                Id = operationId,
                SourceRootId = deleting.RootId,
                State = 0,
                Phase = "accepted",
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            });
            AddLocalOcrTask(setup, deleting, miniTaskId, GpuMiniTaskExecutionState.Active, now);
            await setup.SaveChangesAsync();
        }

        var executions = new PaddleOcrVlmExecutionRegistry();
        using var execution = executions.Begin(miniTaskId);
        var coordinator = new SourceDeletionCoordinator(new SqlSourceDeletionStore(
            SqlTestData.CreateFactory(_fixture), TimeProvider.System, executions));

        Assert.False(await coordinator.RunOneAsync(CancellationToken.None));
        Assert.True(execution.IsCancellationRequested);

        await using var verification = CreateContext();
        Assert.NotNull(await verification.SourceRootConfigurations.SingleOrDefaultAsync(value => value.Id == deleting.RootId));
        Assert.NotNull(await verification.DocumentOcrRequests.SingleOrDefaultAsync(value => value.MiniTaskId == miniTaskId));
        Assert.NotNull(await verification.GpuMiniTasks.SingleOrDefaultAsync(value => value.Id == miniTaskId));
        var operation = await verification.SourceDeletionOperations.SingleAsync(value => value.Id == operationId);
        Assert.Equal(0, operation.State);
        Assert.Equal("draining", operation.Phase);
    }

    [NativeSqlServerFact]
    public async Task Expired_deletion_claim_is_recovered_and_the_previous_worker_cannot_fail_the_new_owner()
    {
        var now = DateTimeOffset.UtcNow;
        var deleting = await SeedRootAsync("expired-lease", SourceRootState.Deleting, now);
        var operationId = Guid.NewGuid();
        var expiredLeaseId = Guid.NewGuid();
        await using (var setup = CreateContext())
        {
            setup.SourceDeletionOperations.Add(new SourceDeletionOperationEntity
            {
                Id = operationId,
                SourceRootId = deleting.RootId,
                State = 1,
                LeaseId = expiredLeaseId,
                LeaseExpiresAtUtc = now.AddMinutes(-1),
                Phase = "accepted",
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            });
            await setup.SaveChangesAsync();
        }

        var store = new SqlSourceDeletionStore(SqlTestData.CreateFactory(_fixture), TimeProvider.System);
        var recovered = await store.ClaimNextAsync(CancellationToken.None);
        Assert.NotNull(recovered);
        Assert.Equal(operationId, recovered.OperationId);
        Assert.NotEqual(expiredLeaseId, recovered.LeaseId);
        await store.FailAsync(
            new SourceDeletionWorkItem(operationId, deleting.RootId, expiredLeaseId, "accepted"),
            "stale-worker",
            CancellationToken.None);

        await using var verification = CreateContext();
        var operation = await verification.SourceDeletionOperations.SingleAsync(row => row.Id == operationId);
        Assert.Equal(1, operation.State);
        Assert.Equal(recovered.LeaseId, operation.LeaseId);
        Assert.NotEqual("stale-worker", operation.ReasonCode);
    }

    [NativeSqlServerFact]
    public async Task Cleanup_rechecks_a_late_shared_artifact_before_removing_its_physical_target()
    {
        var now = DateTimeOffset.UtcNow;
        var deleting = await SeedRootAsync("late-shared-artifact", SourceRootState.Deleting, now);
        var control = await SeedRootAsync("late-shared-control", SourceRootState.Enabled, now);
        var operationId = Guid.NewGuid();
        var contentSha256 = new string('b', 64);
        var relativePath = $"sha256\\{contentSha256[..2]}\\{contentSha256}.bin";
        await using (var setup = CreateContext())
        {
            setup.SourceDeletionOperations.Add(new SourceDeletionOperationEntity
            {
                Id = operationId,
                SourceRootId = deleting.RootId,
                State = 0,
                Phase = "accepted",
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            });
            setup.SourceArtifacts.Add(new SourceArtifactEntity
            {
                Id = Guid.NewGuid(),
                SourceRevisionId = deleting.RevisionId,
                ContentSha256 = contentSha256,
                StoreRelativePath = relativePath,
                ByteLength = 4,
                ChecksumVerifiedAtUtc = now,
                ReferenceCount = 1
            });
            await setup.SaveChangesAsync();
        }

        var store = new SqlSourceDeletionStore(SqlTestData.CreateFactory(_fixture), TimeProvider.System);
        var accepted = Assert.IsType<SourceDeletionWorkItem>(await store.ClaimNextAsync(CancellationToken.None));
        Assert.True((await store.PurgeAsync(accepted, survivorGeneration: null, CancellationToken.None)).ContinueImmediately);
        var rebuilding = Assert.IsType<SourceDeletionWorkItem>(await store.ClaimNextAsync(CancellationToken.None));
        Assert.True((await store.PurgeAsync(rebuilding, survivorGeneration: null, CancellationToken.None)).ContinueImmediately);

        await using (var writer = CreateContext())
        {
            writer.SourceArtifacts.Add(new SourceArtifactEntity
            {
                Id = Guid.NewGuid(),
                SourceRevisionId = control.RevisionId,
                ContentSha256 = contentSha256,
                StoreRelativePath = relativePath,
                ByteLength = 4,
                ChecksumVerifiedAtUtc = now,
                ReferenceCount = 1
            });
            await writer.SaveChangesAsync();
        }

        var cleanup = Assert.IsType<SourceDeletionWorkItem>(await store.ClaimNextAsync(CancellationToken.None));
        Assert.Empty(await store.ReadPendingCleanupAsync(cleanup, CancellationToken.None));

        await using var verification = CreateContext();
        Assert.Contains(await verification.SourceArtifacts.ToListAsync(), artifact => artifact.SourceRevisionId == control.RevisionId);
        Assert.Equal("source-delete-shared-artifact-preserved", await verification.SourceDeletionCleanupItems
            .Where(item => item.SourceDeletionOperationId == operationId)
            .Select(item => item.ReasonCode)
            .SingleAsync());
    }

    [NativeSqlServerFact]
    public async Task Cleanup_waits_for_shared_publication_then_preserves_the_new_reference()
    {
        var now = DateTimeOffset.UtcNow;
        var deleting = await SeedRootAsync("publication-race-delete", SourceRootState.Deleting, now);
        var control = await SeedRootAsync("publication-race-control", SourceRootState.Enabled, now);
        var operationId = Guid.NewGuid();
        var contentSha256 = new string('c', 64);
        var relativePath = $"sha256\\{contentSha256[..2]}\\{contentSha256}.bin";
        await using (var setup = CreateContext())
        {
            setup.SourceDeletionOperations.Add(new SourceDeletionOperationEntity
            {
                Id = operationId,
                SourceRootId = deleting.RootId,
                State = 0,
                Phase = "accepted",
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            });
            setup.SourceArtifacts.Add(new SourceArtifactEntity
            {
                Id = Guid.NewGuid(),
                SourceRevisionId = deleting.RevisionId,
                ContentSha256 = contentSha256,
                StoreRelativePath = relativePath,
                ByteLength = 4,
                ChecksumVerifiedAtUtc = now,
                ReferenceCount = 1
            });
            await setup.SaveChangesAsync();
        }

        var factory = SqlTestData.CreateFactory(_fixture);
        var gate = new SqlSourceArtifactPublicationGate(factory);
        var fileStore = new RecordingFileStore();
        var coordinator = new SourceDeletionCoordinator(
            new SqlSourceDeletionStore(factory, TimeProvider.System),
            fileStore: fileStore,
            artifactPublicationGate: gate);
        await using var publicationLease = await gate.AcquireSharedAsync(contentSha256, CancellationToken.None);

        Assert.False(await coordinator.RunOneAsync(CancellationToken.None));
        Assert.Equal(0, fileStore.DeleteCalls);

        await using (var writer = CreateContext())
        {
            writer.SourceArtifacts.Add(new SourceArtifactEntity
            {
                Id = Guid.NewGuid(),
                SourceRevisionId = control.RevisionId,
                ContentSha256 = contentSha256,
                StoreRelativePath = relativePath,
                ByteLength = 4,
                ChecksumVerifiedAtUtc = now,
                ReferenceCount = 1
            });
            await writer.SaveChangesAsync();
        }
        await publicationLease.DisposeAsync();

        Assert.True(await coordinator.RunOneAsync(CancellationToken.None));
        Assert.Equal(0, fileStore.DeleteCalls);
        await using var verification = CreateContext();
        Assert.NotNull(await verification.SourceArtifacts.SingleOrDefaultAsync(artifact => artifact.SourceRevisionId == control.RevisionId));
        Assert.Equal("completed", await verification.SourceDeletionOperations
            .Where(operation => operation.Id == operationId)
            .Select(operation => operation.Phase)
            .SingleAsync());
    }

    [NativeSqlServerFact]
    public async Task Coordinator_removes_an_expired_processing_job_instead_of_draining_forever()
    {
        var now = DateTimeOffset.UtcNow;
        var deleting = await SeedRootAsync("expired-processing-job", SourceRootState.Deleting, now);
        var operationId = Guid.NewGuid();
        await using (var setup = CreateContext())
        {
            setup.SourceDeletionOperations.Add(new SourceDeletionOperationEntity
            {
                Id = operationId,
                SourceRootId = deleting.RootId,
                State = 0,
                Phase = "accepted",
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            });
            var job = await setup.Jobs.SingleAsync(value => value.Id == deleting.JobId);
            job.PublicState = (int)FluxKnowledge.Domain.Jobs.PublicJobState.WorkerProcessing;
            job.LeaseOwner = "crashed-source-worker";
            job.LeaseExpiresAtUtc = now.AddMinutes(-1);
            await setup.SaveChangesAsync();
        }

        var coordinator = new SourceDeletionCoordinator(new SqlSourceDeletionStore(
            SqlTestData.CreateFactory(_fixture), TimeProvider.System));

        Assert.True(await coordinator.RunOneAsync(CancellationToken.None));

        await using var verification = CreateContext();
        Assert.Null(await verification.SourceRootConfigurations.SingleOrDefaultAsync(value => value.Id == deleting.RootId));
        Assert.Null(await verification.Jobs.SingleOrDefaultAsync(value => value.Id == deleting.JobId));
        Assert.Equal("completed", await verification.SourceDeletionOperations
            .Where(value => value.Id == operationId)
            .Select(value => value.Phase)
            .SingleAsync());
    }

    [NativeSqlServerFact]
    public async Task Coordinator_drains_a_live_outbox_lease_before_removing_owned_state()
    {
        var now = DateTimeOffset.UtcNow;
        var deleting = await SeedRootAsync("outbox-lease", SourceRootState.Deleting, now);
        var operationId = Guid.NewGuid();
        await using (var setup = CreateContext())
        {
            setup.SourceDeletionOperations.Add(new SourceDeletionOperationEntity
            {
                Id = operationId,
                SourceRootId = deleting.RootId,
                State = 0,
                Phase = "accepted",
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            });
            var outbox = await setup.OutboxMessages.SingleAsync(value => value.PipelineRecordId == deleting.RecordId);
            outbox.LeaseOwner = "live-source-delete-test";
            outbox.LeaseExpiresAtUtc = now.AddMinutes(1);
            await setup.SaveChangesAsync();
        }

        var coordinator = new SourceDeletionCoordinator(new SqlSourceDeletionStore(SqlTestData.CreateFactory(_fixture), TimeProvider.System));

        Assert.False(await coordinator.RunOneAsync(CancellationToken.None));

        await using var verification = CreateContext();
        Assert.NotNull(await verification.SourceRootConfigurations.SingleOrDefaultAsync(value => value.Id == deleting.RootId));
        var operation = await verification.SourceDeletionOperations.SingleAsync(value => value.Id == operationId);
        Assert.Equal(0, operation.State);
        Assert.Equal("draining", operation.Phase);
    }

    [NativeSqlServerFact]
    public async Task Coordinator_cuts_over_to_a_survivor_generation_before_removing_a_vector_owning_root()
    {
        var now = DateTimeOffset.Parse("2026-09-18T11:45:00+00:00");
        var deleting = await SeedRootAsync("cutover-deleting", SourceRootState.Deleting, now);
        var survivor = await SeedRootAsync("cutover-survivor", SourceRootState.Enabled, now);
        var retiredGenerationId = Guid.NewGuid();
        var survivorGenerationId = Guid.NewGuid();
        var operationId = Guid.NewGuid();
        await using (var setup = CreateContext())
        {
            setup.IndexGenerations.Add(new IndexGenerationEntity
            {
                Id = retiredGenerationId,
                ModelFingerprint = "source-deletion-test:1",
                Dimensions = 1,
                IndexPath = "retired",
                MetadataChecksum = new string('a', 64),
                VectorCount = 2,
                CreatedAtUtc = now,
                ValidatedAtUtc = now
            });
            setup.SourceDeletionOperations.Add(new SourceDeletionOperationEntity
            {
                Id = operationId,
                SourceRootId = deleting.RootId,
                State = 0,
                Phase = "accepted",
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            });
            await setup.SaveChangesAsync();
        }

        var deletingVector = await AddVectorAsync(deleting, retiredGenerationId, "remove", now);
        var survivorVector = await AddVectorAsync(survivor, retiredGenerationId, "retain", now);
        await using (var setup = CreateContext())
        {
            setup.IndexGenerationVectors.AddRange(
                new IndexGenerationVectorEntity { GenerationId = retiredGenerationId, VectorId = deletingVector.VectorId },
                new IndexGenerationVectorEntity { GenerationId = retiredGenerationId, VectorId = survivorVector.VectorId });
            await setup.SaveChangesAsync();
        }

        var candidate = new IndexGenerationCandidateSnapshot(
            new IndexGenerationDescriptor(
                survivorGenerationId,
                "source-deletion-test:1",
                1,
                "survivor",
                ComputeMetadataChecksum("source-deletion-test:1", 1, [Canonical(survivorVector)]),
                1),
            [Canonical(survivorVector)]);
        var coordinator = new SourceDeletionCoordinator(
            new SqlSourceDeletionStore(SqlTestData.CreateFactory(_fixture), TimeProvider.System),
            new FixedGenerationPublisher(candidate),
            new SuccessfulFileStore());

        Assert.True(await coordinator.RunOneAsync(CancellationToken.None));

        await using var verification = CreateContext();
        Assert.Null(await verification.SourceRootConfigurations.SingleOrDefaultAsync(row => row.Id == deleting.RootId));
        Assert.NotNull(await verification.SourceRootConfigurations.SingleOrDefaultAsync(row => row.Id == survivor.RootId));
        Assert.Null(await verification.Vectors.SingleOrDefaultAsync(row => row.VectorId == deletingVector.VectorId));
        Assert.NotNull(await verification.Vectors.SingleOrDefaultAsync(row => row.VectorId == survivorVector.VectorId));
        var retired = await verification.IndexGenerations.SingleAsync(row => row.Id == retiredGenerationId);
        Assert.NotNull(retired.RetiredAtUtc);
        Assert.Equal(survivorGenerationId, (await verification.IndexState.SingleAsync(row => row.Id == 1)).ActiveIndexGenerationId);
        Assert.Equal([survivorVector.VectorId], await verification.IndexGenerationVectors
            .Where(row => row.GenerationId == survivorGenerationId)
            .Select(row => row.VectorId)
            .ToArrayAsync());
        Assert.Equal("completed", (await verification.SourceDeletionOperations.SingleAsync(row => row.Id == operationId)).Phase);
    }

    [NativeSqlServerFact]
    public async Task Older_deletion_rebuild_keeps_a_later_pending_deletion_vector_as_its_temporary_survivor()
    {
        var now = DateTimeOffset.Parse("2026-09-18T12:00:00+00:00");
        var older = await SeedRootAsync("older-deletion", SourceRootState.Deleting, now);
        var later = await SeedRootAsync("later-deletion", SourceRootState.Deleting, now);
        var retiredGenerationId = Guid.NewGuid();
        await using (var setup = CreateContext())
        {
            setup.IndexGenerations.Add(new IndexGenerationEntity
            {
                Id = retiredGenerationId,
                ModelFingerprint = "source-deletion-test:1",
                Dimensions = 1,
                IndexPath = "retired-two-deletions",
                MetadataChecksum = new string('a', 64),
                VectorCount = 2,
                CreatedAtUtc = now,
                ValidatedAtUtc = now
            });
            setup.SourceDeletionOperations.AddRange(
                new SourceDeletionOperationEntity
                {
                    Id = Guid.NewGuid(),
                    SourceRootId = older.RootId,
                    State = 0,
                    Phase = "accepted",
                    CreatedAtUtc = now,
                    UpdatedAtUtc = now
                },
                new SourceDeletionOperationEntity
                {
                    Id = Guid.NewGuid(),
                    SourceRootId = later.RootId,
                    State = 0,
                    Phase = "accepted",
                    CreatedAtUtc = now.AddSeconds(1),
                    UpdatedAtUtc = now.AddSeconds(1)
                });
            await setup.SaveChangesAsync();
        }

        var olderVector = await AddVectorAsync(older, retiredGenerationId, "older", now);
        var laterVector = await AddVectorAsync(later, retiredGenerationId, "later", now);
        await using (var setup = CreateContext())
        {
            setup.IndexGenerationVectors.AddRange(
                new IndexGenerationVectorEntity { GenerationId = retiredGenerationId, VectorId = olderVector.VectorId },
                new IndexGenerationVectorEntity { GenerationId = retiredGenerationId, VectorId = laterVector.VectorId });
            await setup.SaveChangesAsync();
        }

        var store = new SqlSourceDeletionStore(SqlTestData.CreateFactory(_fixture), TimeProvider.System);
        var claimed = Assert.IsType<SourceDeletionWorkItem>(await store.ClaimNextAsync(CancellationToken.None));
        Assert.Equal(older.RootId, claimed.SourceRootId);
        Assert.True((await store.PurgeAsync(claimed, survivorGeneration: null, CancellationToken.None)).RequiresIndexBuild);

        var membership = await new SqlPipelineStore(SqlTestData.CreateFactory(_fixture), TimeProvider.System)
            .ReadEligibleVectorsAsync(CancellationToken.None);
        Assert.Equal([laterVector.VectorId], membership.Select(vector => vector.VectorId).ToArray());
        var candidate = new IndexGenerationCandidateSnapshot(
            new IndexGenerationDescriptor(
                Guid.NewGuid(),
                "source-deletion-test:1",
                1,
                "later-deletion-survivor",
                ComputeMetadataChecksum("source-deletion-test:1", 1, membership),
                membership.Count),
            membership);

        var rebuilding = Assert.IsType<SourceDeletionWorkItem>(await store.ClaimNextAsync(CancellationToken.None));
        var completedPurge = await store.PurgeAsync(rebuilding, candidate, CancellationToken.None);

        Assert.Equal("cleanup-files", completedPurge.Phase);
        await using var verification = CreateContext();
        Assert.NotNull(await verification.SourceRootConfigurations.SingleOrDefaultAsync(root => root.Id == later.RootId));
        Assert.False(await verification.Vectors.Where(vector => vector.VectorId == laterVector.VectorId)
            .Select(vector => vector.IsDeleted).SingleAsync());
    }

    private async Task<SeededRoot> SeedRootAsync(string name, SourceRootState state, DateTimeOffset now)
    {
        var rootId = Guid.NewGuid();
        var revisionId = Guid.NewGuid();
        var identityId = Guid.NewGuid();
        var recordId = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        var outboxId = Guid.NewGuid();
        await using var context = CreateContext();
        context.SourceRootConfigurations.Add(new SourceRootConfigurationEntity
        {
            Id = rootId,
            CanonicalPath = $"C:\\source-deletion-tests\\{rootId:N}",
            DisplayName = name,
            State = (int)state,
            Recursive = true,
            IncludePatternsJson = "[]",
            ExcludePatternsJson = "[]",
            AllowedClassificationsJson = "[\"text/plain\"]",
            MaximumFileBytes = 16L * 1024 * 1024,
            ReconciliationCadenceSeconds = 900,
            ConfigurationRevision = 1,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        });
        context.SourceRevisions.Add(new SourceRevisionEntity
        {
            Id = revisionId,
            SourceRootId = rootId,
            StableSourceIdentity = $"source-deletion:{revisionId:N}",
            Revision = 1,
            ContentSha256 = new string('a', 64),
            CanonicalPath = $"C:\\source-deletion-tests\\{rootId:N}\\document.txt",
            Classification = "AcceptedUtf8Text",
            Extension = ".txt",
            ByteLength = 4,
            DiscoveredAtUtc = now,
            DiscoveryEvidenceJson = "{}"
        });
        context.SourceIdentities.Add(new SourceIdentityEntity
        {
            Id = identityId,
            SourceKind = "local file",
            StableKey = $"source-deletion:{rootId:N}",
            CreatedAtUtc = now
        });
        context.PipelineRecords.Add(new PipelineRecordEntity
        {
            Id = recordId,
            SourceIdentityId = identityId,
            SourceRevisionId = revisionId,
            Revision = 1,
            ContentHash = new string('a', 64),
            RootLineageRecordId = recordId,
            CurrentStage = 0,
            RegisteredAtUtc = now
        });
        context.Jobs.Add(new JobEntity
        {
            Id = jobId,
            PipelineRecordId = recordId,
            SourceRevision = 1,
            Stage = 0,
            Operation = "extract-utf8",
            PublicState = 0,
            DueAtUtc = now
        });
        context.OutboxMessages.Add(new OutboxMessageEntity
        {
            Id = outboxId,
            PipelineRecordId = recordId,
            SourceRevision = 1,
            Stage = 0,
            Operation = "extract-utf8",
            DispatchGeneration = 0,
            IdempotencyKey = $"source-deletion:{rootId:N}",
            DueAtUtc = now,
            CreatedAtUtc = now
        });
        await context.SaveChangesAsync();
        return new SeededRoot(rootId, revisionId, recordId, jobId);
    }

    private FluxKnowledgeDbContext CreateContext() => new(
        new DbContextOptionsBuilder<FluxKnowledgeDbContext>()
            .UseSqlServer(_fixture.ConnectionString)
            .Options);

    private sealed record SeededRoot(Guid RootId, Guid RevisionId, Guid RecordId, Guid JobId);

    private static void AddLocalOcrTask(
        FluxKnowledgeDbContext context,
        SeededRoot root,
        Guid miniTaskId,
        GpuMiniTaskExecutionState executionState,
        DateTimeOffset now)
    {
        context.GpuMiniTasks.Add(new GpuMiniTaskEntity
        {
            Id = miniTaskId,
            ParentJobId = root.JobId,
            SourceRevision = 1,
            PriorityLane = 0,
            ModelRuntimeKey = PaddleOcrVlmRuntimeContract.ModelRuntimeKey,
            SettingsFingerprint = PaddleOcrVlmRuntimeContract.SettingsFingerprint,
            EstimatedBytes = PaddleOcrVlmRuntimeContract.EstimatedDocumentBytes,
            IdempotencyKey = $"source-delete-local-ocr:{root.RecordId:N}:{miniTaskId:N}",
            ExecutionState = (int)executionState,
            CreatedAtUtc = now
        });
        AddLocalOcrRequest(context, root, miniTaskId, now);
    }

    private static void AddLocalOcrRequest(
        FluxKnowledgeDbContext context,
        SeededRoot root,
        Guid miniTaskId,
        DateTimeOffset now)
    {
        context.DocumentOcrRequests.Add(new DocumentOcrRequestEntity
        {
            MiniTaskId = miniTaskId,
            ParentJobId = root.JobId,
            PipelineRecordId = root.RecordId,
            SourceRevision = 1,
            RetainedSourceRevisionId = root.RevisionId,
            ContentSha256 = new string('a', 64),
            RequestedPageIndexesJson = "[0]",
            ModelRuntimeKey = PaddleOcrVlmRuntimeContract.ModelRuntimeKey,
            SettingsFingerprint = PaddleOcrVlmRuntimeContract.SettingsFingerprint,
            State = (int)DocumentOcrRequestState.Pending,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        });
    }

    private async Task<VectorEntity> AddVectorAsync(SeededRoot root, Guid generationId, string content, DateTimeOffset now)
    {
        var values = new byte[] { 0, 0, 0, 0 };
        await using var context = CreateContext();
        var artifact = new ArtifactEntity
        {
            Id = Guid.NewGuid(),
            PipelineRecordId = root.RecordId,
            SourceRevision = 1,
            Stage = 3,
            ContentHash = new string('a', 64),
            ContentType = "text/plain",
            SearchText = content,
            CreatedAtUtc = now
        };
        var chunk = new TextChunkEntity
        {
            Artifact = artifact,
            SourceRevision = 1,
            Ordinal = 0,
            StartOffset = 0,
            Length = content.Length,
            Content = content,
            ContentHash = new string('c', 64)
        };
        var vector = new VectorEntity
        {
            TextChunk = chunk,
            SourceRevision = 1,
            ModelFingerprint = "source-deletion-test:1",
            Dimensions = 1,
            Values = values,
            TextChunkContentHash = chunk.ContentHash,
            PayloadChecksum = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(values)),
            IndexGenerationId = generationId,
            CreatedAtUtc = now
        };
        context.Vectors.Add(vector);
        await context.SaveChangesAsync();
        return vector;
    }

    private static CanonicalVector Canonical(VectorEntity vector) => new(
        vector.VectorId,
        vector.TextChunkId,
        vector.ModelFingerprint,
        vector.Dimensions,
        vector.Values,
        vector.TextChunkContentHash,
        vector.PayloadChecksum,
        vector.SourceRevision);

    private static string ComputeMetadataChecksum(string modelFingerprint, int dimensions, IReadOnlyList<CanonicalVector> vectors)
    {
        var data = $"{modelFingerprint}|cos|{dimensions}|{string.Join(',', vectors.Select(vector => $"{vector.VectorId}:{vector.PayloadChecksum}"))}";
        return Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(data)));
    }

    private sealed class FixedGenerationPublisher(IndexGenerationCandidateSnapshot candidate) : IIndexGenerationPublisher
    {
        public ValueTask<IndexGenerationCandidateSnapshot> BuildAndPlaceAsync(Guid indexGenerationId, CancellationToken cancellationToken) =>
            ValueTask.FromResult(candidate);

        public ValueTask<IndexGenerationDescriptor> RebuildFromSqlAsync(Guid indexGenerationId, CancellationToken cancellationToken) =>
            ValueTask.FromResult(candidate.Generation);
    }

    private sealed class SuccessfulFileStore : ISourceDeletionFileStore
    {
        public ValueTask<SourceDeletionFileResult> DeleteAsync(SourceDeletionFileTarget target, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new SourceDeletionFileResult(true));
    }

    private sealed class RecordingFileStore : ISourceDeletionFileStore
    {
        public int DeleteCalls { get; private set; }

        public ValueTask<SourceDeletionFileResult> DeleteAsync(SourceDeletionFileTarget target, CancellationToken cancellationToken)
        {
            DeleteCalls++;
            return ValueTask.FromResult(new SourceDeletionFileResult(true));
        }
    }

    private sealed class RetryingContextFactory(string connectionString) : IDbContextFactory<FluxKnowledgeDbContext>
    {
        private readonly DbContextOptions<FluxKnowledgeDbContext> _options =
            new DbContextOptionsBuilder<FluxKnowledgeDbContext>()
                .UseSqlServer(connectionString, sqlServer => sqlServer.EnableRetryOnFailure())
                .Options;

        public FluxKnowledgeDbContext CreateDbContext() => new(_options);
    }
}
