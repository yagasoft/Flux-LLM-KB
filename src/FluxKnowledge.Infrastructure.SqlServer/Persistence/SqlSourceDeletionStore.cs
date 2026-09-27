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
/// store is called; it still rechecks every durable relationship under one serializable
/// transaction so a failed cleanup never spills into another root.
/// </summary>
public sealed class SqlSourceDeletionStore(
    IDbContextFactory<FluxKnowledgeDbContext> contextFactory,
    TimeProvider timeProvider,
    PaddleOcrVlmExecutionRegistry? localOcrExecutions = null, IGpuInteractiveOwnerProbe? queryOwnerProbe = null,
    EmbeddingGpuRuntime? embeddingRuntime = null, EmbeddingGpuExecutor? localEmbeddingExecutions = null) : ISourceDeletionStore
{
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(15);

    public SqlSourceDeletionStore(IDbContextFactory<FluxKnowledgeDbContext> contextFactory)
        : this(contextFactory, TimeProvider.System, null)
    {
    }

    public async ValueTask<SourceDeletionWorkItem?> ClaimNextAsync(CancellationToken cancellationToken)
    {
        await using var executionContext = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var strategy = executionContext.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false);
            var now = timeProvider.GetUtcNow();
            var operation = await context.SourceDeletionOperations
                .FromSqlInterpolated($"""
                    SELECT TOP (1) *
                    FROM [SourceDeletionOperations] WITH (UPDLOCK, HOLDLOCK, ROWLOCK)
                    WHERE [State] = {0}
                       OR ([State] = {1} AND ([LeaseExpiresAtUtc] IS NULL OR [LeaseExpiresAtUtc] <= {now}))
                    ORDER BY [CreatedAtUtc], [Id]
                    """)
                .SingleOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);
            if (operation is null)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return null;
            }

            operation.State = 1;
            operation.LeaseId = Guid.NewGuid();
            operation.LeaseExpiresAtUtc = now.Add(LeaseDuration);
            operation.UpdatedAtUtc = now;
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new SourceDeletionWorkItem(operation.Id, operation.SourceRootId, operation.LeaseId.Value, operation.Phase);
        }).ConfigureAwait(false);
    }

    public async ValueTask<SourceDeletionRunResult> PurgeAsync(
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
                var waiting = await context.SourceDeletionOperations.SingleAsync(value => value.Id == workItem.OperationId, cancellationToken).ConfigureAwait(false);
                if (!OwnsActiveLease(waiting, workItem, timeProvider.GetUtcNow()))
                    throw new InvalidOperationException("The source deletion cleanup lease is no longer owned.");
                waiting.State = 0;
                ReleaseLease(waiting);
                waiting.ReasonCode = "source-delete-search-queries-active";
                waiting.UpdatedAtUtc = timeProvider.GetUtcNow();
                await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return new SourceDeletionRunResult(false, waiting.Phase);
            }
            await SqlPublishedPassageSelection.AcquireFenceAsync(context, cancellationToken).ConfigureAwait(false);
            var operation = await context.SourceDeletionOperations
                .FromSqlInterpolated($"""
                    SELECT * FROM [SourceDeletionOperations] WITH (UPDLOCK, HOLDLOCK)
                    WHERE [Id] = {workItem.OperationId}
                    """)
                .SingleAsync(cancellationToken)
                .ConfigureAwait(false);
            if (operation.State == 3)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return new SourceDeletionRunResult(true, operation.Phase);
            }

            if (!OwnsActiveLease(operation, workItem, timeProvider.GetUtcNow()))
            {
                throw new InvalidOperationException("The source deletion operation is no longer owned by this purge step.");
            }

            if (string.Equals(operation.Phase, "cleanup-files", StringComparison.Ordinal))
            {
                if (await context.SourceDeletionCleanupItems.AnyAsync(item => item.SourceDeletionOperationId == operation.Id && item.State == 0, cancellationToken).ConfigureAwait(false))
                {
                    operation.State = 0;
                    ReleaseLease(operation);
                    operation.UpdatedAtUtc = timeProvider.GetUtcNow();
                    await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                    await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                    return new SourceDeletionRunResult(false, operation.Phase);
                }

                await context.SourceDeletionCleanupItems.Where(item => item.SourceDeletionOperationId == operation.Id)
                    .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
                await context.SourceRootConfigurations.Where(value => value.Id == operation.SourceRootId)
                    .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
                operation.State = 3;
                ReleaseLease(operation);
                operation.Phase = "completed";
                operation.ReasonCode = null;
                operation.UpdatedAtUtc = timeProvider.GetUtcNow();
                await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return new SourceDeletionRunResult(true, operation.Phase);
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
                operation.State = 3;
                ReleaseLease(operation);
                operation.Phase = "completed";
                operation.UpdatedAtUtc = timeProvider.GetUtcNow();
                await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return new SourceDeletionRunResult(true, operation.Phase);
            }

            if (root.State != (int)SourceRootState.Deleting)
            {
                return await RefuseAsync(context, transaction, operation, "source-delete-not-fenced", cancellationToken).ConfigureAwait(false);
            }

            if (await context.OutlookCaptureProfiles.AnyAsync(value => value.SourceRootId == root.Id, cancellationToken).ConfigureAwait(false))
            {
                return await RefuseAsync(context, transaction, operation, "outlook-source-delete-unsupported", cancellationToken).ConfigureAwait(false);
            }

            var revisionIds = await context.SourceRevisions
                .Where(value => value.SourceRootId == root.Id)
                .Select(value => value.Id)
                .ToArrayAsync(cancellationToken)
                .ConfigureAwait(false);
            var recordIds = await context.PipelineRecords
                .Where(value => value.SourceRevisionId.HasValue && revisionIds.Contains(value.SourceRevisionId.Value))
                .Select(value => value.Id)
                .ToArrayAsync(cancellationToken)
                .ConfigureAwait(false);
            var sourceGpuTaskIds = recordIds.Length == 0
                ? []
                : await context.GpuMiniTasks.Where(value =>
                        context.Jobs.Any(job => job.Id == value.ParentJobId && recordIds.Contains(job.PipelineRecordId)))
                    .Select(value => value.Id)
                    .ToArrayAsync(cancellationToken)
                    .ConfigureAwait(false);
            var localOcrTaskIds = recordIds.Length == 0
                ? []
                : await context.DocumentOcrRequests.Where(value => recordIds.Contains(value.PipelineRecordId))
                    .Select(value => value.MiniTaskId)
                    .ToArrayAsync(cancellationToken)
                    .ConfigureAwait(false);
            var localOcrTaskSet = localOcrTaskIds.ToHashSet();
            var localEmbeddingTaskIds = embeddingRuntime is null ? [] : await SqlEmbeddingGpuRequestStore.OwnedLocalTasks(context, embeddingRuntime)
                .Where(task => context.Jobs.Any(job => job.Id == task.ParentJobId && recordIds.Contains(job.PipelineRecordId)))
                .Select(task => task.Id).ToArrayAsync(cancellationToken).ConfigureAwait(false);
            var localEmbeddingTaskSet = localEmbeddingTaskIds.ToHashSet();
            if (sourceGpuTaskIds.Any(taskId => !localOcrTaskSet.Contains(taskId) && !localEmbeddingTaskSet.Contains(taskId)) ||
                await SqlEmbeddingGpuRequestStore.HasContradictoryRequestsAsync(context, recordIds, localEmbeddingTaskIds, cancellationToken).ConfigureAwait(false))
            {
                return await RefuseAsync(context, transaction, operation, "source-delete-external-execution-owned", cancellationToken).ConfigureAwait(false);
            }
            var activeLocalTaskIds = sourceGpuTaskIds.Length == 0
                ? []
                : await context.GpuMiniTasks.Where(value =>
                        sourceGpuTaskIds.Contains(value.Id) &&
                        value.ExecutionState == (int)Domain.Gpu.GpuMiniTaskExecutionState.Active)
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

                operation.State = 0;
                ReleaseLease(operation);
                operation.Phase = "draining";
                operation.UpdatedAtUtc = timeProvider.GetUtcNow();
                await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return new SourceDeletionRunResult(false, operation.Phase);
            }
            if (await HasActiveWorkAsync(context, root.Id, revisionIds, recordIds, timeProvider.GetUtcNow(), cancellationToken).ConfigureAwait(false))
            {
                operation.State = 0;
                ReleaseLease(operation);
                operation.Phase = "draining";
                operation.UpdatedAtUtc = timeProvider.GetUtcNow();
                await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return new SourceDeletionRunResult(false, operation.Phase);
            }

            if (await HasCrossRootDependencyAsync(context, revisionIds, recordIds, cancellationToken).ConfigureAwait(false))
            {
                return await RefuseAsync(context, transaction, operation, "source-delete-cross-root-dependency", cancellationToken).ConfigureAwait(false);
            }

            var targetVectorIds = await ReadTargetVectorIdsAsync(context, recordIds, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(operation.Phase, "rebuild-index", StringComparison.Ordinal))
            {
                // Withdraw owned unplaced checkpoints in the same durable phase as
                // their source rows. Recovery between deletion phases must not treat
                // a deliberately withdrawn partial draft as inconsistent provenance.
                await context.IndexGenerations.Where(generation => generation.EmbeddingJobId.HasValue && generation.IndexPath == string.Empty &&
                    context.Jobs.Any(job => job.Id == generation.EmbeddingJobId.Value && recordIds.Contains(job.PipelineRecordId)))
                    .ExecuteUpdateAsync(update => update.SetProperty(generation => generation.RetiredAtUtc, timeProvider.GetUtcNow()), cancellationToken).ConfigureAwait(false);
                if (recordIds.Length > 0)
                {
                    await context.PipelineRecords.Where(record => recordIds.Contains(record.Id))
                        .ExecuteUpdateAsync(setters => setters.SetProperty(record => record.IsDeleted, true), cancellationToken)
                        .ConfigureAwait(false);
                }
                if (targetVectorIds.Length > 0)
                {
                    await context.Vectors.Where(vector => targetVectorIds.Contains(vector.VectorId))
                        .ExecuteUpdateAsync(setters => setters.SetProperty(vector => vector.IsDeleted, true), cancellationToken)
                        .ConfigureAwait(false);
                }

                operation.State = 0;
                ReleaseLease(operation);
                operation.Phase = "rebuild-index";
                operation.ReasonCode = null;
                operation.UpdatedAtUtc = timeProvider.GetUtcNow();
                await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return new SourceDeletionRunResult(false, operation.Phase, ContinueImmediately: true, RequiresIndexBuild: true);
            }

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
                operation.State = 0;
                ReleaseLease(operation);
                operation.Phase = "rebuild-index";
                operation.UpdatedAtUtc = timeProvider.GetUtcNow();
                await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return new SourceDeletionRunResult(false, operation.Phase, ContinueImmediately: true, RequiresIndexBuild: true);
            }

            await CaptureCleanupTargetsAsync(
                context,
                operation,
                revisionIds,
                recordIds,
                targetVectorIds,
                retireAllGenerations: survivorGeneration is null,
                cancellationToken).ConfigureAwait(false);
            if (survivorGeneration is not null)
            {
                await ActivateSurvivorGenerationAsync(context, survivorGeneration, cancellationToken).ConfigureAwait(false);
            }
            // Persist captured retirement in this transaction before releasing checkpoint
            // job foreign keys; database reads must see the same ownership proof.
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await DeleteOwnedGraphAsync(context, root.Id, revisionIds, recordIds, cancellationToken).ConfigureAwait(false);
            if (survivorGeneration is null)
            {
                await ClearProjectionAndValidateEmptyCatalogueAsync(context, cancellationToken).ConfigureAwait(false);
            }
            operation.State = 0;
            ReleaseLease(operation);
            operation.Phase = "cleanup-files";
            operation.ReasonCode = null;
            operation.UpdatedAtUtc = timeProvider.GetUtcNow();
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new SourceDeletionRunResult(false, operation.Phase, ContinueImmediately: true);
        }).ConfigureAwait(false);
    }

    public async ValueTask<IReadOnlyList<SourceDeletionFileTarget>> ReadPendingCleanupAsync(
        SourceDeletionWorkItem workItem,
        CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var operation = await context.SourceDeletionOperations.SingleOrDefaultAsync(value =>
            value.Id == workItem.OperationId && value.SourceRootId == workItem.SourceRootId &&
            value.State == 1 && value.LeaseId == workItem.LeaseId && value.LeaseExpiresAtUtc > timeProvider.GetUtcNow() &&
            value.Phase == "cleanup-files", cancellationToken).ConfigureAwait(false);
        if (operation is null)
        {
            throw new InvalidOperationException("The source deletion cleanup items are no longer owned by this operation.");
        }

        var pending = await context.SourceDeletionCleanupItems
            .Where(item => item.SourceDeletionOperationId == workItem.OperationId && item.State == 0)
            .OrderBy(item => item.StorageKind).ThenBy(item => item.RelativePath).ThenBy(item => item.Id)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var now = timeProvider.GetUtcNow();
        foreach (var item in pending.Where(item => item.StorageKind == 1))
        {
            var shared = await context.SourceArtifacts.AnyAsync(artifact =>
                artifact.ContentSha256 == item.ContentSha256 &&
                artifact.StoreRelativePath == item.RelativePath &&
                artifact.ByteLength == item.ByteLength,
                cancellationToken).ConfigureAwait(false);
            if (!shared)
            {
                continue;
            }

            item.State = 1;
            item.ReasonCode = "source-delete-shared-artifact-preserved";
            item.UpdatedAtUtc = now;
            operation.SharedArtifactCount++;
            operation.UpdatedAtUtc = now;
        }
        if (context.ChangeTracker.HasChanges())
        {
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        return pending
            .Where(item => item.State == 0)
            .Select(item => new SourceDeletionFileTarget(item.Id, item.StorageKind, item.RelativePath, item.ContentSha256, item.ByteLength))
            .ToArray();
    }

    public async ValueTask RecordCleanupResultAsync(
        SourceDeletionWorkItem workItem,
        Guid cleanupItemId,
        SourceDeletionFileResult result,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(result);
        await using var executionContext = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var strategy = executionContext.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false);
            var operation = await context.SourceDeletionOperations.SingleOrDefaultAsync(value =>
                value.Id == workItem.OperationId && value.SourceRootId == workItem.SourceRootId &&
                value.State == 1 && value.LeaseId == workItem.LeaseId && value.LeaseExpiresAtUtc > timeProvider.GetUtcNow(), cancellationToken).ConfigureAwait(false);
            var item = await context.SourceDeletionCleanupItems.SingleOrDefaultAsync(value => value.Id == cleanupItemId && value.SourceDeletionOperationId == workItem.OperationId, cancellationToken).ConfigureAwait(false);
            if (operation is null || item is null || !string.Equals(operation.Phase, "cleanup-files", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("The source deletion cleanup item is no longer owned by this operation.");
            }

            item.State = result.Completed ? 1 : 2;
            item.ReasonCode = result.ReasonCode;
            item.UpdatedAtUtc = timeProvider.GetUtcNow();
            operation.UpdatedAtUtc = item.UpdatedAtUtc;
            if (!result.Completed)
            {
                operation.State = 4;
                ReleaseLease(operation);
                operation.Phase = "attention";
                operation.ReasonCode = result.ReasonCode ?? "source-delete-file-cleanup-failed";
            }
            else
            {
                operation.State = 0;
                ReleaseLease(operation);
            }

            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }).ConfigureAwait(false);
    }

    public async ValueTask FailAsync(SourceDeletionWorkItem workItem, string reasonCode, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var operation = await context.SourceDeletionOperations.SingleOrDefaultAsync(value =>
            value.Id == workItem.OperationId && value.SourceRootId == workItem.SourceRootId &&
            value.State == 1 && value.LeaseId == workItem.LeaseId && value.LeaseExpiresAtUtc > timeProvider.GetUtcNow(), cancellationToken).ConfigureAwait(false);
        if (operation is null)
        {
            return;
        }

        operation.State = 4;
        ReleaseLease(operation);
        operation.Phase = "attention";
        operation.ReasonCode = reasonCode;
        operation.UpdatedAtUtc = timeProvider.GetUtcNow();
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<long[]> ReadTargetVectorIdsAsync(
        FluxKnowledgeDbContext context,
        IReadOnlyCollection<Guid> recordIds,
        CancellationToken cancellationToken)
    {
        if (recordIds.Count == 0)
        {
            return [];
        }

        return await (
                from vector in context.Vectors
                join chunk in context.TextChunks on vector.TextChunkId equals chunk.Id
                join artifact in context.Artifacts on chunk.ArtifactId equals artifact.Id
                where recordIds.Contains(artifact.PipelineRecordId)
                select vector.VectorId)
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task CaptureCleanupTargetsAsync(
        FluxKnowledgeDbContext context,
        SourceDeletionOperationEntity operation,
        IReadOnlyCollection<Guid> revisionIds,
        IReadOnlyCollection<Guid> recordIds,
        IReadOnlyCollection<long> targetVectorIds,
        bool retireAllGenerations,
        CancellationToken cancellationToken)
    {
        var artifacts = revisionIds.Count == 0
            ? []
            : await context.SourceArtifacts.Where(artifact => revisionIds.Contains(artifact.SourceRevisionId))
                .Select(artifact => new { artifact.StoreRelativePath, artifact.ContentSha256, artifact.ByteLength })
                .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        var distinctArtifacts = artifacts.DistinctBy(artifact => new { artifact.StoreRelativePath, artifact.ContentSha256, artifact.ByteLength }).ToArray();
        operation.SourceArtifactCount = distinctArtifacts.Length;
        operation.PipelineRecordCount = await context.PipelineRecords.CountAsync(record => record.SourceRevisionId.HasValue && revisionIds.Contains(record.SourceRevisionId.Value), cancellationToken).ConfigureAwait(false);
        foreach (var artifact in distinctArtifacts)
        {
            var shared = await context.SourceArtifacts.AnyAsync(candidate =>
                !revisionIds.Contains(candidate.SourceRevisionId) &&
                candidate.StoreRelativePath == artifact.StoreRelativePath &&
                candidate.ContentSha256 == artifact.ContentSha256 &&
                candidate.ByteLength == artifact.ByteLength, cancellationToken).ConfigureAwait(false);
            if (shared)
            {
                operation.SharedArtifactCount++;
                continue;
            }

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

        var ownedGenerationIds = recordIds.Count == 0
            ? []
            : (await context.Artifacts.Where(artifact =>
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
        var generations = retireAllGenerations
            ? await context.IndexGenerations.Where(generation => generation.IndexPath != string.Empty ||
                ownedGenerationIds.Contains(generation.Id)).ToArrayAsync(cancellationToken).ConfigureAwait(false)
            : targetVectorIds.Count == 0 && ownedGenerationIds.Length == 0
                ? []
                : await (
                    from generation in context.IndexGenerations
                    where ownedGenerationIds.Contains(generation.Id) ||
                          context.IndexGenerationVectors.Any(membership =>
                              membership.GenerationId == generation.Id &&
                              targetVectorIds.Contains(membership.VectorId))
                    select generation)
                .Distinct()
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
            await context.IndexGenerations.ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
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
            string.Equals(vector.PayloadChecksum, Convert.ToHexStringLower(SHA256.HashData(vector.Values)), StringComparison.Ordinal)) &&
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
        IReadOnlyCollection<Guid> revisionIds,
        IReadOnlyCollection<Guid> recordIds,
        DateTimeOffset now,
        CancellationToken cancellationToken) =>
        await context.SourceScanJobs.AnyAsync(value => value.LeaseOwner != null && value.LeaseExpiresAtUtc > now &&
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

    private static async Task<bool> HasCrossRootDependencyAsync(
        FluxKnowledgeDbContext context,
        IReadOnlyCollection<Guid> revisionIds,
        IReadOnlyCollection<Guid> recordIds,
        CancellationToken cancellationToken)
    {
        if (recordIds.Count == 0)
        {
            return false;
        }

        return await context.PipelineRecords.AnyAsync(value =>
                !recordIds.Contains(value.Id) &&
                ((value.ParentRevisionRecordId.HasValue && recordIds.Contains(value.ParentRevisionRecordId.Value)) ||
                 recordIds.Contains(value.RootLineageRecordId)), cancellationToken)
            .ConfigureAwait(false) ||
            await context.SourceActivities.AnyAsync(value =>
                !revisionIds.Contains(value.SourceRevisionId) && value.ResultingPipelineRecordId.HasValue &&
                recordIds.Contains(value.ResultingPipelineRecordId.Value), cancellationToken).ConfigureAwait(false);
    }

    private static async Task DeleteOwnedGraphAsync(
        FluxKnowledgeDbContext context,
        Guid rootId,
        IReadOnlyCollection<Guid> revisionIds,
        IReadOnlyCollection<Guid> recordIds,
        CancellationToken cancellationToken)
    {
        var branchIds = revisionIds.Count == 0
            ? []
            : await context.SourceProcessorBranches.Where(value => revisionIds.Contains(value.SourceRevisionId))
                .Select(value => value.Id).ToArrayAsync(cancellationToken).ConfigureAwait(false);
        var activityIds = revisionIds.Count == 0
            ? []
            : await context.SourceActivities.Where(value => revisionIds.Contains(value.SourceRevisionId))
                .Select(value => value.Id).ToArrayAsync(cancellationToken).ConfigureAwait(false);
        var scanRequestIds = await context.SourceScanRequests.Where(value => value.SourceRootId == rootId)
            .Select(value => value.Id).ToArrayAsync(cancellationToken).ConfigureAwait(false);
        var artifactIds = recordIds.Count == 0
            ? []
            : await context.Artifacts.Where(value => recordIds.Contains(value.PipelineRecordId))
                .Select(value => value.Id).ToArrayAsync(cancellationToken).ConfigureAwait(false);
        var chunkIds = artifactIds.Length == 0
            ? []
            : await context.TextChunks.Where(value => artifactIds.Contains(value.ArtifactId))
                .Select(value => value.Id).ToArrayAsync(cancellationToken).ConfigureAwait(false);

        await DeleteOwnedLocalModelExecutionGraphAsync(context, recordIds, cancellationToken).ConfigureAwait(false);

        await context.AuditEvents.Where(value => value.SourceRootId == rootId ||
                (value.SourceScanRequestId.HasValue && scanRequestIds.Contains(value.SourceScanRequestId.Value)) ||
                (value.SourceRevisionId.HasValue && revisionIds.Contains(value.SourceRevisionId.Value)) ||
                (value.SourceActivityId.HasValue && activityIds.Contains(value.SourceActivityId.Value)) ||
                (value.PipelineRecordId.HasValue && recordIds.Contains(value.PipelineRecordId.Value)))
            .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        await context.SourceScanOutbox.Where(value => context.SourceScanRequests.Any(request => request.Id == value.SourceScanRequestId && request.SourceRootId == rootId))
            .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        await context.SourceScanJobs.Where(value => context.SourceScanRequests.Any(request => request.Id == value.SourceScanRequestId && request.SourceRootId == rootId))
            .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        await context.SourceRootWatchStates.Where(value => value.SourceRootId == rootId).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        await context.SourceScanRequests.Where(value => value.SourceRootId == rootId).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);

        // A publication binds the physical document owner, its hidden document input,
        // retained-processor branch, and the selected pipeline revision.  Remove only
        // publications wholly owned by this fenced source before deleting any of those
        // principals; cross-root dependencies were refused before this graph is reached.
        if (revisionIds.Count > 0)
        {
            await context.DocumentPublications.Where(value =>
                    revisionIds.Contains(value.OwnerSourceRevisionId) ||
                    revisionIds.Contains(value.DocumentInputSourceRevisionId))
                .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        }

        if (branchIds.Length > 0)
        {
            var forceRequestIds = await context.SourceProcessorForceRequests
                .Where(value => branchIds.Contains(value.SourceProcessorBranchId) ||
                    revisionIds.Contains(value.SourceRevisionId) || activityIds.Contains(value.SourceActivityId))
                .Select(value => value.Id)
                .ToArrayAsync(cancellationToken).ConfigureAwait(false);
            var actionIds = await context.OperatorActionActionLedger
                .Where(value => branchIds.Contains(value.SourceProcessorBranchId) ||
                    (value.SourceProcessorForceRequestId.HasValue && forceRequestIds.Contains(value.SourceProcessorForceRequestId.Value)))
                .Select(value => value.ActionId)
                .ToArrayAsync(cancellationToken).ConfigureAwait(false);
            if (actionIds.Length > 0)
            {
                await context.OperatorActionOperationLedger.Where(value => actionIds.Contains(value.ActionId))
                    .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
                await context.SourceProcessorActionIgnoreHeads.Where(value => actionIds.Contains(value.ActionId))
                    .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
                await context.OperatorActionActionLedger.Where(value => actionIds.Contains(value.ActionId))
                    .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
            }
            if (forceRequestIds.Length > 0)
            {
                await context.SourceProcessorForceRequests.Where(value => forceRequestIds.Contains(value.Id))
                    .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
            }
            await context.SourceProcessorCodeBlockedDiagnostics.Where(value => branchIds.Contains(value.SourceProcessorBranchId)).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
            await context.SourceProcessorCodeCompletionReceipts.Where(value => branchIds.Contains(value.SourceProcessorBranchId)).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
            await context.SourceProcessorCodeDiagnostics.Where(value => branchIds.Contains(value.DocumentId)).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
            await context.SourceProcessorCodeReferences.Where(value => branchIds.Contains(value.DocumentId)).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
            await context.SourceProcessorCodeSymbols.Where(value => branchIds.Contains(value.DocumentId)).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
            await context.SourceProcessorCodeDocuments.Where(value => branchIds.Contains(value.SourceProcessorBranchId)).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
            await context.SourceProcessorBranchMembers.Where(value => branchIds.Contains(value.BranchId)).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
            await context.SourceProcessorAttempts.Where(value => branchIds.Contains(value.BranchId)).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
            await context.SourceProcessorBranches.Where(value => branchIds.Contains(value.Id)).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        }

        if (revisionIds.Count > 0)
        {
            await context.DeferredCapabilities.Where(value => revisionIds.Contains(value.SourceRevisionId)).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        }

        if (activityIds.Length > 0)
        {
            await context.SourceActivityRelations.Where(value => activityIds.Contains(value.PredecessorActivityId) || activityIds.Contains(value.SuccessorActivityId)).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
            await context.SourceActivities.Where(value => activityIds.Contains(value.Id)).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        }

        if (chunkIds.Length > 0)
        {
            var vectorIds = await context.Vectors.Where(value => chunkIds.Contains(value.TextChunkId)).Select(value => value.VectorId).ToArrayAsync(cancellationToken).ConfigureAwait(false);
            if (vectorIds.Length > 0)
            {
                await context.IndexGenerationVectors.Where(value => vectorIds.Contains(value.VectorId)).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
                await context.Vectors.Where(value => vectorIds.Contains(value.VectorId)).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
            }
            await context.TextChunks.Where(value => chunkIds.Contains(value.Id)).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        }

        if (recordIds.Count > 0)
        {
            var identityIds = await context.PipelineRecords.Where(value => recordIds.Contains(value.Id)).Select(value => value.SourceIdentityId).ToArrayAsync(cancellationToken).ConfigureAwait(false);
            await context.Artifacts.Where(value => recordIds.Contains(value.PipelineRecordId)).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
            await context.OutboxMessages.Where(value => recordIds.Contains(value.PipelineRecordId)).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
            var ownedJobIds = await context.Jobs.Where(value => recordIds.Contains(value.PipelineRecordId)).Select(value => value.Id).ToArrayAsync(cancellationToken).ConfigureAwait(false);
            if (await context.IndexGenerations.AnyAsync(value => value.EmbeddingJobId.HasValue && ownedJobIds.Contains(value.EmbeddingJobId.Value) && value.RetiredAtUtc == null, cancellationToken).ConfigureAwait(false))
                throw new InvalidOperationException("source-delete-checkpoint-not-retired");
            await context.IndexGenerations.Where(value => value.EmbeddingJobId.HasValue && ownedJobIds.Contains(value.EmbeddingJobId.Value))
                .ExecuteUpdateAsync(update => update.SetProperty(value => value.EmbeddingJobId, (Guid?)null), cancellationToken).ConfigureAwait(false);
            await context.Jobs.Where(value => recordIds.Contains(value.PipelineRecordId)).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
            await context.PipelineRecords.Where(value => recordIds.Contains(value.Id)).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
            await context.SourceIdentities.Where(value => identityIds.Contains(value.Id) && !context.PipelineRecords.Any(record => record.SourceIdentityId == value.Id)).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        }

        if (revisionIds.Count > 0)
        {
            await context.SourceArtifacts.Where(value => revisionIds.Contains(value.SourceRevisionId)).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
            while (await context.SourceRevisions.Where(value => value.SourceRootId == rootId &&
                    !context.SourceRevisions.Any(child => child.ParentSourceRevisionId == value.Id)).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false) > 0)
            {
            }
        }
    }

    private static async Task DeleteOwnedLocalModelExecutionGraphAsync(
        FluxKnowledgeDbContext context,
        IReadOnlyCollection<Guid> recordIds,
        CancellationToken cancellationToken)
    {
        if (recordIds.Count == 0)
        {
            return;
        }

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
            await context.GpuMiniTasks.AnyAsync(
                    task => task.BatchId.HasValue && batchIds.Contains(task.BatchId.Value) && !miniTaskIds.Contains(task.Id),
                    cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("source-delete-local-ocr-shared-batch");
        }
        if (batchIds.Length > 0 &&
            await context.GpuCapacitySlots.AnyAsync(slot => slot.ActiveBatchId.HasValue && batchIds.Contains(slot.ActiveBatchId.Value), cancellationToken)
                .ConfigureAwait(false))
        {
            throw new InvalidOperationException("source-delete-local-ocr-active-batch");
        }

        if (batchIds.Length > 0)
        {
            await context.GpuExecutorEvidence.Where(value => batchIds.Contains(value.BatchId))
                .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
            await context.GpuExecutorResultReceipts.Where(value => batchIds.Contains(value.BatchId))
                .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
            await context.GpuExecutorDispatches.Where(value => batchIds.Contains(value.BatchId))
                .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
            await context.GpuSchedulerOperationReceipts.Where(value => value.BatchId.HasValue && batchIds.Contains(value.BatchId.Value))
                .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        }

        if (miniTaskIds.Length > 0)
        {
            await context.EmbeddingGpuRequests.Where(value => miniTaskIds.Contains(value.MiniTaskId))
                .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
            await context.DocumentOcrRequests.Where(value => miniTaskIds.Contains(value.MiniTaskId))
                .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
            await context.GpuMiniTasks.Where(value => miniTaskIds.Contains(value.Id))
                .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        }
        // A malformed or pre-scheduler request can lack its mini task. It is still owned by
        // the fenced record and must not survive after the parent record is removed.
        await context.DocumentOcrRequests.Where(value => recordIds.Contains(value.PipelineRecordId))
            .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        await context.EmbeddingGpuRequests.Where(value => recordIds.Contains(value.PipelineRecordId))
            .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);

        if (batchIds.Length > 0)
        {
            await context.GpuBatches.Where(value => batchIds.Contains(value.Id))
                .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<SourceDeletionRunResult> RefuseAsync(
        FluxKnowledgeDbContext context,
        Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction transaction,
        SourceDeletionOperationEntity operation,
        string reasonCode,
        CancellationToken cancellationToken)
    {
        operation.State = 4;
        ReleaseLease(operation);
        operation.Phase = "attention";
        operation.ReasonCode = reasonCode;
        operation.UpdatedAtUtc = timeProvider.GetUtcNow();
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new SourceDeletionRunResult(false, operation.Phase, reasonCode);
    }
}
