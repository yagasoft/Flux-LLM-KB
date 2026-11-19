using System.Data;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Domain.Sources;
using Microsoft.EntityFrameworkCore;

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence;

public sealed partial class SqlSourceDeletionStore
{
    internal async ValueTask RecordCompletedManifestCleanupAsync(SourceDeletionWorkItem work,IReadOnlyList<Guid> ids,CancellationToken token)
    {
        if (ids.Count is < 1 or > 16 || ids.Distinct().Count() != ids.Count) throw new ArgumentException("Invalid manifest receipt page.",nameof(ids));
        try
        {
            await WithSqlFailureAsync(async () =>
            {
                await using var execution = await contextFactory.CreateDbContextAsync(token);
                await execution.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
                {
                    await using var context = await contextFactory.CreateDbContextAsync(token);
                    await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.Serializable,token);
                    var operation = await ReadOperationForUpdateAsync(context,work.OperationId,token);
                    if (operation is null || operation.LastCommittedLeaseId == work.LeaseId ||
                        !OwnsActiveLease(operation,work,timeProvider.GetUtcNow()) || operation.Phase != "cleanup-memberships") return;
                    if (!await context.SourceRootConfigurations.AnyAsync(root => root.Id == operation.SourceRootId && root.State == (int)SourceRootState.Deleting,token)) return;
                    var items = await context.SourceDeletionCleanupItems.Where(item => ids.Contains(item.Id) && item.SourceDeletionOperationId == operation.Id &&
                        item.StorageKind == 3 && item.State == 0).ToArrayAsync(token);
                    if (items.Length != ids.Count) throw new InvalidOperationException("source-delete-manifest-receipt-target-changed");
                    foreach (var item in items) { item.State=1;item.ReasonCode=null;item.UpdatedAtUtc=timeProvider.GetUtcNow(); }
                    await CommitUnitAsync(context,transaction,operation,operation.Phase,token,progressed:true,nextState:0,continueImmediately:true);
                });
                return true;
            });
        }
        catch (SourceDeletionLeaseLostException) { }
    }
}
