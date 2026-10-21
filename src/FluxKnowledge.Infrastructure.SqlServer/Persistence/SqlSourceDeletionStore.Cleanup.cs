using System.Data;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Domain.Sources;
using Microsoft.EntityFrameworkCore;

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence;

public sealed partial class SqlSourceDeletionStore
{
    public async ValueTask<IReadOnlyList<SourceDeletionFileTarget>> ReadPendingCleanupAsync(
        SourceDeletionWorkItem workItem, CancellationToken cancellationToken) =>
        await WithSqlFailureAsync(async () =>
        {
            await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            var owns = await context.SourceDeletionOperations.AsNoTracking().AnyAsync(value =>
                value.Id == workItem.OperationId && value.SourceRootId == workItem.SourceRootId &&
                value.State == 1 && value.LeaseId == workItem.LeaseId && value.LeaseExpiresAtUtc > timeProvider.GetUtcNow() &&
                value.Phase == "cleanup-files" && context.SourceRootConfigurations.Any(root =>
                    root.Id == value.SourceRootId && root.State == (int)SourceRootState.Deleting), cancellationToken).ConfigureAwait(false);
            if (!owns) return Array.Empty<SourceDeletionFileTarget>();
            var item = await context.SourceDeletionCleanupItems.AsNoTracking()
                .Where(value => value.SourceDeletionOperationId == workItem.OperationId && value.State == 0)
                .OrderBy(value => value.StorageKind).ThenBy(value => value.RelativePath).ThenBy(value => value.Id)
                .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            if (item is null) return Array.Empty<SourceDeletionFileTarget>();
            var shared = item.StorageKind == 1 && await context.SourceArtifacts.AnyAsync(artifact =>
                artifact.ContentSha256 == item.ContentSha256 && artifact.StoreRelativePath == item.RelativePath &&
                artifact.ByteLength == item.ByteLength, cancellationToken).ConfigureAwait(false);
            return new[] { new SourceDeletionFileTarget(item.Id, item.StorageKind, item.RelativePath, item.ContentSha256, item.ByteLength)
                { SharedReferenceExists = shared } };
        }).ConfigureAwait(false);

    public async ValueTask RecordCleanupResultAsync(SourceDeletionWorkItem workItem, Guid cleanupItemId,
        SourceDeletionFileResult result, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(result);
        try
        {
            await WithSqlFailureAsync(async () =>
            {
                await using var execution = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
                await execution.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
                {
                    await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
                    await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false);
                    var operation = await ReadOperationForUpdateAsync(context, workItem.OperationId, cancellationToken).ConfigureAwait(false);
                    if (operation is null || operation.LastCommittedLeaseId == workItem.LeaseId ||
                        !OwnsActiveLease(operation, workItem, timeProvider.GetUtcNow()) || operation.Phase is not ("cleanup-files" or "cleanup-memberships")) return;
                    if (!await context.SourceRootConfigurations.AnyAsync(root => root.Id == operation.SourceRootId &&
                        root.State == (int)SourceRootState.Deleting, cancellationToken).ConfigureAwait(false)) return;
                    var item = await context.SourceDeletionCleanupItems.SingleOrDefaultAsync(value =>
                        value.Id == cleanupItemId && value.SourceDeletionOperationId == operation.Id && value.State == 0,
                        cancellationToken).ConfigureAwait(false);
                    if (item is null) return;
                    if (operation.Phase == "cleanup-memberships" && item.StorageKind is not (3 or 4)) return;
                    if (operation.Phase == "cleanup-files" && item.StorageKind is not (1 or 2)) return;
                    item.State = result.Completed ? 1 : 2;
                    item.ReasonCode = result.ReasonCode;
                    item.UpdatedAtUtc = timeProvider.GetUtcNow();
                    if (result.Completed && result.ReasonCode == "source-delete-shared-artifact-preserved")
                        operation.SharedArtifactCount++;
                    await CommitUnitAsync(context, transaction, operation, result.Completed ? operation.Phase : "attention",
                        cancellationToken, result.Completed ? null : result.ReasonCode ?? "source-delete-file-cleanup-failed",
                        progressed: result.Completed, nextState: result.Completed ? 0 : 4,
                        continueImmediately: result.Completed).ConfigureAwait(false);
                }).ConfigureAwait(false);
                return true;
            }).ConfigureAwait(false);
        }
        catch (SourceDeletionLeaseLostException) { }
    }

    public ValueTask FailAsync(SourceDeletionWorkItem workItem, string reasonCode, CancellationToken cancellationToken) =>
        RecordDispositionAsync(workItem, reasonCode, attention: true, cancellationToken);

    public ValueTask DeferAsync(SourceDeletionWorkItem workItem, string reasonCode, CancellationToken cancellationToken) =>
        RecordDispositionAsync(workItem, reasonCode, attention: false, cancellationToken);

    private async ValueTask RecordDispositionAsync(SourceDeletionWorkItem workItem, string reasonCode,
        bool attention, CancellationToken cancellationToken)
    {
        try
        {
            await WithSqlFailureAsync(async () =>
            {
                await using var execution = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
                await execution.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
                {
                    await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
                    await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false);
                    var operation = await ReadOperationForUpdateAsync(context, workItem.OperationId, cancellationToken).ConfigureAwait(false);
                    if (operation is null || !OwnsActiveLease(operation, workItem, timeProvider.GetUtcNow())) return;
                    await CommitUnitAsync(context, transaction, operation, attention ? "attention" : operation.Phase, cancellationToken,
                        reasonCode, progressed: false, nextState: attention ? 4 : 0, continueImmediately: false).ConfigureAwait(false);
                }).ConfigureAwait(false);
                return true;
            }).ConfigureAwait(false);
        }
        catch (SourceDeletionLeaseLostException) { }
    }
}
