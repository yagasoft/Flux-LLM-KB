namespace FluxKnowledge.Application.Ports;

/// <summary>
/// Local-only seam for replaying already-durable deferred activities. Implementations must preserve
/// the supplied activity idempotency keys and never activate an executor directly.
/// </summary>
public interface IDeferredContentReprocessor
{
    ValueTask<DeferredContentReplayResult> ReprocessAsync(
        IReadOnlyList<DeferredContentReplayRequest> activities,
        CancellationToken cancellationToken);
}

public sealed record DeferredContentReplayRequest(
    Guid SourceActivityId,
    string ActivityIdempotencyKey,
    string RequiredCapability,
    Guid CapabilityId,
    string ProcessorVersion,
    string ProcessorFingerprint);

public sealed record DeferredContentReplayResult(
    bool Accepted,
    int ReplayedCount,
    string Message)
{
    public static DeferredContentReplayResult Unavailable { get; } = new(
        false,
        0,
        "Deferred replay is unavailable until a matching local capability is registered.");

    public static DeferredContentReplayResult LocalOperationUnavailable { get; } = new(
        false,
        0,
        "A matching local capability is registered, but its replay operation is not registered.");
}
