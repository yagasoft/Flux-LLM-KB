using System.Data;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Configurations;
using Microsoft.EntityFrameworkCore;

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence;

public sealed partial class SqlSourceDeletionStore
{
    public async ValueTask<IReadOnlyList<SourceDeletionFileTarget>> ReadPendingCleanupAsync(
        SourceDeletionWorkItem workItem, CancellationToken cancellationToken, int maxTargets = 1)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxTargets, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maxTargets, ISourceDeletionStore.FileCleanupBatchSize);
        if (workItem.Phase != "cleanup-files") return [];

        return await WithSqlFailureAsync(async () =>
        {
            await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            if (await SqlOutlookSourceReservations.ProtectsDeletionAsync(context, workItem.SourceRootId, cancellationToken).ConfigureAwait(false))
                return Array.Empty<SourceDeletionFileTarget>();
            var now = timeProvider.GetUtcNow();
            return await (from item in context.SourceDeletionCleanupItems.AsNoTracking()
                          join operation in context.SourceDeletionOperations on item.SourceDeletionOperationId equals operation.Id
                          where item.SourceDeletionOperationId == workItem.OperationId && item.State == 0 &&
                              operation.SourceRootId == workItem.SourceRootId && operation.State == 1 &&
                              operation.LeaseId == workItem.LeaseId && operation.LeaseExpiresAtUtc > now &&
                              operation.Phase == "cleanup-files" && context.SourceRootConfigurations.Any(root =>
                                  root.Id == operation.SourceRootId && root.State == (int)SourceRootState.Deleting)
                          orderby item.StorageKind, item.Id
                          select new SourceDeletionFileTarget(item.Id, item.StorageKind, item.RelativePath, item.ContentSha256, item.ByteLength)
                          {
                              SharedReferenceExists = item.StorageKind == 1 && context.SourceArtifacts.Any(artifact =>
                                  artifact.ContentSha256 == EF.Functions.Collate(item.ContentSha256!, "DATABASE_DEFAULT") &&
                                  artifact.StoreRelativePath == EF.Functions.Collate(item.RelativePath, SchemaConfiguration.SchedulerFenceCollation) &&
                                  artifact.ByteLength == item.ByteLength)
                          }).Take(maxTargets).ToArrayAsync(cancellationToken).ConfigureAwait(false);
        }).ConfigureAwait(false);
    }

    public async ValueTask RecordCleanupResultAsync(SourceDeletionWorkItem workItem, Guid cleanupItemId,
        SourceDeletionFileResult result, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(result);
        await RecordCleanupReceiptsAsync(workItem, [new(cleanupItemId, result)], requireFilePhase: false, cancellationToken)
            .ConfigureAwait(false);
    }

    public ValueTask<bool> RecordCleanupResultsAsync(SourceDeletionWorkItem workItem,
        IReadOnlyList<SourceDeletionFileOutcome> outcomes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(outcomes);
        ArgumentOutOfRangeException.ThrowIfLessThan(outcomes.Count, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(outcomes.Count, ISourceDeletionStore.FileCleanupBatchSize);
        if (workItem.Phase != "cleanup-files") return ValueTask.FromResult(false);
        var receipts = outcomes.Select(outcome => new CleanupReceipt(outcome.Target.CleanupItemId, outcome.Result, outcome.Target)).ToArray();
        if (receipts.Select(receipt => receipt.Id).Distinct().Count() != receipts.Length)
            throw new ArgumentException("Cleanup outcomes must identify distinct targets.", nameof(outcomes));
        return RecordCleanupReceiptsAsync(workItem, receipts, requireFilePhase: true, cancellationToken);
    }

    private async ValueTask<bool> RecordCleanupReceiptsAsync(SourceDeletionWorkItem workItem,
        CleanupReceipt[] receipts, bool requireFilePhase, CancellationToken cancellationToken)
    {
        var ids = receipts.Select(receipt => receipt.Id).ToArray();
        var attemptedCommit = false;
        try
        {
            return await WithSqlFailureAsync(async () =>
            {
                await using var execution = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
                return await execution.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
                {
                    await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
                    await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false);
                    var operation = await ReadOperationForUpdateAsync(context, workItem.OperationId, cancellationToken).ConfigureAwait(false);
                    if (operation is null || operation.SourceRootId != workItem.SourceRootId) return false;

                    // Only this invocation can recover its own committed outcome after
                    // a lost acknowledgement. A new receipt for a consumed lease is stale.
                    if (operation.LastCommittedLeaseId == workItem.LeaseId) return attemptedCommit;
                    if (!OwnsActiveLease(operation, workItem, timeProvider.GetUtcNow()) ||
                        operation.Phase is not ("cleanup-files" or "cleanup-memberships") ||
                        (requireFilePhase && operation.Phase != "cleanup-files")) return false;
                    if (!await context.SourceRootConfigurations.AnyAsync(root => root.Id == operation.SourceRootId &&
                        root.State == (int)SourceRootState.Deleting, cancellationToken).ConfigureAwait(false)) return false;

                    var files = operation.Phase == "cleanup-files";
                    var items = await context.SourceDeletionCleanupItems.Where(item =>
                        item.SourceDeletionOperationId == operation.Id && item.State == 0 && ids.Contains(item.Id) &&
                        (files ? item.StorageKind == 1 || item.StorageKind == 2 : item.StorageKind == 3 || item.StorageKind == 4))
                        .ToDictionaryAsync(item => item.Id, cancellationToken).ConfigureAwait(false);
                    if (items.Count != receipts.Length) return false;

                    string? failure = null;
                    var progressed = false;
                    var now = timeProvider.GetUtcNow();
                    foreach (var receipt in receipts)
                    {
                        var item = items[receipt.Id];
                        if (receipt.Target is { } target && !target.MatchesBinding(
                            new(item.Id, item.StorageKind, item.RelativePath, item.ContentSha256, item.ByteLength))) return false;

                        item.State = receipt.Result.Completed ? 1 : 2;
                        item.ReasonCode = receipt.Result.ReasonCode;
                        item.UpdatedAtUtc = now;
                        if (receipt.Result.Completed)
                        {
                            progressed = true;
                            if (receipt.Result.ReasonCode == "source-delete-shared-artifact-preserved")
                                operation.SharedArtifactCount++;
                        }
                        else
                        {
                            failure ??= receipt.Result.ReasonCode ?? "source-delete-file-cleanup-failed";
                        }
                    }

                    attemptedCommit = true;
                    await CommitUnitAsync(context, transaction, operation, failure is null ? operation.Phase : "attention",
                        cancellationToken, failure, progressed, nextState: failure is null ? 0 : 4,
                        continueImmediately: failure is null).ConfigureAwait(false);
                    return true;
                }).ConfigureAwait(false);
            }).ConfigureAwait(false);
        }
        catch (SourceDeletionLeaseLostException) { return false; }
    }

    private sealed record CleanupReceipt(Guid Id, SourceDeletionFileResult Result, SourceDeletionFileTarget? Target = null);

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
