using System.Data;
using System.Security.Cryptography;
using System.Text;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Domain.Jobs;
using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence;

/// <summary>
/// Owns the SQL portion of a local-source deletion.  The root is fenced before this
/// store is called; it still rechecks every durable relationship under one serializable
/// transaction so a failed cleanup never spills into another root.
/// </summary>
public sealed class SqlSourceDeletionStore(
    IDbContextFactory<FluxKnowledgeDbContext> contextFactory,
    TimeProvider timeProvider) : ISourceDeletionStore
{
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(15);

    public SqlSourceDeletionStore(IDbContextFactory<FluxKnowledgeDbContext> contextFactory)
        : this(contextFactory, TimeProvider.System)
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
            if (recordIds.Length > 0 && await context.GpuMiniTasks.AnyAsync(value =>
                    context.Jobs.Any(job => job.Id == value.ParentJobId && recordIds.Contains(job.PipelineRecordId)),
                    cancellationToken).ConfigureAwait(false))
            {
                return await RefuseAsync(context, transaction, operation, "source-delete-external-execution-owned", cancellationToken).ConfigureAwait(false);
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

            var currentMembership = await ReadEligibleVectorsAsync(context, cancellationToken).ConfigureAwait(false);
            if (survivorGeneration is null && currentMembership.Count != 0)
            {
                return await RefuseAsync(context, transaction, operation, "source-delete-index-publisher-unavailable", cancellationToken).ConfigureAwait(false);
            }
            if (survivorGeneration is not null && !HasValidDescriptor(survivorGeneration))
            {
                return await RefuseAsync(context, transaction, operation, "source-delete-index-candidate-invalid", cancellationToken).ConfigureAwait(false);
            }
            if (survivorGeneration is not null && !SameSnapshot(survivorGeneration.Vectors, currentMembership))
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
                targetVectorIds,
                retireAllGenerations: survivorGeneration is null,
                cancellationToken).ConfigureAwait(false);
            if (survivorGeneration is not null)
            {
                await ActivateSurvivorGenerationAsync(context, survivorGeneration, cancellationToken).ConfigureAwait(false);
            }
            await DeleteOwnedGraphAsync(context, root.Id, revisionIds, recordIds, cancellationToken).ConfigureAwait(false);
            if (survivorGeneration is null)
            {
                await MarkEmptyCatalogueAsync(context, cancellationToken).ConfigureAwait(false);
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

    private static Task<List<CanonicalVector>> ReadEligibleVectorsAsync(
        FluxKnowledgeDbContext context,
        CancellationToken cancellationToken) =>
        (
            from vector in context.Vectors
            join chunk in context.TextChunks on vector.TextChunkId equals chunk.Id
            join artifact in context.Artifacts on chunk.ArtifactId equals artifact.Id
            join record in context.PipelineRecords on artifact.PipelineRecordId equals record.Id
            where !vector.IsDeleted && !record.IsDeleted &&
                  (record.SourceRevisionId.HasValue
                      ? context.SourceRevisions.Any(revision => revision.Id == record.SourceRevisionId.Value && revision.SuppressedAtUtc == null)
                      : record.Revision == context.PipelineRecords.Where(candidate => candidate.SourceIdentityId == record.SourceIdentityId).Max(candidate => candidate.Revision))
            orderby vector.VectorId
            select new CanonicalVector(vector.VectorId, vector.TextChunkId, vector.ModelFingerprint, vector.Dimensions,
                vector.Values, vector.TextChunkContentHash, vector.PayloadChecksum, vector.SourceRevision))
        .ToListAsync(cancellationToken);

    private async Task CaptureCleanupTargetsAsync(
        FluxKnowledgeDbContext context,
        SourceDeletionOperationEntity operation,
        IReadOnlyCollection<Guid> revisionIds,
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

        var generations = retireAllGenerations
            ? await context.IndexGenerations.ToArrayAsync(cancellationToken).ConfigureAwait(false)
            : targetVectorIds.Count == 0
                ? []
                : await (
                    from membership in context.IndexGenerationVectors
                    join generation in context.IndexGenerations on membership.GenerationId equals generation.Id
                    where targetVectorIds.Contains(membership.VectorId)
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

    private async Task MarkEmptyCatalogueAsync(FluxKnowledgeDbContext context, CancellationToken cancellationToken)
    {
        if (await context.Vectors.AnyAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("The source deletion cannot mark an empty catalogue while vectors remain.");
        }

        var state = await context.IndexState.SingleAsync(value => value.Id == 1, cancellationToken).ConfigureAwait(false);
        state.ActiveIndexGenerationId = null;
        state.EmptyCatalogueValidatedAtUtc = timeProvider.GetUtcNow();
        state.UpdatedAtUtc = timeProvider.GetUtcNow();
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await context.IndexGenerationVectors.ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        await context.IndexGenerations.ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
    }

    private static bool SameSnapshot(IReadOnlyList<CanonicalVector> expected, IReadOnlyList<CanonicalVector> actual) =>
        expected.Count == actual.Count && expected.Zip(actual, static (left, right) =>
            left.VectorId == right.VectorId && left.Dimensions == right.Dimensions &&
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
        actual.VectorCount == expected.VectorCount;

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
                value.LeaseOwner != null && value.LeaseExpiresAtUtc > now, cancellationToken)
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
