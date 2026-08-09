using FluxKnowledge.Domain.Common;

namespace FluxKnowledge.Domain.Sources;

public sealed record SourceScanRequestId(Guid Value)
{
    public static SourceScanRequestId New() => new(Guid.NewGuid());
}

public sealed record SourceScanRequest
{
    public SourceScanRequestId Id { get; private init; }

    public SourceRootId SourceRootId { get; private init; }

    public string RequestedBy { get; private init; }

    public DateTimeOffset RequestedAtUtc { get; private init; }

    public SourceScanRequestState State { get; private init; }

    public bool IsReleased => State != SourceScanRequestState.Held;

    public DateTimeOffset? ReleasedAtUtc { get; private init; }

    public static SourceScanRequest CreateHeld(SourceRootId sourceRootId, string requestedBy)
    {
        ArgumentNullException.ThrowIfNull(sourceRootId);
        ArgumentException.ThrowIfNullOrWhiteSpace(requestedBy);
        return new SourceScanRequest(
            SourceScanRequestId.New(),
            sourceRootId,
            requestedBy,
            DateTimeOffset.UtcNow,
            SourceScanRequestState.Held,
            null);
    }

    public SourceScanRequest Release(DateTimeOffset releasedAtUtc)
    {
        if (State != SourceScanRequestState.Held)
        {
            throw new DomainInvariantException("Only held source scan requests can be released.");
        }

        return this with { State = SourceScanRequestState.Released, ReleasedAtUtc = releasedAtUtc };
    }

    public static SourceScanRequest Restore(
        SourceScanRequestId id,
        SourceRootId sourceRootId,
        string requestedBy,
        DateTimeOffset requestedAtUtc,
        SourceScanRequestState state,
        DateTimeOffset? releasedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(sourceRootId);
        ArgumentException.ThrowIfNullOrWhiteSpace(requestedBy);
        if (state == SourceScanRequestState.Held && releasedAtUtc is not null)
        {
            throw new DomainInvariantException("A held source scan request cannot have a release time.");
        }

        if (state != SourceScanRequestState.Held && releasedAtUtc is null)
        {
            throw new DomainInvariantException("A released source scan request requires a release time.");
        }

        return new SourceScanRequest(id, sourceRootId, requestedBy, requestedAtUtc, state, releasedAtUtc);
    }

    private SourceScanRequest(
        SourceScanRequestId id,
        SourceRootId sourceRootId,
        string requestedBy,
        DateTimeOffset requestedAtUtc,
        SourceScanRequestState state,
        DateTimeOffset? releasedAtUtc)
    {
        Id = id;
        SourceRootId = sourceRootId;
        RequestedBy = requestedBy;
        RequestedAtUtc = requestedAtUtc;
        State = state;
        ReleasedAtUtc = releasedAtUtc;
    }
}
