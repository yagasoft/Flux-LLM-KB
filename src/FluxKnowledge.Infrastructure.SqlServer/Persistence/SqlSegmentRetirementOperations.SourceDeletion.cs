using System.Data;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Domain.Sources;
using Microsoft.EntityFrameworkCore;

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence;

public sealed partial class SqlSegmentRetirementOperations
{
    public async ValueTask<string?> AdvanceAsync(SourceDeletionWorkItem work, CancellationToken cancellationToken)
    {
        await using var context = await contexts.CreateDbContextAsync(cancellationToken);
        if (await context.IndexState.Where(value => value.Id == 1).Select(value => value.RequiredIndexFormat).SingleAsync(cancellationToken) != 2) return null;
        var target = await context.SourceDeletionCleanupItems.AsNoTracking().Where(value => value.SourceDeletionOperationId == work.OperationId &&
            (value.StorageKind == 3 || value.StorageKind == 4) && value.State == 0)
            .OrderBy(value => value.StorageKind).ThenBy(value => value.RelativePath).FirstOrDefaultAsync(cancellationToken);
        if (target is null) return null;
        try
        {
            await ValidateSourceDeletionAsync(context, work, target.StorageKind, target.RelativePath, cancellationToken);
            if (target.StorageKind == 3)
            {
                if (!Guid.TryParseExact(target.RelativePath, "N", out var id)) throw new InvalidOperationException("segment-retirement-source-target-invalid");
                var row = await context.IndexGenerations.AsNoTracking().SingleAsync(value => value.Id == id, cancellationToken);
                var operation = row.RetirementOperationId ?? Guid.NewGuid();
                await MarkManifestAsync(id, operation, cancellationToken, work);
                // Segment cleanup has its own durable exact targets and is processed after all affected manifests detach.
                if (await context.IndexSegments.AnyAsync(value => value.RetirementOperationId == operation && value.State != 3, cancellationToken))
                    return await AdvanceSegmentBeforeManifestAsync(work, operation, cancellationToken);
                if (!await TryReclaimManifestAsync(id, operation, cancellationToken, work)) return "source-delete-segment-owner-active";
            }
            else
            {
                var segment = await context.IndexSegments.AsNoTracking().SingleAsync(value => value.Id == target.RelativePath, cancellationToken);
                if (segment.State != 3)
                {
                    var operation = segment.RetirementOperationId ?? Guid.NewGuid();
                    await MarkUnreferencedSegmentAsync(segment.Id, operation, cancellationToken, work);
                    if (!await TryReclaimSegmentAsync(segment.Id, operation, cancellationToken, work)) return "source-delete-segment-owner-active";
                }
            }
            await new SqlSourceDeletionStore(contexts, clock, queryOwnerProbe: probe).RecordCleanupResultAsync(work, target.Id, new(true), cancellationToken);
            return null;
        }
        catch (InvalidOperationException exception) when (exception.Message.StartsWith("segment-retirement-", StringComparison.Ordinal))
        { return "source-delete-" + exception.Message; }
    }

    private async ValueTask<string?> AdvanceSegmentBeforeManifestAsync(SourceDeletionWorkItem work, Guid operation, CancellationToken cancellationToken)
    {
        await using var context = await contexts.CreateDbContextAsync(cancellationToken);
        var id = await context.IndexSegments.AsNoTracking().Where(value => value.RetirementOperationId == operation && value.State != 3 &&
            context.SourceDeletionCleanupItems.Any(target => target.SourceDeletionOperationId == work.OperationId && target.StorageKind == 4 && target.RelativePath == value.Id))
            .OrderBy(value => value.Id).Select(value => value.Id).FirstOrDefaultAsync(cancellationToken);
        if (id is null) return "source-delete-segment-peer-retirement-pending";
        return await TryReclaimSegmentAsync(id, operation, cancellationToken, work)
            ? "source-delete-segment-retirement-progressing" : "source-delete-segment-owner-active";
    }

    public async ValueTask MarkUnreferencedSegmentAsync(string segmentId, Guid operationId, CancellationToken cancellationToken,
        SourceDeletionWorkItem? sourceDeletion = null)
    {
        if (operationId == Guid.Empty) throw new ArgumentException("segment-retirement-operation-invalid");
        await using var strategy = await contexts.CreateDbContextAsync(cancellationToken);
        await strategy.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var context = await contexts.CreateDbContextAsync(cancellationToken);
            await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
            if (!await EnterAsync(context, cancellationToken)) throw new InvalidOperationException("segment-retirement-query-owned");
            await ValidateSourceDeletionAsync(context, sourceDeletion, 4, segmentId, cancellationToken);
            var segment = await context.IndexSegments.SingleAsync(value => value.Id == segmentId, cancellationToken);
            if (segment.State is 2 or 3 && segment.RetirementOperationId == operationId) return;
            if (segment.State != 1 || segment.ProducingCandidateId is not { } producer ||
                !await context.IndexPublicationCandidates.AnyAsync(value => value.Id == producer && (value.State == 2 || value.State == 3), cancellationToken))
                throw new InvalidOperationException("segment-retirement-producer-unconfirmed");
            if (await SegmentHasPinsAsync(context, segment, cancellationToken, sourceDeletion?.OperationId))
                throw new InvalidOperationException("segment-retirement-segment-referenced");
            segment.State = 2; segment.RetirementOperationId = operationId;
            await context.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        });
    }

    private async Task ValidateSourceDeletionAsync(FluxKnowledgeDbContext context, SourceDeletionWorkItem? work,
        int kind, string path, CancellationToken cancellationToken)
    {
        if (work is null) return;
        if (!await context.SourceDeletionOperations.AnyAsync(value => value.Id == work.OperationId && value.SourceRootId == work.SourceRootId &&
            value.State == 1 && value.LeaseId == work.LeaseId && value.LeaseExpiresAtUtc > clock.GetUtcNow() && value.Phase == "cleanup-memberships" &&
            context.SourceRootConfigurations.Any(root => root.Id == value.SourceRootId && root.State == (int)SourceRootState.Deleting) &&
            context.SourceDeletionCleanupItems.Any(item => item.SourceDeletionOperationId == value.Id && item.StorageKind == kind && item.RelativePath == path), cancellationToken))
            throw new InvalidOperationException("segment-retirement-source-authority-lost");
    }
}
