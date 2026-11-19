using System.Data;
using System.Security.Cryptography;
using System.Text;
using FluxKnowledge.Application.Indexing;
using FluxKnowledge.Application.Gpu;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Domain.Jobs;
using FluxKnowledge.Domain.Pipeline;
using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using FluxKnowledge.Infrastructure.SqlServer.Workers;
using Microsoft.EntityFrameworkCore;

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence;

/// <summary>
/// Owns the SQL portion of a local-source deletion.  The root is fenced before this
/// store is called; each leased unit rechecks its authority under a serializable
/// transaction. Historical memberships drain without holding a whole-source purge open.
/// </summary>
public sealed partial class SqlSourceDeletionStore(
    IDbContextFactory<FluxKnowledgeDbContext> contextFactory,
    TimeProvider timeProvider,
    PaddleOcrVlmExecutionRegistry? localOcrExecutions = null, IGpuInteractiveOwnerProbe? queryOwnerProbe = null,
    EmbeddingGpuRuntime? embeddingRuntime = null, EmbeddingGpuExecutor? localEmbeddingExecutions = null,
    ICorpusAnnPreparationSignal? annPreparation = null) : ISourceDeletionStore
{
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(15);

    public SqlSourceDeletionStore(IDbContextFactory<FluxKnowledgeDbContext> contextFactory)
        : this(contextFactory, TimeProvider.System, null)
    {
    }

    public ValueTask<SourceDeletionWorkItem?> ClaimNextAsync(CancellationToken cancellationToken) =>
        WithSqlFailureAsync(() => ClaimNextCoreAsync(cancellationToken));

    private async ValueTask<SourceDeletionWorkItem?> ClaimNextCoreAsync(CancellationToken cancellationToken)
    {
        // A retry after COMMIT must recover this attempted claim, not take a
        // second source. Keep both identities outside the retry delegate.
        var attemptedLease = Guid.NewGuid();
        Guid? attemptedOperation = null;
        await using var executionContext = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var strategy = executionContext.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false);
            var now = timeProvider.GetUtcNow();
            var operation = attemptedOperation is { } operationId
                ? await ReadOperationForUpdateAsync(context, operationId, cancellationToken).ConfigureAwait(false)
                : await context.SourceDeletionOperations.FromSqlInterpolated($"""
                    SELECT TOP (1) *
                    FROM [SourceDeletionOperations] WITH (UPDLOCK, HOLDLOCK, ROWLOCK)
                    WHERE [State] = {0}
                       OR ([State] = {1} AND ([LeaseExpiresAtUtc] IS NULL OR [LeaseExpiresAtUtc] <= {now}))
                    ORDER BY [CreatedAtUtc], [Id]
                    """)
                    .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            if (operation is null)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return null;
            }

            attemptedOperation = operation.Id;
            if (operation.State == 1 && operation.LeaseId == attemptedLease && operation.LeaseExpiresAtUtc > now)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return new SourceDeletionWorkItem(operation.Id, operation.SourceRootId, attemptedLease, operation.Phase)
                { LeaseExpiresAtUtc = operation.LeaseExpiresAtUtc };
            }
            if (operation.State != 0 && !(operation.State == 1 &&
                (operation.LeaseExpiresAtUtc is null || operation.LeaseExpiresAtUtc <= now)))
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return null;
            }
            operation.State = 1;
            operation.LeaseId = attemptedLease;
            operation.LeaseExpiresAtUtc = now.Add(LeaseDuration);
            operation.UpdatedAtUtc = now;
            if (await SqlOutlookSourceReservations.ProtectsDeletionAsync(context, operation.SourceRootId, cancellationToken).ConfigureAwait(false))
            {
                await RefuseAsync(context, transaction, operation, SqlOutlookSourceReservations.DeleteRefusal, cancellationToken).ConfigureAwait(false);
                return null;
            }
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new SourceDeletionWorkItem(operation.Id, operation.SourceRootId, attemptedLease, operation.Phase)
            { LeaseExpiresAtUtc = operation.LeaseExpiresAtUtc };
        }).ConfigureAwait(false);
    }

    public async ValueTask<SourceDeletionRunResult> PurgeAsync(
        SourceDeletionWorkItem workItem,
        IndexGenerationCandidateSnapshot? survivorGeneration,
        CancellationToken cancellationToken)
    {
        try
        {
            return await WithSqlFailureAsync(() => PurgeCoreAsync(workItem, survivorGeneration, cancellationToken)).ConfigureAwait(false);
        }
        catch (SourceDeletionLeaseLostException)
        {
            return LostOwnership(workItem);
        }
    }

    private async ValueTask<SourceDeletionRunResult> PurgeCoreAsync(
        SourceDeletionWorkItem workItem,
        IndexGenerationCandidateSnapshot? survivorGeneration,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workItem);
        await using var executionContext = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var strategy = executionContext.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false);
            if (!await SqlCorpusGenerationLeaseStore.TryAcquireCleanupTransactionAsync(context, queryOwnerProbe, cancellationToken).ConfigureAwait(false))
            {
                var waiting = await ReadOperationForUpdateAsync(context, workItem.OperationId, cancellationToken).ConfigureAwait(false);
                if (ReadPreviousOutcome(waiting, workItem) is { } previous) return previous;
                return await CommitUnitAsync(context, transaction, waiting!, waiting!.Phase,
                    cancellationToken, "source-delete-search-queries-active", progressed: false, continueImmediately: false).ConfigureAwait(false);
            }
            await SqlPublishedPassageSelection.AcquireFenceAsync(context, cancellationToken).ConfigureAwait(false);
            var claimedOperation = await ReadOperationForUpdateAsync(context, workItem.OperationId, cancellationToken).ConfigureAwait(false);
            if (ReadPreviousOutcome(claimedOperation, workItem) is { } previousOutcome) return previousOutcome;
            var operation = claimedOperation!;

            if (await SqlOutlookSourceReservations.ProtectsDeletionAsync(context, operation.SourceRootId, cancellationToken).ConfigureAwait(false))
                return await RefuseAsync(context, transaction, operation, SqlOutlookSourceReservations.DeleteRefusal, cancellationToken).ConfigureAwait(false);

            if (string.Equals(operation.Phase, "cleanup-files", StringComparison.Ordinal))
            {
                var sourceState = await context.SourceRootConfigurations.Where(root => root.Id == operation.SourceRootId)
                    .Select(root => (int?)root.State).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
                if (sourceState != (int)SourceRootState.Deleting)
                    return await RefuseAsync(context, transaction, operation, "source-delete-not-fenced", cancellationToken).ConfigureAwait(false);
                if (await context.SourceDeletionCleanupItems.AnyAsync(item => item.SourceDeletionOperationId == operation.Id && item.State == 2, cancellationToken).ConfigureAwait(false))
                    return await RefuseAsync(context, transaction, operation, "source-delete-file-cleanup-failed", cancellationToken).ConfigureAwait(false);
                if (await context.SourceDeletionCleanupItems.AnyAsync(item => item.SourceDeletionOperationId == operation.Id && item.State != 1, cancellationToken).ConfigureAwait(false))
                {
                    return await CommitUnitAsync(context, transaction, operation, operation.Phase,
                        cancellationToken, progressed: false, continueImmediately: false).ConfigureAwait(false);
                }

                await context.SourceDeletionCleanupItems.Where(item => item.SourceDeletionOperationId == operation.Id)
                    .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
                await context.SourceRootConfigurations.Where(value => value.Id == operation.SourceRootId)
                    .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
                return await CommitUnitAsync(context, transaction, operation, "completed", cancellationToken, nextState: 3).ConfigureAwait(false);
            }

            var root = await context.SourceRootConfigurations
                .FromSqlInterpolated($"""
                    SELECT * FROM [SourceRootConfigurations] WITH (UPDLOCK, HOLDLOCK)
                    WHERE [Id] = {workItem.SourceRootId}
                    """)
                .SingleOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);
            if (root is null)
            {
                return await RefuseAsync(context, transaction, operation, "source-delete-source-root-missing", cancellationToken).ConfigureAwait(false);
            }

            if (root.State != (int)SourceRootState.Deleting)
            {
                return await RefuseAsync(context, transaction, operation, "source-delete-not-fenced", cancellationToken).ConfigureAwait(false);
            }

            if (await SqlOutlookSourceReservations.ProtectsDeletionAsync(context, root.Id, cancellationToken).ConfigureAwait(false))
            {
                return await RefuseAsync(context, transaction, operation, SqlOutlookSourceReservations.DeleteRefusal, cancellationToken).ConfigureAwait(false);
            }

            // This phase removes only retired projection memberships. Reclassifying
            // every historical execution for each 1,000-row page is quadratic in
            // source history. Keep fresh execution/page fences here; the full
            // ownership and graph proof below still runs on every payload unit.
            if (operation.Phase is "cleanup-memberships" or "purge-records" &&
                await SqlSourceDeletionOwnership.HasUnconfirmedWorkAsync(context, root.Id, cancellationToken).ConfigureAwait(false))
                return await CommitUnitAsync(context, transaction, operation, operation.Phase, cancellationToken,
                    "source-delete-native-cleanup-pending", progressed: false, continueImmediately: false).ConfigureAwait(false);
            if (operation.Phase == "cleanup-memberships")
            {
                if (await HasActiveWorkAsync(context, root.Id, timeProvider.GetUtcNow(), cancellationToken).ConfigureAwait(false))
                    return await CommitUnitAsync(context, transaction, operation, operation.Phase, cancellationToken,
                        "source-delete-work-active", progressed: false, continueImmediately: false).ConfigureAwait(false);
                return await DeleteMembershipUnitAsync(context, transaction, operation, cancellationToken).ConfigureAwait(false);
            }

            var revisionIds = SqlSourceDeletionOwnership.RevisionIds(context, root.Id);
            var recordIds = SqlSourceDeletionOwnership.RecordIds(context, root.Id);
            var ownership = await SqlSourceDeletionOwnership.ReadAsync(context, root.Id, embeddingRuntime, cancellationToken).ConfigureAwait(false);
            if (!ownership.IsOwned)
            {
                return await RefuseAsync(context, transaction, operation, "source-delete-external-execution-owned", cancellationToken).ConfigureAwait(false);
            }
            var sourceGpuTaskIds = ownership.TaskIds;
            var localOcrTaskSet = ownership.OcrTaskIds.ToHashSet();
            var localEmbeddingTaskSet = ownership.EmbeddingTaskIds.ToHashSet();
            var activeLocalTaskIds = sourceGpuTaskIds.Length == 0
                ? []
                : await context.GpuMiniTasks.Where(value =>
                        value.ExecutionState == (int)Domain.Gpu.GpuMiniTaskExecutionState.Active && value.ParentJobId.HasValue &&
                        context.Jobs.Any(job => job.Id == value.ParentJobId && recordIds.Contains(job.PipelineRecordId)))
                    .Select(value => value.Id)
                    .ToArrayAsync(cancellationToken)
                    .ConfigureAwait(false);
            var unconfirmedEmbeddingCleanup = await context.EmbeddingGpuRequests.AnyAsync(value => recordIds.Contains(value.PipelineRecordId) &&
                value.ExecutorInstanceId != null && !value.NativeCleanupConfirmed, cancellationToken).ConfigureAwait(false);
            if (activeLocalTaskIds.Length > 0 || unconfirmedEmbeddingCleanup)
            {
                var activeOcrTasks = activeLocalTaskIds.Where(localOcrTaskSet.Contains).ToArray();
                if (activeOcrTasks.Length > 0 && !(localOcrExecutions is not null && activeOcrTasks.Any(localOcrExecutions.RequestCancellation)))
                {
                    return await RefuseAsync(context, transaction, operation, "source-delete-external-execution-owned", cancellationToken).ConfigureAwait(false);
                }
                if (localEmbeddingExecutions is not null)
                    foreach (var taskId in activeLocalTaskIds.Where(localEmbeddingTaskSet.Contains)) localEmbeddingExecutions.RequestCancellation(taskId);

                return await CommitUnitAsync(context, transaction, operation,
                    IsAfterCutover(operation.Phase) ? operation.Phase : "draining", cancellationToken,
                    "source-delete-native-cleanup-pending", progressed: false, continueImmediately: false).ConfigureAwait(false);
            }
            if (await HasActiveWorkAsync(context, root.Id, timeProvider.GetUtcNow(), cancellationToken).ConfigureAwait(false))
            {
                return await CommitUnitAsync(context, transaction, operation,
                    IsAfterCutover(operation.Phase) ? operation.Phase : "draining", cancellationToken,
                    "source-delete-work-active", progressed: false, continueImmediately: false).ConfigureAwait(false);
            }

            if (await HasCrossRootDependencyAsync(context, root.Id, cancellationToken).ConfigureAwait(false))
            {
                return await RefuseAsync(context, transaction, operation, "source-delete-cross-root-dependency", cancellationToken).ConfigureAwait(false);
            }

            if (operation.Phase == "purge-records")
            {
                if (await context.SourceDeletionCleanupItems.AnyAsync(value => value.SourceDeletionOperationId == operation.Id &&
                    (value.StorageKind == 3 || value.StorageKind == 4) && value.State != 1, cancellationToken))
                    return await CommitUnitAsync(context, transaction, operation, operation.Phase, cancellationToken,
                        "source-delete-segment-retirement-pending", progressed: false, continueImmediately: false);
                if (await HasOwnedMembershipsAsync(context, root.Id, cancellationToken).ConfigureAwait(false))
                    return await RefuseAsync(context, transaction, operation, "source-delete-membership-changed", cancellationToken).ConfigureAwait(false);
                var executionGraph=await AssertOwnedGraphPurgeReadyAsync(context,recordIds,cancellationToken);
                if (!await DetachTerminalCandidateProvenanceAsync(context, root.Id, recordIds, cancellationToken))
                    return await CommitUnitAsync(context, transaction, operation, operation.Phase, cancellationToken,
                        "source-delete-publication-execution-unconfirmed", progressed: false, continueImmediately: false);
                var vectorPage = await OwnedVectors(context,root.Id).OrderBy(value => value.VectorId)
                    .Select(value => value.VectorId).Take(PurgePayloadBatchSize).ToArrayAsync(cancellationToken);
                if (vectorPage.Length > 0)
                {
                    if (await context.Vectors.AnyAsync(value => vectorPage.Contains(value.VectorId) && !value.IsDeleted,cancellationToken))
                        return await RefuseAsync(context,transaction,operation,"source-delete-vector-not-withdrawn",cancellationToken);
                    await context.Vectors.Where(value => vectorPage.Contains(value.VectorId)).ExecuteDeleteAsync(cancellationToken);
                    return await CommitUnitAsync(context,transaction,operation,operation.Phase,cancellationToken);
                }
                var chunkPage = await context.TextChunks.Where(value => value.Artifact.PipelineRecord.SourceRevisionId.HasValue &&
                        value.Artifact.PipelineRecord.SourceRevision!.SourceRootId == root.Id)
                    .OrderBy(value => value.Id).Select(value => value.Id).Take(PurgePayloadBatchSize).ToArrayAsync(cancellationToken);
                if (chunkPage.Length > 0)
                {
                    await context.TextChunks.Where(value => chunkPage.Contains(value.Id)).ExecuteDeleteAsync(cancellationToken);
                    return await CommitUnitAsync(context,transaction,operation,operation.Phase,cancellationToken);
                }
                if(await DeleteOwnedExecutionPageAsync(context,executionGraph,cancellationToken).ConfigureAwait(false))
                    return await CommitUnitAsync(context,transaction,operation,operation.Phase,cancellationToken);
                if (await DeleteOwnedAuditPageAsync(context, root.Id, cancellationToken).ConfigureAwait(false))
                    return await CommitUnitAsync(context, transaction, operation, operation.Phase, cancellationToken);
                // Keep ownership sets on SQL while their principals remain. The
                // fresh empty audit proof and final graph removal share this unit.
                if (await DeleteOwnedGraphAsync(context, root.Id, cancellationToken).ConfigureAwait(false))
                    return await CommitUnitAsync(context, transaction, operation, operation.Phase, cancellationToken).ConfigureAwait(false);
                if (await context.IndexState.Where(value => value.Id == 1).Select(value => value.RequiredIndexFormat).SingleAsync(cancellationToken) == 1 &&
                    (await SqlPublishedPassageSelection.ReadVectorIdsAsync(context, cancellationToken).ConfigureAwait(false)).Count == 0)
                    await ClearProjectionAndValidateEmptyCatalogueAsync(context, cancellationToken).ConfigureAwait(false);
                return await CommitUnitAsync(context, transaction, operation, "cleanup-files", cancellationToken).ConfigureAwait(false);
            }

            var targetVectorIds = OwnedVectors(context, root.Id).Select(value => value.VectorId);
            if (!string.Equals(operation.Phase, "rebuild-index", StringComparison.Ordinal))
            {
                // Withdraw owned unplaced checkpoints in the same durable phase as
                // their source rows. Recovery between deletion phases must not treat
                // a deliberately withdrawn partial draft as inconsistent provenance.
                await context.IndexGenerations.Where(generation => generation.RetiredAtUtc == null && generation.EmbeddingJobId.HasValue && generation.IndexPath == string.Empty &&
                    context.Jobs.Any(job => job.Id == generation.EmbeddingJobId.Value && recordIds.Contains(job.PipelineRecordId)))
                    .ExecuteUpdateAsync(update => update.SetProperty(generation => generation.RetiredAtUtc, timeProvider.GetUtcNow()), cancellationToken).ConfigureAwait(false);
                await context.PipelineRecords.Where(record => !record.IsDeleted && recordIds.Contains(record.Id))
                    .ExecuteUpdateAsync(setters => setters.SetProperty(record => record.IsDeleted, true), cancellationToken).ConfigureAwait(false);
                var cursor = operation.WithdrawalVectorCursor ?? 0;
                var page = await OwnedVectors(context, root.Id).Where(vector => !vector.IsDeleted && vector.VectorId > cursor)
                    .OrderBy(vector => vector.VectorId).Select(vector => vector.VectorId).Take(PayloadBatchSize)
                    .ToArrayAsync(cancellationToken).ConfigureAwait(false);
                if (page.Length > 0)
                {
                    // Ownership of these IDs was frozen by the selection above
                    // under this same serializable publication transaction.
                    await context.Vectors.Where(vector => page.Contains(vector.VectorId))
                        .ExecuteUpdateAsync(setters => setters.SetProperty(vector => vector.IsDeleted, true), cancellationToken).ConfigureAwait(false);
                    if (await context.Vectors.AnyAsync(vector => page.Contains(vector.VectorId) && !vector.IsDeleted, cancellationToken))
                        return await RefuseAsync(context, transaction, operation, "source-delete-withdrawal-not-committed", cancellationToken);
                    operation.WithdrawalVectorCursor = page[^1];
                    if (page.Length == PayloadBatchSize)
                        return await CommitUnitAsync(context, transaction, operation, operation.Phase, cancellationToken).ConfigureAwait(false);
                }
                if (await OwnedVectors(context, root.Id).AnyAsync(vector => !vector.IsDeleted, cancellationToken))
                    return await RefuseAsync(context, transaction, operation, "source-delete-withdrawal-changed", cancellationToken);

                return await CommitUnitAsync(context, transaction, operation, "rebuild-index", cancellationToken).ConfigureAwait(false);
            }

            var indexState = await context.IndexState.SingleAsync(value => value.Id == 1, cancellationToken);
            if (indexState.RequiredIndexFormat == 2)
            {
                if (survivorGeneration is null || survivorGeneration.Generation.FormatVersion != 2 ||
                    survivorGeneration.SegmentedCandidateId is null || survivorGeneration.Vectors.Count != 0)
                    return await RefuseAsync(context, transaction, operation, "source-delete-index-candidate-invalid", cancellationToken);
                IndexPublicationCandidateEntity candidate;
                try { candidate = await SqlSegmentedIndexStore.ValidateRefreshBeforeAsync(context, survivorGeneration, cancellationToken); }
                catch (FluxKnowledge.Application.Pipeline.PublicationSnapshotConflictException)
                {
                    return await CommitUnitAsync(context, transaction, operation, "rebuild-index", cancellationToken,
                        "source-delete-index-candidate-stale", progressed: false, continueImmediately: false);
                }
                await CaptureCleanupTargetsAsync(context, operation, revisionIds, recordIds, targetVectorIds, cancellationToken);
                await SqlSegmentedIndexStore.ActivateAsync(context, candidate, indexState, timeProvider.GetUtcNow(), cancellationToken);
                var committed = await CommitUnitAsync(context, transaction, operation, "cleanup-memberships", cancellationToken);
                annPreparation?.RequestPreparation();
                return committed;
            }
            if (indexState.RequiredIndexFormat != 1) throw new InvalidOperationException("index-format-unsupported");
            var currentMembership = await SqlPublishedPassageSelection.ReadVectorsAsync(context, cancellationToken).ConfigureAwait(false);
            if (survivorGeneration is null && currentMembership.Count != 0)
            {
                return await RefuseAsync(context, transaction, operation, "source-delete-index-publisher-unavailable", cancellationToken).ConfigureAwait(false);
            }
            if (survivorGeneration is not null && (!HasValidDescriptor(survivorGeneration) ||
                survivorGeneration.ExpectedCorpusStamp is null || survivorGeneration.Generation.CorpusStamp is null))
            {
                return await RefuseAsync(context, transaction, operation, "source-delete-index-candidate-invalid", cancellationToken).ConfigureAwait(false);
            }
            var corpusStamp = await SqlPublishedPassageSelection.ReadStampAsync(context, cancellationToken).ConfigureAwait(false);
            if (survivorGeneration is not null &&
                (!SameSnapshot(survivorGeneration.Vectors, currentMembership) ||
                 survivorGeneration.ExpectedCorpusStamp is { } expectedStamp && expectedStamp != corpusStamp ||
                 survivorGeneration.Generation.CorpusStamp is { } generationStamp && generationStamp != corpusStamp))
            {
                return await CommitUnitAsync(context, transaction, operation, "rebuild-index", cancellationToken,
                    progressed: false).ConfigureAwait(false);
            }

            await CaptureCleanupTargetsAsync(
                context,
                operation,
                revisionIds,
                recordIds,
                targetVectorIds,
                cancellationToken).ConfigureAwait(false);
            if (survivorGeneration is not null)
            {
                await ActivateSurvivorGenerationAsync(context, survivorGeneration, cancellationToken).ConfigureAwait(false);
            }
            if (survivorGeneration is null)
            {
                await ClearProjectionAndValidateEmptyCatalogueAsync(context, cancellationToken).ConfigureAwait(false);
            }
            return await CommitUnitAsync(context, transaction, operation, "cleanup-memberships", cancellationToken).ConfigureAwait(false);
        }).ConfigureAwait(false);
    }


    private async Task CaptureCleanupTargetsAsync(
        FluxKnowledgeDbContext context,
        SourceDeletionOperationEntity operation,
        IQueryable<Guid> revisionIds,
        IQueryable<Guid> recordIds,
        IQueryable<long> targetVectorIds,
        CancellationToken cancellationToken)
    {
        var artifacts = await context.SourceArtifacts.Where(artifact => revisionIds.Contains(artifact.SourceRevisionId))
                .Select(artifact => new { artifact.StoreRelativePath, artifact.ContentSha256, artifact.ByteLength })
                .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        var distinctArtifacts = artifacts.DistinctBy(artifact => new { artifact.StoreRelativePath, artifact.ContentSha256, artifact.ByteLength }).ToArray();
        operation.SourceArtifactCount = distinctArtifacts.Length;
        operation.SharedArtifactCount = 0;
        operation.PipelineRecordCount = await context.PipelineRecords.CountAsync(record => record.SourceRevisionId.HasValue && revisionIds.Contains(record.SourceRevisionId.Value), cancellationToken).ConfigureAwait(false);
        foreach (var artifact in distinctArtifacts)
        {
            // Keep a target even when another source currently shares it. Two
            // deleting roots can both cut over before either drops its reference;
            // the final gated receipt decides preservation or physical removal.
            context.SourceDeletionCleanupItems.Add(new SourceDeletionCleanupItemEntity
            {
                Id = Guid.NewGuid(),
                SourceDeletionOperationId = operation.Id,
                StorageKind = 1,
                RelativePath = artifact.StoreRelativePath,
                ContentSha256 = artifact.ContentSha256,
                ByteLength = artifact.ByteLength,
                State = 0,
                CreatedAtUtc = timeProvider.GetUtcNow(),
                UpdatedAtUtc = timeProvider.GetUtcNow()
            });
        }

        var ownedGenerationIds = (await context.Artifacts.Where(artifact =>
                    recordIds.Contains(artifact.PipelineRecordId) &&
                    artifact.Stage == (int)PipelineStage.Embed &&
                    artifact.ContentType == EmbedDraftDefaults.ArtifactContentType)
                .Select(artifact => artifact.SearchText)
                .ToArrayAsync(cancellationToken).ConfigureAwait(false))
                .Select(value => Guid.TryParse(value, out var generationId) ? generationId : Guid.Empty)
                .Where(static generationId => generationId != Guid.Empty)
                .Distinct()
                .ToArray();
        var checkpointIds = await (from generation in context.IndexGenerations
            join job in context.Jobs on generation.EmbeddingJobId equals job.Id
            where recordIds.Contains(job.PipelineRecordId)
            select generation.Id).ToArrayAsync(cancellationToken).ConfigureAwait(false);
        ownedGenerationIds = ownedGenerationIds.Union(checkpointIds).ToArray();
        IQueryable<Guid>? candidateIds = null;
        if (await targetVectorIds.AnyAsync(cancellationToken).ConfigureAwait(false))
            candidateIds = context.IndexGenerationVectors.Where(membership => targetVectorIds.Contains(membership.VectorId))
                .Select(membership => membership.GenerationId).Distinct();
        if (ownedGenerationIds.Length > 0)
        {
            var ownedIds = context.IndexGenerations.Where(generation => ownedGenerationIds.Contains(generation.Id))
                .Select(generation => generation.Id);
            candidateIds = candidateIds is null ? ownedIds : candidateIds.Union(ownedIds);
        }
        // Discover distinct references before materialising generations. A
        // correlated EXISTS can repeatedly scan the source graph per generation.
        var generations = candidateIds is null ? [] : await (
                from generation in context.IndexGenerations
                join candidateId in candidateIds on generation.Id equals candidateId
                where generation.FormatVersion == 1
                select generation).TagWith("source-delete-generation-targets")
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        foreach (var generation in generations)
        {
            generation.RetiredAtUtc ??= timeProvider.GetUtcNow();
            context.SourceDeletionCleanupItems.Add(new SourceDeletionCleanupItemEntity
            {
                Id = Guid.NewGuid(),
                SourceDeletionOperationId = operation.Id,
                StorageKind = 2,
                RelativePath = generation.Id.ToString("N"),
                State = 0,
                CreatedAtUtc = timeProvider.GetUtcNow(),
                UpdatedAtUtc = timeProvider.GetUtcNow()
            });
        }
        if (await context.IndexState.Where(value => value.Id == 1).Select(value => value.RequiredIndexFormat).SingleAsync(cancellationToken) == 2)
        {
            var segments = await context.IndexSegmentVectors.Where(value => targetVectorIds.Contains(value.VectorId))
                .Select(value => value.SegmentId).Distinct().ToArrayAsync(cancellationToken);
            var manifests = await context.IndexGenerationSegments.Where(value => segments.Contains(value.SegmentId))
                .Select(value => value.GenerationId).Distinct().ToArrayAsync(cancellationToken);
            foreach (var id in manifests) AddIndexTarget(3, id.ToString("N"));
            foreach (var id in segments) AddIndexTarget(4, id);
        }
        void AddIndexTarget(int kind, string path) => context.SourceDeletionCleanupItems.Add(new()
        {
            Id = Guid.NewGuid(), SourceDeletionOperationId = operation.Id, StorageKind = kind, RelativePath = path,
            CreatedAtUtc = timeProvider.GetUtcNow(), UpdatedAtUtc = timeProvider.GetUtcNow()
        });
    }

    private static async Task<bool> DetachTerminalCandidateProvenanceAsync(FluxKnowledgeDbContext context, Guid rootId,
        IQueryable<Guid> recordIds, CancellationToken cancellationToken)
    {
        var jobs = context.Jobs.Where(value => recordIds.Contains(value.PipelineRecordId)).Select(value => value.Id);
        var drafts = context.IndexGenerations.Where(value => value.EmbeddingJobId != null && jobs.Contains(value.EmbeddingJobId.Value)).Select(value => value.Id);
        var owned = context.IndexPublicationCandidates.Where(value => value.OwnerJobId != null && jobs.Contains(value.OwnerJobId.Value) ||
            value.DraftGenerationId != null && drafts.Contains(value.DraftGenerationId.Value) ||
            context.IndexCandidateMembers.Any(member => member.CandidateId == value.Id && member.SourceRootId == rootId));
        if (await owned.AnyAsync(value => value.State < 2, cancellationToken)) return false;
        // Only terminal candidate rows are detached. Shared segments keep their producing candidate and immutable provenance.
        await owned.Where(value => value.OwnerJobId != null).ExecuteUpdateAsync(update => update
            .SetProperty(value => value.RetiredOwnerJobId, value => value.OwnerJobId)
            .SetProperty(value => value.OwnerJobId, (Guid?)null), cancellationToken);
        await owned.Where(value => value.DraftGenerationId != null && drafts.Contains(value.DraftGenerationId.Value)).ExecuteUpdateAsync(update => update
            .SetProperty(value => value.RetiredDraftGenerationId, value => value.DraftGenerationId)
            .SetProperty(value => value.DraftGenerationId, (Guid?)null), cancellationToken);
        return true;
    }

    private async Task ActivateSurvivorGenerationAsync(
        FluxKnowledgeDbContext context,
        IndexGenerationCandidateSnapshot candidate,
        CancellationToken cancellationToken)
    {
        var generation = await context.IndexGenerations.SingleOrDefaultAsync(value => value.Id == candidate.Generation.Id, cancellationToken).ConfigureAwait(false);
        if (generation is null)
        {
            generation = new IndexGenerationEntity
            {
                Id = candidate.Generation.Id,
                ModelFingerprint = candidate.Generation.ModelFingerprint,
                Dimensions = candidate.Generation.Dimensions,
                IndexPath = candidate.Generation.IndexPath,
                MetadataChecksum = candidate.Generation.MetadataChecksum,
                VectorCount = candidate.Generation.VectorCount,
                CorpusEpoch = candidate.Generation.CorpusStamp?.CorpusEpoch,
                CorpusVersion = candidate.Generation.CorpusStamp?.CorpusVersion,
                CreatedAtUtc = timeProvider.GetUtcNow(),
                ValidatedAtUtc = timeProvider.GetUtcNow()
            };
            context.IndexGenerations.Add(generation);
        }
        else if (generation.RetiredAtUtc is not null || !SameGeneration(generation, candidate.Generation))
        {
            throw new InvalidOperationException("The survivor generation identity is incompatible with durable SQL metadata.");
        }

        var existing = await context.IndexGenerationVectors.Where(value => value.GenerationId == candidate.Generation.Id)
            .Select(value => value.VectorId).ToArrayAsync(cancellationToken).ConfigureAwait(false);
        if (existing.Length > 0 && !existing.Order().SequenceEqual(candidate.Vectors.Select(value => value.VectorId).Order()))
        {
            throw new InvalidOperationException("The survivor generation identity is incompatible with durable SQL membership.");
        }
        foreach (var vector in candidate.Vectors)
        {
            if (!existing.Contains(vector.VectorId))
            {
                context.IndexGenerationVectors.Add(new IndexGenerationVectorEntity { GenerationId = candidate.Generation.Id, VectorId = vector.VectorId });
            }
        }

        var state = await context.IndexState.SingleAsync(value => value.Id == 1, cancellationToken).ConfigureAwait(false);
        state.ActiveIndexGenerationId = candidate.Generation.Id;
        state.EmptyCatalogueValidatedAtUtc = null;
        state.UpdatedAtUtc = timeProvider.GetUtcNow();
    }

    private async Task ClearProjectionAndValidateEmptyCatalogueAsync(FluxKnowledgeDbContext context, CancellationToken cancellationToken)
    {
        // The eligibility and draft proof below must include retirements tracked in this transaction.
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        if ((await SqlPublishedPassageSelection.ReadVectorsAsync(context, cancellationToken).ConfigureAwait(false)).Count != 0)
            throw new InvalidOperationException("The source deletion cannot clear a searchable survivor projection.");
        var hasCanonicalVectors = await context.Vectors.AnyAsync(cancellationToken).ConfigureAwait(false);
        var hasOtherUnplacedDrafts = await context.IndexGenerations.AnyAsync(generation =>
            generation.IndexPath == string.Empty && generation.RetiredAtUtc == null, cancellationToken).ConfigureAwait(false);

        var state = await context.IndexState.SingleAsync(value => value.Id == 1, cancellationToken).ConfigureAwait(false);
        state.ActiveIndexGenerationId = null;
        // No search survivors is different from a proven empty canonical catalogue.
        // Other deletion owners or pending sources keep their SQL rows and remain unavailable
        // to semantic search until their own publication/purge finishes.
        state.EmptyCatalogueValidatedAtUtc = hasCanonicalVectors || hasOtherUnplacedDrafts ? null : timeProvider.GetUtcNow();
        state.UpdatedAtUtc = timeProvider.GetUtcNow();
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        if (!hasCanonicalVectors && !hasOtherUnplacedDrafts)
        {
            await context.IndexGenerationVectors.ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
            // Later deletion units still prove exact request/task ownership using
            // this generation and its job/epoch/profile binding. Keep that proof
            // until ordinary owned execution cleanup has removed every request.
            await context.IndexGenerations.Where(generation =>
                    !context.EmbeddingGpuRequests.Any(request => request.GenerationId == generation.Id))
                .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static bool SameSnapshot(IReadOnlyList<CanonicalVector> expected, IReadOnlyList<CanonicalVector> actual) =>
        expected.Count == actual.Count && expected.Zip(actual, static (left, right) =>
            left.VectorId == right.VectorId && left.TextChunkId == right.TextChunkId &&
            left.SourceRevision == right.SourceRevision && left.Values.AsSpan().SequenceEqual(right.Values) &&
            string.Equals(left.TextChunkContentHash, right.TextChunkContentHash, StringComparison.Ordinal) && left.Dimensions == right.Dimensions &&
            string.Equals(left.ModelFingerprint, right.ModelFingerprint, StringComparison.Ordinal) &&
            string.Equals(left.PayloadChecksum, right.PayloadChecksum, StringComparison.Ordinal)).All(static equal => equal);

    private static bool OwnsActiveLease(SourceDeletionOperationEntity operation, SourceDeletionWorkItem workItem, DateTimeOffset now) =>
        operation.State == 1 &&
        operation.SourceRootId == workItem.SourceRootId &&
        operation.LeaseId == workItem.LeaseId &&
        operation.LeaseExpiresAtUtc > now;

    private static void ReleaseLease(SourceDeletionOperationEntity operation)
    {
        operation.LeaseId = null;
        operation.LeaseExpiresAtUtc = null;
    }

    private static bool HasValidDescriptor(IndexGenerationCandidateSnapshot candidate) =>
        candidate.Generation.VectorCount == candidate.Vectors.Count &&
        candidate.Generation.Dimensions > 0 &&
        candidate.Vectors.All(vector =>
            vector.Dimensions == candidate.Generation.Dimensions &&
            string.Equals(vector.ModelFingerprint, candidate.Generation.ModelFingerprint, StringComparison.Ordinal) &&
            vector.Values.Length == checked(vector.Dimensions * sizeof(float))) &&
        string.Equals(
            candidate.Generation.MetadataChecksum,
            ComputeMetadataChecksum(candidate.Generation.ModelFingerprint, candidate.Generation.Dimensions, candidate.Vectors),
            StringComparison.Ordinal);

    private static string ComputeMetadataChecksum(string modelFingerprint, int dimensions, IReadOnlyList<CanonicalVector> vectors)
    {
        var data = $"{modelFingerprint}|cos|{dimensions}|{string.Join(',', vectors.Select(vector => $"{vector.VectorId}:{vector.PayloadChecksum}"))}";
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(data)));
    }

    private static bool SameGeneration(IndexGenerationEntity actual, IndexGenerationDescriptor expected) =>
        string.Equals(actual.ModelFingerprint, expected.ModelFingerprint, StringComparison.Ordinal) &&
        actual.Dimensions == expected.Dimensions &&
        string.Equals(actual.IndexPath, expected.IndexPath, StringComparison.Ordinal) &&
        string.Equals(actual.MetadataChecksum, expected.MetadataChecksum, StringComparison.Ordinal) &&
        actual.VectorCount == expected.VectorCount && actual.CorpusEpoch == expected.CorpusStamp?.CorpusEpoch &&
        actual.CorpusVersion == expected.CorpusStamp?.CorpusVersion;

    private static async Task<bool> HasActiveWorkAsync(
        FluxKnowledgeDbContext context,
        Guid rootId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var revisionIds = SqlSourceDeletionOwnership.RevisionIds(context, rootId);
        var recordIds = SqlSourceDeletionOwnership.RecordIds(context, rootId);
        return await context.SourceScanJobs.AnyAsync(value => value.LeaseOwner != null && value.LeaseExpiresAtUtc > now &&
                context.SourceScanRequests.Any(request => request.Id == value.SourceScanRequestId && request.SourceRootId == rootId), cancellationToken)
            .ConfigureAwait(false) ||
        await context.Jobs.AnyAsync(value => recordIds.Contains(value.PipelineRecordId) &&
                ((value.LeaseOwner != null && value.LeaseExpiresAtUtc > now) ||
                 (value.Operation == FluxKnowledge.Application.Workers.PipelineOperations.ExtractVisio &&
                  value.PublicState == (int)FluxKnowledge.Domain.Jobs.PublicJobState.WorkerProcessing)), cancellationToken)
            .ConfigureAwait(false) ||
        await context.OutboxMessages.AnyAsync(value => recordIds.Contains(value.PipelineRecordId) && value.LeaseOwner != null && value.LeaseExpiresAtUtc > now, cancellationToken)
            .ConfigureAwait(false) ||
        await context.SourceProcessorBranches.AnyAsync(value => revisionIds.Contains(value.SourceRevisionId) && value.LeaseOwner != null && value.LeaseExpiresAtUtc > now, cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task<bool> HasCrossRootDependencyAsync(
        FluxKnowledgeDbContext context,
        Guid rootId,
        CancellationToken cancellationToken)
    {
        var revisionIds = SqlSourceDeletionOwnership.RevisionIds(context, rootId);
        var recordIds = SqlSourceDeletionOwnership.RecordIds(context, rootId);
        return await context.PipelineRecords.AnyAsync(value =>
                !recordIds.Contains(value.Id) &&
                ((value.ParentRevisionRecordId.HasValue && recordIds.Contains(value.ParentRevisionRecordId.Value)) ||
                 recordIds.Contains(value.RootLineageRecordId)), cancellationToken)
            .ConfigureAwait(false) ||
            await context.SourceActivities.AnyAsync(value =>
                !revisionIds.Contains(value.SourceRevisionId) && value.ResultingPipelineRecordId.HasValue &&
                recordIds.Contains(value.ResultingPipelineRecordId.Value), cancellationToken).ConfigureAwait(false);
    }

    private static async Task<bool> DeleteOwnedGraphAsync(
        FluxKnowledgeDbContext context,
        Guid rootId,
        CancellationToken cancellationToken)
    {
        var revisionIds = SqlSourceDeletionOwnership.RevisionIds(context, rootId);
        var recordIds = SqlSourceDeletionOwnership.RecordIds(context, rootId);
        var branches = context.SourceProcessorBranches.Where(value => revisionIds.Contains(value.SourceRevisionId));
        var branchIds = branches.Select(value => value.Id);
        var activities = context.SourceActivities.Where(value => revisionIds.Contains(value.SourceRevisionId));
        var activityIds = activities.Select(value => value.Id);

        await context.SourceScanOutbox.Where(value => context.SourceScanRequests.Any(request => request.Id == value.SourceScanRequestId && request.SourceRootId == rootId))
            .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        await context.SourceScanJobs.Where(value => context.SourceScanRequests.Any(request => request.Id == value.SourceScanRequestId && request.SourceRootId == rootId))
            .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        await context.SourceRootWatchStates.Where(value => value.SourceRootId == rootId).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        await context.SourceScanRequests.Where(value => value.SourceRootId == rootId).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);

        // A publication binds the physical document owner, its hidden document input,
        // retained-processor branch, and the selected pipeline revision.  Remove only
        // publications bound to an owned revision before deleting those principals;
        // preserve either ownership arm and the preceding cross-root refusals.
        if (await revisionIds.AnyAsync(cancellationToken).ConfigureAwait(false))
        {
            await context.DocumentPublications.Where(value =>
                    revisionIds.Contains(value.OwnerSourceRevisionId) ||
                    revisionIds.Contains(value.DocumentInputSourceRevisionId))
                .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        }

        if (await branchIds.AnyAsync(cancellationToken).ConfigureAwait(false))
        {
            var forceRequests = context.SourceProcessorForceRequests
                .Where(value => branchIds.Contains(value.SourceProcessorBranchId) ||
                    revisionIds.Contains(value.SourceRevisionId) || activityIds.Contains(value.SourceActivityId));
            var forceRequestIds = forceRequests.Select(value => value.Id);
            var actions = context.OperatorActionActionLedger
                .Where(value => branchIds.Contains(value.SourceProcessorBranchId) ||
                    (value.SourceProcessorForceRequestId.HasValue && forceRequestIds.Contains(value.SourceProcessorForceRequestId.Value)));
            var actionIds = actions.Select(value => value.ActionId);
            if (await actionIds.AnyAsync(cancellationToken).ConfigureAwait(false))
            {
                await context.OperatorActionOperationLedger.Where(value => actionIds.Contains(value.ActionId))
                    .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
                await context.SourceProcessorActionIgnoreHeads.Where(value => actionIds.Contains(value.ActionId))
                    .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
                await actions.ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
            }
            if (await forceRequestIds.AnyAsync(cancellationToken).ConfigureAwait(false))
            {
                await forceRequests.ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
            }
            await context.SourceProcessorCodeBlockedDiagnostics.Where(value => branchIds.Contains(value.SourceProcessorBranchId)).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
            await context.SourceProcessorCodeCompletionReceipts.Where(value => branchIds.Contains(value.SourceProcessorBranchId)).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
            await context.SourceProcessorCodeDiagnostics.Where(value => branchIds.Contains(value.DocumentId)).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
            await context.SourceProcessorCodeReferences.Where(value => branchIds.Contains(value.DocumentId)).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
            await context.SourceProcessorCodeSymbols.Where(value => branchIds.Contains(value.DocumentId)).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
            await context.SourceProcessorCodeDocuments.Where(value => branchIds.Contains(value.SourceProcessorBranchId)).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
            await context.SourceProcessorBranchMembers.Where(value => branchIds.Contains(value.BranchId)).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
            await context.SourceProcessorAttempts.Where(value => branchIds.Contains(value.BranchId)).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
            await branches.ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        }

        if (await revisionIds.AnyAsync(cancellationToken).ConfigureAwait(false))
        {
            await context.DeferredCapabilities.Where(value => revisionIds.Contains(value.SourceRevisionId)).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        }

        if (await activityIds.AnyAsync(cancellationToken).ConfigureAwait(false))
        {
            await context.SourceActivityRelations.Where(value => activityIds.Contains(value.PredecessorActivityId) || activityIds.Contains(value.SuccessorActivityId)).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
            await activities.ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        }

        if (await recordIds.AnyAsync(cancellationToken).ConfigureAwait(false))
        {
            // The sole caller already proved vectors/chunks/execution empty in
            // this serializable unit. Keep principals for the next payload page.
            if (await DeleteOwnedArtifactPageAsync(context, recordIds, cancellationToken).ConfigureAwait(false)) return true;
            // Records are about to disappear. This one ownership set must stay
            // frozen; all other selectors still have their live SQL principals.
            var identityIds = await context.PipelineRecords.Where(value => recordIds.Contains(value.Id))
                .Select(value => value.SourceIdentityId).Distinct().ToArrayAsync(cancellationToken).ConfigureAwait(false);
            await context.OutboxMessages.Where(value => recordIds.Contains(value.PipelineRecordId)).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
            var ownedJobIds = context.Jobs.Where(value => recordIds.Contains(value.PipelineRecordId)).Select(value => value.Id);
            if (await context.IndexGenerations.AnyAsync(value => value.EmbeddingJobId.HasValue && ownedJobIds.Contains(value.EmbeddingJobId.Value) && value.RetiredAtUtc == null, cancellationToken).ConfigureAwait(false))
                throw new InvalidOperationException("source-delete-checkpoint-not-retired");
            await context.IndexGenerations.Where(value => value.EmbeddingJobId.HasValue && ownedJobIds.Contains(value.EmbeddingJobId.Value))
                .ExecuteUpdateAsync(update => update.SetProperty(value => value.EmbeddingJobId, (Guid?)null), cancellationToken).ConfigureAwait(false);
            await context.Jobs.Where(value => recordIds.Contains(value.PipelineRecordId)).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
            await context.PipelineRecords.Where(value => recordIds.Contains(value.Id)).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
            foreach (var identityPage in identityIds.Chunk(1_000))
                await context.SourceIdentities.Where(value => identityPage.Contains(value.Id) &&
                    !context.PipelineRecords.Any(record => record.SourceIdentityId == value.Id))
                    .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        }

        if (await revisionIds.AnyAsync(cancellationToken).ConfigureAwait(false))
        {
            await context.SourceArtifacts.Where(value => revisionIds.Contains(value.SourceRevisionId)).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
            while (await context.SourceRevisions.Where(value => value.SourceRootId == rootId &&
                    !context.SourceRevisions.Any(child => child.ParentSourceRevisionId == value.Id)).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false) > 0)
            {
            }
        }
        return false;
    }

    private static async Task<LocalExecutionGraph> AssertOwnedGraphPurgeReadyAsync(FluxKnowledgeDbContext context,IQueryable<Guid> recordIds,CancellationToken ct)
    {
        if (await context.IndexGenerations.AnyAsync(generation => generation.EmbeddingJobId.HasValue && generation.RetiredAtUtc == null &&
            context.Jobs.Any(job => job.Id == generation.EmbeddingJobId && recordIds.Contains(job.PipelineRecordId)),ct))
            throw new InvalidOperationException("source-delete-checkpoint-not-retired");
        return await ReadOwnedLocalModelExecutionGraphAsync(context,recordIds,ct);
    }

    private sealed record LocalExecutionGraph(Guid[] MiniTaskIds,Guid[] BatchIds);

    private static async Task<LocalExecutionGraph> ReadOwnedLocalModelExecutionGraphAsync(
        FluxKnowledgeDbContext context,IQueryable<Guid> recordIds,CancellationToken cancellationToken)
    {
        var miniTaskIds = await context.DocumentOcrRequests
            .Where(request => recordIds.Contains(request.PipelineRecordId))
            .Select(request => request.MiniTaskId)
            .Union(context.EmbeddingGpuRequests.Where(request => recordIds.Contains(request.PipelineRecordId)).Select(request => request.MiniTaskId))
            .ToArrayAsync(cancellationToken)
            .ConfigureAwait(false);
        if (await context.EmbeddingGpuRequests.AnyAsync(request => recordIds.Contains(request.PipelineRecordId) &&
            request.ExecutorInstanceId != null && !request.NativeCleanupConfirmed, cancellationToken).ConfigureAwait(false))
            throw new InvalidOperationException("source-delete-embedding-native-cleanup-unconfirmed");
        var batchIds = miniTaskIds.Length == 0
            ? []
            : await context.GpuMiniTasks.Where(task => miniTaskIds.Contains(task.Id) && task.BatchId.HasValue)
                .Select(task => task.BatchId!.Value)
                .Distinct()
                .ToArrayAsync(cancellationToken)
                .ConfigureAwait(false);
        if (batchIds.Length > 0 &&
            await HasForeignBatchTaskAsync(context,miniTaskIds,batchIds,cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("source-delete-local-ocr-shared-batch");
        }
        if (batchIds.Length > 0 &&
            await context.GpuCapacitySlots.AnyAsync(slot => slot.ActiveBatchId.HasValue && batchIds.Contains(slot.ActiveBatchId.Value), cancellationToken)
                .ConfigureAwait(false))
        {
            throw new InvalidOperationException("source-delete-local-ocr-active-batch");
        }

        return new(miniTaskIds,batchIds);
    }

    private async Task<SourceDeletionRunResult> RefuseAsync(
        FluxKnowledgeDbContext context,
        Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction transaction,
        SourceDeletionOperationEntity operation,
        string reasonCode,
        CancellationToken cancellationToken)
    {
        return await CommitUnitAsync(context, transaction, operation, "attention", cancellationToken,
            reasonCode, progressed: false, nextState: 4, continueImmediately: false).ConfigureAwait(false);
    }
}
