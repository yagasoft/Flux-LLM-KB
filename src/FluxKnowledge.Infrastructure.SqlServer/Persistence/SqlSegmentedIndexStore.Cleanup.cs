using System.Data;
using Microsoft.EntityFrameworkCore;

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence;

public sealed partial class SqlSegmentedIndexStore
{
    private async Task CleanupTerminalRootWorklistAsync(Guid candidateId, CancellationToken token)
    {
        await using var strategyContext = await contexts.CreateDbContextAsync(token);
        int deleted;
        do
        {
            deleted = await strategyContext.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
            {
                await using var context = await contexts.CreateDbContextAsync(token);
                await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.Serializable, token);
                await AcquireSharedTransactionAsync(context, token);
                var candidate = await LockCandidateAsync(context, candidateId, token);
                if (candidate.State is not (2 or 3) || candidate.BuildPurpose != 0 || !IsRootRefresh(candidate)) return 0;
                // Never acquire the publication fence while holding the candidate.
                // Terminal staging has no native ownership or serving authority.
                var count = await context.Database.ExecuteSqlInterpolatedAsync(
                    $"DELETE TOP ({MetadataPageSize}) FROM [IndexCandidateMembers] WHERE [CandidateId] = {candidateId};", token);
                await transaction.CommitAsync(token);
                return count;
            });
        } while (deleted != 0);
    }
}
