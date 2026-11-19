using System.Linq.Expressions;
using FluxKnowledge.Application.Gpu;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;

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
}
