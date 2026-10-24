using FluxKnowledge.Application.Documents;
using FluxKnowledge.Application.Gpu;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence;

public sealed partial class SqlGpuSchedulerStore
{
    private static async Task<bool> HasOcrReleaseProofAsync(FluxKnowledgeDbContext context,
        GpuExecutorDispatchEntity dispatch, CancellationToken ct)
    {
        if (dispatch.ExecutorKey != PaddleOcrVlmRuntimeContract.ExecutorKey) return true;
        var requests = await (from request in context.DocumentOcrRequests
            join task in context.GpuMiniTasks on request.MiniTaskId equals task.Id
            where task.BatchId == dispatch.BatchId && task.AdmissionGeneration == dispatch.AdmissionGeneration
            select request).ToArrayAsync(ct).ConfigureAwait(false);
        return requests.Length == 1 && SqlDocumentOcrStore.HasCleanupProof(requests[0],
            new GpuExecutorBatchHandle(dispatch.BatchId, dispatch.CapacitySlotKey, dispatch.ExecutorKey,
                dispatch.AdmissionGeneration, dispatch.DispatchId), requests[0].MiniTaskId);
    }
}
