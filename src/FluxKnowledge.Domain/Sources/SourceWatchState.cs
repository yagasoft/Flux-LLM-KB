using FluxKnowledge.Domain.Common;

namespace FluxKnowledge.Domain.Sources;

/// <summary>
/// Bounded source-relative hints. Only guarded reconciliation can authorise source access.
/// </summary>
public sealed record SourceWatchSignal(
    SourceRootId RootId,
    SourceWatchSignalKind Kind,
    DateTimeOffset ObservedAtUtc,
    IReadOnlyList<SourceWatchPathHint>? Paths = null,
    bool PathsIncomplete = false,
    bool RepositoryControlChanged = false,
    DateTimeOffset? FirstObservedAtUtc = null);

public sealed record SourceWatchPathHint(string RelativePath, bool IsSubtree = false);

public sealed record SourceWatchState(
    SourceRootId SourceRootId,
    DateTimeOffset? FirstSignalAtUtc,
    DateTimeOffset? LastSignalAtUtc,
    int SignalCount,
    long DebounceGeneration,
    DateTimeOffset? DueAtUtc,
    bool OverflowDetected = false)
{
    public static SourceWatchState Empty(SourceRootId sourceRootId)
    {
        ArgumentNullException.ThrowIfNull(sourceRootId);
        return new SourceWatchState(sourceRootId, null, null, 0, 0, null);
    }

    public SourceWatchState Observe(SourceWatchSignal signal, TimeSpan quietPeriod, TimeSpan maximumDelay)
    {
        ArgumentNullException.ThrowIfNull(signal);
        ValidatePeriods(quietPeriod, maximumDelay);
        if (signal.RootId != SourceRootId)
        {
            throw new DomainInvariantException("Watch signal root must match the persisted watch state.");
        }

        var firstObservedAtUtc = signal.FirstObservedAtUtc is { } earlier
            ? Min(earlier, signal.ObservedAtUtc)
            : signal.ObservedAtUtc;
        var firstSignalAtUtc = FirstSignalAtUtc is { } first
            ? Min(first, firstObservedAtUtc)
            : firstObservedAtUtc;
        var lastSignalAtUtc = LastSignalAtUtc is { } last && last > signal.ObservedAtUtc
            ? last
            : signal.ObservedAtUtc;
        var nextDebouncedDueAtUtc = Min(lastSignalAtUtc + quietPeriod, firstSignalAtUtc + maximumDelay);
        var overflowDetected = OverflowDetected || signal.Kind == SourceWatchSignalKind.Overflow;
        var dueAtUtc = signal.Kind == SourceWatchSignalKind.Overflow
            ? signal.ObservedAtUtc
            : OverflowDetected && DueAtUtc is { } existingDueAtUtc
                ? existingDueAtUtc
                : nextDebouncedDueAtUtc;

        return this with
        {
            FirstSignalAtUtc = firstSignalAtUtc,
            LastSignalAtUtc = lastSignalAtUtc,
            SignalCount = SignalCount == int.MaxValue ? int.MaxValue : SignalCount + 1,
            DebounceGeneration = checked(DebounceGeneration + 1),
            DueAtUtc = dueAtUtc,
            OverflowDetected = overflowDetected
        };
    }

    private static void ValidatePeriods(TimeSpan quietPeriod, TimeSpan maximumDelay)
    {
        if (quietPeriod <= TimeSpan.Zero || maximumDelay <= TimeSpan.Zero || maximumDelay < quietPeriod)
        {
            throw new DomainInvariantException("Watch debounce periods must be positive and the maximum delay must not be shorter than the quiet period.");
        }
    }

    private static DateTimeOffset Min(DateTimeOffset left, DateTimeOffset right) => left <= right ? left : right;
}
