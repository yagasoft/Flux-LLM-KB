using FluxKnowledge.Application.Pipeline;
using FluxKnowledge.Application.Workers;
using FluxKnowledge.Domain.Sources;

namespace FluxKnowledge.Application.Sources;

/// <summary>Routes only supported retained text work into the established in-process pipeline.</summary>
public interface IRetainedTextRegistrationStore
{
    ValueTask<bool> RegisterAsync(SourceActivity activity, CancellationToken cancellationToken);
}

public sealed class RetainedTextActivityPlanner(
    IRetainedTextRegistrationStore registrationStore,
    IOutboxWakeSignal? wakeSignal = null)
{
    public async ValueTask<bool> PlanAsync(SourceActivity activity, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(activity);
        if (!IsSupportedInProcessTextActivity(activity))
        {
            return false;
        }

        var registered = await registrationStore.RegisterAsync(activity, cancellationToken).ConfigureAwait(false);
        if (registered)
        {
            wakeSignal?.Notify();
        }
        return registered;
    }

    private static bool IsSupportedInProcessTextActivity(SourceActivity activity) =>
        activity.ExecutionClass == ExecutionClass.InProcess &&
        activity.State == SourceActivityState.Pending &&
        activity.Kind is SourceActivityKind.TextExtraction or SourceActivityKind.MetadataExtraction;
}
