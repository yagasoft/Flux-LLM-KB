using System.Data;
using FluxKnowledge.Application.Gpu;
using FluxKnowledge.Application.Indexing;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence;

public sealed record SegmentRetirementPlan(Guid OperationId, IndexGenerationDescriptor Generation, IReadOnlyList<IndexSegmentDescriptor> Segments);

/// <summary>Exact retirement in two committed phases; no waiting for native disposal under either SQL fence.</summary>
public sealed partial class SqlSegmentRetirementOperations(IDbContextFactory<FluxKnowledgeDbContext> contexts,
    IIndexProjectionFileStore files, string indexRoot, string recoveryRoot, TimeProvider clock, IGpuInteractiveOwnerProbe probe) : IIndexSourceDeletionMaintenance
{
    internal Func<string, FluxKnowledgeDbContext, CancellationToken, ValueTask>? FileOperationCheckpoint { get; init; }
    public async ValueTask<SegmentRetirementPlan> MarkManifestAsync(Guid generationId, Guid operationId, CancellationToken cancellationToken,
        SourceDeletionWorkItem? sourceDeletion = null)
    {
        if (operationId == Guid.Empty) throw new ArgumentException("segment-retirement-operation-invalid");
        await using var strategy = await contexts.CreateDbContextAsync(cancellationToken);
        return await strategy.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var context = await contexts.CreateDbContextAsync(cancellationToken);
            await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
            if (!await EnterAsync(context, cancellationToken)) throw new InvalidOperationException("segment-retirement-query-owned");
            await ValidateSourceDeletionAsync(context, sourceDeletion, 3, generationId.ToString("N"), cancellationToken);
            var generation = await context.IndexGenerations.SingleAsync(value => value.Id == generationId, cancellationToken);
            await CheckManifestPinsAsync(context, generation, cancellationToken, sourceDeletion?.OperationId);
            if (generation.RetirementOperationId is not null && generation.RetirementOperationId != operationId)
                throw new InvalidOperationException("segment-retirement-operation-conflict");
            var ids = await context.IndexGenerationSegments.Where(value => value.GenerationId == generationId).Select(value => value.SegmentId).ToArrayAsync(cancellationToken);
            generation.RetirementOperationId = operationId;
            generation.RetiredAtUtc ??= clock.GetUtcNow();
            await context.IndexGenerationSegments.Where(value => value.GenerationId == generationId).ExecuteDeleteAsync(cancellationToken);
            foreach (var id in ids)
            {
                if (await context.IndexGenerationSegments.AnyAsync(value => value.SegmentId == id, cancellationToken)) continue;
                if (sourceDeletion is not null && !await context.SourceDeletionCleanupItems.AnyAsync(value =>
                    value.SourceDeletionOperationId == sourceDeletion.OperationId && value.StorageKind == 4 && value.RelativePath == id, cancellationToken))
                    continue; // Unrelated newly unshared segments belong to ordinary retention, not this source's cleanup authority.
                var segment = await context.IndexSegments.SingleAsync(value => value.Id == id, cancellationToken);
                if (!await SegmentHasPinsAsync(context, segment, cancellationToken, sourceDeletion?.OperationId))
                {
                    if (segment.State == 2 && segment.RetirementOperationId == operationId) continue;
                    if (segment.State != 1) throw new InvalidOperationException("segment-retirement-state-changed");
                    segment.State = 2; segment.RetirementOperationId = operationId;
                }
            }
            await context.SaveChangesAsync(cancellationToken);
            var targets = await context.IndexSegments.AsNoTracking().Where(value => value.RetirementOperationId == operationId).OrderBy(value => value.Id).ToArrayAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new SegmentRetirementPlan(operationId, SqlSegmentedIndexStore.Describe(generation), targets.Select(SqlSegmentedIndexStore.Describe).ToArray());
        });
    }

    public async ValueTask<bool> TryReclaimSegmentAsync(string segmentId, Guid operationId, CancellationToken cancellationToken,
        SourceDeletionWorkItem? sourceDeletion = null)
    {
        // Filesystem work must not be replayed by an EF retry strategy. A repeated call rechecks the durable exact intent.
        await using var context = await NewFileOperationContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        var originalTransaction = (SqlTransaction)transaction.GetDbTransaction();
        if (!await EnterAsync(context, cancellationToken)) return false;
        if (FileOperationCheckpoint is { } admitted) await admitted("admitted", context, cancellationToken);
        await ValidateSourceDeletionAsync(context, sourceDeletion, 4, segmentId, cancellationToken);
        var segment = await context.IndexSegments.SingleAsync(value => value.Id == segmentId, cancellationToken);
        if (segment.RetirementOperationId != operationId || segment.State is not (2 or 3))
            throw new InvalidOperationException("segment-retirement-intent-mismatch");
        if (segment.State == 3) return true;
        if (await SegmentHasPinsAsync(context, segment, cancellationToken, sourceDeletion?.OperationId)) return false;
        var owners = await context.IndexSegmentOwners.Where(value => value.SegmentId == segmentId).ToArrayAsync(cancellationToken);
        foreach (var owner in owners)
        {
            // Session loss and lease age are never disposal proof. Inaccessible/foreign incarnations retain ownership.
            if (probe.Observe(new(owner.OwnerProcessId, owner.OwnerStartedAtUtc, owner.OwnerMachineFingerprint)) != GpuInteractiveOwnerObservation.Exited)
                return false;
            context.IndexSegmentOwners.Remove(owner);
        }
        RequireSegmentPath(segment);
        if (FileOperationCheckpoint is { } deleting) await deleting("before-delete", context, cancellationToken);
        await ProveOriginalOwnershipAsync(context, originalTransaction, cancellationToken);
        await files.DeleteSegmentAsync(SqlSegmentedIndexStore.Describe(segment), cancellationToken);
        await context.IndexSegmentVectors.Where(value => value.SegmentId == segmentId).ExecuteDeleteAsync(cancellationToken);
        segment.State = 3; segment.ReclaimedAtUtc = clock.GetUtcNow();
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async ValueTask<bool> TryReclaimManifestAsync(Guid generationId, Guid operationId, CancellationToken cancellationToken,
        SourceDeletionWorkItem? sourceDeletion = null)
    {
        await using var context = await NewFileOperationContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        var originalTransaction = (SqlTransaction)transaction.GetDbTransaction();
        if (!await EnterAsync(context, cancellationToken)) return false;
        if (FileOperationCheckpoint is { } admitted) await admitted("admitted", context, cancellationToken);
        await ValidateSourceDeletionAsync(context, sourceDeletion, 3, generationId.ToString("N"), cancellationToken);
        var generation = await context.IndexGenerations.SingleAsync(value => value.Id == generationId, cancellationToken);
        if (generation.RetirementOperationId != operationId || generation.RetiredAtUtc is null)
            throw new InvalidOperationException("segment-retirement-intent-mismatch");
        if (generation.ReclaimedAtUtc is not null) return true;
        await CheckManifestPinsAsync(context, generation, cancellationToken, sourceDeletion?.OperationId);
        if (await context.IndexGenerationSegments.AnyAsync(value => value.GenerationId == generationId, cancellationToken) ||
            await context.IndexSegments.AnyAsync(value => value.RetirementOperationId == operationId && value.State != 3, cancellationToken)) return false;
        RequireManifestPath(generation);
        if (FileOperationCheckpoint is { } deleting) await deleting("before-delete", context, cancellationToken);
        await ProveOriginalOwnershipAsync(context, originalTransaction, cancellationToken);
        await files.DeleteManifestAsync(SqlSegmentedIndexStore.Describe(generation), cancellationToken);
        generation.ReclaimedAtUtc = clock.GetUtcNow();
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    private async Task<bool> EnterAsync(FluxKnowledgeDbContext context, CancellationToken cancellationToken)
    {
        await SqlIndexRecoveryReferences.AcquireReadTransactionAsync(context, cancellationToken);
        if (!await SqlCorpusGenerationLeaseStore.TryAcquireCleanupTransactionAsync(context, probe, cancellationToken)) return false;
        await SqlPublishedPassageSelection.AcquireFenceAsync(context, cancellationToken);
        await SqlIndexRecoveryReferences.RequireCurrentAsync(context, recoveryRoot, indexRoot, cancellationToken);
        return true;
    }

    private static async Task CheckManifestPinsAsync(FluxKnowledgeDbContext context, IndexGenerationEntity generation, CancellationToken cancellationToken,
        Guid? sourceDeletionOperation = null)
    {
        if (generation.FormatVersion != 2 || generation.ValidatedAtUtc is null || generation.EmbeddingJobId is not null ||
            await context.IndexState.AnyAsync(value => value.ActiveIndexGenerationId == generation.Id || value.CorpusRebuildOperationId != null, cancellationToken) ||
            await context.CorpusQueryLeases.AnyAsync(value => value.GenerationId == generation.Id, cancellationToken) ||
            await context.IndexPublicationCandidates.AnyAsync(value => value.State < 2 &&
                (value.BaseGenerationId == generation.Id || value.DraftGenerationId == generation.Id || value.ResultGenerationId == generation.Id), cancellationToken) ||
            await context.Vectors.AnyAsync(value => value.IndexGenerationId == generation.Id, cancellationToken) ||
            await context.EmbeddingGpuRequests.AnyAsync(value => value.GenerationId == generation.Id, cancellationToken) ||
            await context.SourceDeletionCleanupItems.AnyAsync(value => value.State != 1 && value.RelativePath == generation.Id.ToString("N") &&
                !(sourceDeletionOperation != null && value.StorageKind == 3 && (value.SourceDeletionOperationId == sourceDeletionOperation ||
                  context.SourceDeletionOperations.Any(operation => operation.Id == value.SourceDeletionOperationId &&
                    context.SourceRootConfigurations.Any(root => root.Id == operation.SourceRootId && root.State == (int)FluxKnowledge.Domain.Sources.SourceRootState.Deleting)))), cancellationToken))
            throw new InvalidOperationException("segment-retirement-manifest-referenced");
        var id = generation.Id.ToString("N"); var dashed = generation.Id.ToString("D");
        if (await context.IndexRecoveryPins.AnyAsync(value => value.GenerationId == generation.Id || value.IndexPath == generation.IndexPath, cancellationToken) ||
            await context.NativeOperationIntents.AnyAsync(value => value.TargetMetadataJson.Contains(id) || value.TargetMetadataJson.Contains(dashed), cancellationToken) ||
            await context.CorpusRebuildOperations.AnyAsync(value => value.ManifestJson.Contains(id) || value.ManifestJson.Contains(dashed), cancellationToken))
            throw new InvalidOperationException("segment-retirement-recovery-referenced");
        // Completed version-2 Publish artefacts retain historical identity proof, not a physical pin.
        if (await context.Artifacts.AnyAsync(value => (value.SearchText == id || value.SearchText == dashed || value.SearchText == generation.IndexPath) &&
            (value.Stage != (int)FluxKnowledge.Domain.Pipeline.PipelineStage.Publish ||
             !context.OutboxMessages.Any(delivery => delivery.CompletedArtifactId == value.Id && delivery.DispatchedAtUtc != null)), cancellationToken))
            throw new InvalidOperationException("segment-retirement-artefact-unconfirmed");
    }

    private static async Task<bool> SegmentHasPinsAsync(FluxKnowledgeDbContext context, IndexSegmentEntity segment, CancellationToken cancellationToken,
        Guid? sourceDeletionOperation = null) =>
        await context.IndexRecoveryPins.AnyAsync(value => value.IndexPath == segment.DirectoryPath, cancellationToken) ||
        await context.IndexGenerationSegments.AnyAsync(value => value.SegmentId == segment.Id, cancellationToken) ||
        await context.CurrentIndexMembers.AnyAsync(value => value.SegmentId == segment.Id, cancellationToken) ||
        await context.IndexCandidateMembers.AnyAsync(value => context.IndexPublicationCandidates.Any(candidate => candidate.Id == value.CandidateId && candidate.State < 2) &&
            context.IndexSegmentVectors.Any(member => member.SegmentId == segment.Id && member.VectorId == value.VectorId), cancellationToken) ||
        await context.NativeOperationIntents.AnyAsync(value => value.TargetMetadataJson.Contains(segment.Id) || value.TargetMetadataJson.Contains(segment.DirectoryPath), cancellationToken) ||
        await context.SourceDeletionCleanupItems.AnyAsync(value => value.State != 1 && value.RelativePath == segment.Id &&
            !(sourceDeletionOperation != null && value.StorageKind == 4 && (value.SourceDeletionOperationId == sourceDeletionOperation ||
              context.SourceDeletionOperations.Any(operation => operation.Id == value.SourceDeletionOperationId &&
                context.SourceRootConfigurations.Any(root => root.Id == operation.SourceRootId && root.State == (int)FluxKnowledge.Domain.Sources.SourceRootState.Deleting)))), cancellationToken) ||
        await context.IndexState.AnyAsync(value => value.CorpusRebuildOperationId != null, cancellationToken);

    private async Task<FluxKnowledgeDbContext> NewFileOperationContextAsync(CancellationToken cancellationToken)
    {
        await using var factoryContext = await contexts.CreateDbContextAsync(cancellationToken);
        var binding = new SqlConnectionStringBuilder(factoryContext.Database.GetConnectionString()) { ConnectRetryCount = 0, Enlist = false };
        return new(new DbContextOptionsBuilder<FluxKnowledgeDbContext>().UseSqlServer(binding.ConnectionString).Options);
    }

    private static async Task ProveOriginalOwnershipAsync(FluxKnowledgeDbContext context, SqlTransaction original, CancellationToken token)
    {
        if (original.Connection is not { } connection || connection != context.Database.GetDbConnection() ||
            context.Database.CurrentTransaction?.GetDbTransaction() != original)
            throw new InvalidOperationException("segment-retirement-ownership-lost");
        await using var proof = new SqlCommand("""
            SELECT CASE WHEN XACT_STATE()=1 AND
                APPLOCK_MODE(N'public', @references, N'Transaction')=N'Shared' AND
                APPLOCK_MODE(N'public', @recovery, N'Transaction')=N'Exclusive' THEN 1 ELSE 0 END;
            """, connection, original) { CommandTimeout = 5 };
        proof.Parameters.AddWithValue("@references", SqlIndexRecoveryReferences.LockResource);
        proof.Parameters.AddWithValue("@recovery", SqlDerivedIndexRecoveryStore.LockResource);
        if (Convert.ToInt32(await proof.ExecuteScalarAsync(token)) != 1)
            throw new InvalidOperationException("segment-retirement-ownership-lost");
    }

    private void RequireSegmentPath(IndexSegmentEntity value)
    {
        if (!string.Equals(value.DirectoryPath, Path.Combine(Path.GetFullPath(indexRoot), "segments", value.Id), StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("segment-retirement-path-outside-layout");
    }
    private void RequireManifestPath(IndexGenerationEntity value)
    {
        if (!string.Equals(value.IndexPath, Path.Combine(Path.GetFullPath(indexRoot), "manifests", value.Id.ToString("N")), StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("segment-retirement-path-outside-layout");
    }

}
