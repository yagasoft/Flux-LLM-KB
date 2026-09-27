using System.Linq.Expressions;
using FluxKnowledge.Application.Gpu;
using FluxKnowledge.Domain.Gpu;
using FluxKnowledge.Domain.Jobs;
using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence;

public sealed partial class SqlGpuSchedulerStore
{
    private static IQueryable<GpuMiniTaskEntity> WithWorkload(IQueryable<GpuMiniTaskEntity> tasks,
        GpuWorkloadPolicy policy, GpuWorkloadKind kind)
    {
        var parameter = Expression.Parameter(typeof(GpuMiniTaskEntity), "task");
        Expression matches = Expression.Constant(false);
        foreach (var profile in policy.Profiles.Where(p => p.Kind == kind))
            matches = Expression.OrElse(matches, Expression.AndAlso(
                Expression.Equal(Expression.Property(parameter, nameof(GpuMiniTaskEntity.ModelRuntimeKey)), Expression.Constant(profile.ModelRuntimeKey)),
                Expression.Equal(Expression.Property(parameter, nameof(GpuMiniTaskEntity.SettingsFingerprint)), Expression.Constant(profile.SettingsFingerprint))));
        return tasks.Where(Expression.Lambda<Func<GpuMiniTaskEntity, bool>>(matches, parameter));
    }

    private static async Task RecordWorkloadTurnAsync(FluxKnowledgeDbContext context, GpuMiniTaskEntity admitted,
        GpuSchedulerOptions options, CancellationToken cancellationToken)
    {
        if (options.WorkloadPolicy is null) return;
        var state = await context.GpuSchedulerStates.SingleAsync(s => s.Id == 1, cancellationToken).ConfigureAwait(false);
        if (options.WorkloadPolicy.Classify(admitted.ModelRuntimeKey, admitted.SettingsFingerprint) == GpuWorkloadKind.Ocr)
        {
            state.SearchBatchesWhileOcrWaiting = 0;
            return;
        }
        var waiters = context.GpuMiniTasks.Where(t => t.ParentJobId != null &&
            t.ExecutionState == (int)GpuMiniTaskExecutionState.Ready && t.ParentJob!.PublicState == (int)PublicJobState.GpuQueued &&
            !t.ParentJob.PipelineRecord.IsDeleted && (t.ParentJob.PipelineRecord.SourceRevisionId == null ||
                t.ParentJob.PipelineRecord.SourceRevision!.SourceRoot.State == (int)SourceRootState.Enabled));
        if (!await WithWorkload(waiters, options.WorkloadPolicy, GpuWorkloadKind.Ocr).AnyAsync(cancellationToken).ConfigureAwait(false))
            state.SearchBatchesWhileOcrWaiting = 0;
        else if (options.WorkloadPolicy.Classify(admitted.ModelRuntimeKey, admitted.SettingsFingerprint) == GpuWorkloadKind.Retrieval)
            state.SearchBatchesWhileOcrWaiting++;
    }
}
