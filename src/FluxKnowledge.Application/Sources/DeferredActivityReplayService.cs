using FluxKnowledge.Application.Ports;
using FluxKnowledge.Domain.Sources;

namespace FluxKnowledge.Application.Sources;

/// <summary>Re-offers SQL-authoritative deferred work only to an already registered local processor.</summary>
public sealed class DeferredActivityReplayService(
    ISourceCapabilityStore capabilityStore,
    IDeferredActivityReplayStore replayStore,
    ILocalSourceCapabilityHandlerRegistry handlers) : IDeferredContentReprocessor
{
    public async ValueTask<DeferredContentReplayResult> ReplayAsync(
        Guid capabilityId,
        Guid? rootId,
        CancellationToken cancellationToken)
    {
        var capability = await capabilityStore.FindAsync(capabilityId, cancellationToken).ConfigureAwait(false);
        if (capability is null || !capability.IsRunnable || capability.ExecutionClass != ExecutionClass.InProcess ||
            !handlers.TryResolve(capability.Id, out var handler) || !Matches(handler, capability))
        {
            return DeferredContentReplayResult.Unavailable;
        }

        var replayed = await replayStore.ReplayAsync(capability, rootId, cancellationToken).ConfigureAwait(false);
        return new DeferredContentReplayResult(true, replayed, replayed == 0
            ? "No matching deferred activities are eligible for replay."
            : "Deferred activities were offered to the local in-process pipeline.");
    }

    public async ValueTask<DeferredContentReplayResult> ReprocessAsync(
        IReadOnlyList<DeferredContentReplayRequest> activities,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(activities);
        var replayed = 0;
        foreach (var request in activities
                     .OrderBy(value => value.CapabilityId)
                     .ThenBy(value => value.SourceActivityId))
        {
            var capability = await capabilityStore.FindAsync(request.CapabilityId, cancellationToken).ConfigureAwait(false);
            if (!Matches(request, capability) || !handlers.TryResolve(request.CapabilityId, out var handler) ||
                !Matches(handler, capability!))
            {
                continue;
            }

            replayed += await replayStore.ReplayActivityAsync(request, capability!, cancellationToken).ConfigureAwait(false);
        }

        return replayed == 0
            ? DeferredContentReplayResult.Unavailable
            : new DeferredContentReplayResult(true, replayed, "Deferred activities were offered to the local in-process pipeline.");
    }

    private static bool Matches(DeferredContentReplayRequest request, RegisteredSourceCapability? capability) =>
        capability is not null && capability.IsRunnable && capability.ExecutionClass == ExecutionClass.InProcess &&
        request.CapabilityId == capability.Id &&
        string.Equals(request.RequiredCapability, capability.ProcessorKind, StringComparison.Ordinal) &&
        string.Equals(request.ProcessorVersion, capability.ProcessorVersion, StringComparison.Ordinal) &&
        string.Equals(request.ProcessorFingerprint, capability.ProcessorFingerprint, StringComparison.Ordinal);

    private static bool Matches(SourceCapabilityDescriptor handler, RegisteredSourceCapability capability) =>
        handler.ExecutionClass == ExecutionClass.InProcess && handler.AcceptedActivityKind == SourceActivityKind.TextExtraction &&
        string.Equals(handler.AcceptedClassification, "AcceptedUtf8Text", StringComparison.Ordinal) &&
        string.Equals(handler.OutputContract, "pipeline:extract-utf8", StringComparison.Ordinal) &&
        capability.AcceptedActivityKind == handler.AcceptedActivityKind &&
        string.Equals(capability.AcceptedClassification, handler.AcceptedClassification, StringComparison.Ordinal) &&
        string.Equals(capability.OutputContract, handler.OutputContract, StringComparison.Ordinal) &&
        handler.Id == capability.Id && string.Equals(handler.ProcessorKind, capability.ProcessorKind, StringComparison.Ordinal) &&
        string.Equals(handler.ProcessorVersion, capability.ProcessorVersion, StringComparison.Ordinal) &&
        string.Equals(handler.ProcessorFingerprint, capability.ProcessorFingerprint, StringComparison.Ordinal);
}

public interface IDeferredActivityReplayStore
{
    ValueTask<int> ReplayAsync(RegisteredSourceCapability capability, Guid? rootId, CancellationToken cancellationToken);

    ValueTask<int> ReplayActivityAsync(
        DeferredContentReplayRequest request,
        RegisteredSourceCapability capability,
        CancellationToken cancellationToken);
}

/// <summary>Startup-only recovery seam for durable local work that has no pipeline receipt yet.</summary>
public interface ISourceActivityRestartStore
{
    ValueTask<int> OfferUnlinkedInProcessActivitiesAsync(CancellationToken cancellationToken);
}
