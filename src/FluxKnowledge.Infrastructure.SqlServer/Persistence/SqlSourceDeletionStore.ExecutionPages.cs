using FluxKnowledge.Application.Sources;
using Microsoft.EntityFrameworkCore;

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence;

public sealed partial class SqlSourceDeletionStore
{
    private const int ExecutionTaskPageSize=1000;

    private static async Task<bool> DeleteOwnedExecutionPageAsync(FluxKnowledgeDbContext context,
        LocalExecutionGraph graph,CancellationToken ct)
    {
        if(graph.MiniTaskIds.Length==0)return false;
        // The proof is local to this serializable transaction. Parent/source
        // anchors remain until every page has committed, including orphan requests.
        var tasks=graph.MiniTaskIds.Order().Take(ExecutionTaskPageSize).ToArray();
        var ownedBatches=graph.BatchIds.ToHashSet();
        var resultBatches=await context.GpuExecutorResultReceipts.Where(result=>tasks.Contains(result.MiniTaskId))
            .Select(result=>result.BatchId).Distinct().ToArrayAsync(ct).ConfigureAwait(false);
        if(resultBatches.Any(batch=>!ownedBatches.Contains(batch)))
            throw new SourceDeletionFailureException("source-delete-local-ocr-foreign-result",
                new InvalidOperationException("An owned task has a result receipt bound to a foreign batch."));
        var touched=await context.GpuMiniTasks.Where(task=>tasks.Contains(task.Id)&&task.BatchId.HasValue)
            .Select(task=>task.BatchId!.Value).Distinct().ToArrayAsync(ct).ConfigureAwait(false);
        await context.GpuExecutorResultReceipts.Where(result=>tasks.Contains(result.MiniTaskId))
            .ExecuteDeleteAsync(ct).ConfigureAwait(false);
        await context.EmbeddingGpuRequests.Where(request=>tasks.Contains(request.MiniTaskId)).ExecuteDeleteAsync(ct).ConfigureAwait(false);
        await context.DocumentOcrRequests.Where(request=>tasks.Contains(request.MiniTaskId)).ExecuteDeleteAsync(ct).ConfigureAwait(false);
        await context.MediaTranscriptionGpuRequests.Where(request=>tasks.Contains(request.MiniTaskId)).ExecuteDeleteAsync(ct).ConfigureAwait(false);
        await context.GpuMiniTasks.Where(task=>tasks.Contains(task.Id)).ExecuteDeleteAsync(ct).ConfigureAwait(false);
        // A batch can span pages. Preserve its identity/count and batch-wide
        // receipts until the final task leaves; never retire an unselected task.
        var empty=await context.GpuBatches.Where(batch=>touched.Contains(batch.Id)&&
            !context.GpuMiniTasks.Any(task=>task.BatchId==batch.Id)).Select(batch=>batch.Id).ToArrayAsync(ct).ConfigureAwait(false);
        await DeleteEmptyExecutionBatchesAsync(context,empty,ct).ConfigureAwait(false);
        return true;
    }

    // The caller proves empty batch ownership inside its original transaction.
    internal static async Task DeleteEmptyExecutionBatchesAsync(FluxKnowledgeDbContext context,Guid[] empty,CancellationToken ct)
    {
        if(empty.Length>0)
        {
            await context.GpuExecutorEvidence.Where(value=>empty.Contains(value.BatchId)).ExecuteDeleteAsync(ct).ConfigureAwait(false);
            await context.GpuExecutorResultReceipts.Where(value=>empty.Contains(value.BatchId)).ExecuteDeleteAsync(ct).ConfigureAwait(false);
            await context.GpuExecutorDispatches.Where(value=>empty.Contains(value.BatchId)).ExecuteDeleteAsync(ct).ConfigureAwait(false);
            await context.GpuSchedulerOperationReceipts.Where(value=>value.BatchId.HasValue&&empty.Contains(value.BatchId.Value))
                .ExecuteDeleteAsync(ct).ConfigureAwait(false);
            await context.GpuBatches.Where(value=>empty.Contains(value.Id)).ExecuteDeleteAsync(ct).ConfigureAwait(false);
        }
    }
}
