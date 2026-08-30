namespace FluxKnowledge.Application.Indexing;

public enum DerivedIndexRecoveryState
{
    Starting,
    Healthy,
    Recovering,
    RetryScheduled,
    OperatorActionRequired
}

public enum DerivedIndexRecoveryFailureCategory
{
    None,
    MissingDerivedIndex,
    InvalidDerivedIndex,
    TransientIo,
    SqlMembershipInvalid,
    SqlSchemaInvalid,
    ConfigurationInvalid,
    PermissionsDenied,
    RetryExhausted
}

public sealed record DerivedIndexRecoverySnapshot(
    DerivedIndexRecoveryState State,
    Guid? ActiveGenerationId,
    DateTimeOffset? LastCompletedAtUtc,
    DateTimeOffset? NextRetryAtUtc,
    DerivedIndexRecoveryFailureCategory? FailureCategory,
    int CleanedCandidateCount,
    bool IsValidatedEmptyCatalogue = false);

public sealed record DerivedIndexRecoveryFault(
    DerivedIndexRecoveryFailureCategory Category,
    Guid? ActiveGenerationId);

public sealed record DerivedIndexRecoveryDecision(
    bool ShouldRetry,
    TimeSpan? Delay,
    DerivedIndexRecoveryState NextState,
    DerivedIndexRecoveryFailureCategory FailureCategory);

public sealed record DerivedIndexRecoveryAuditEvent(
    string EventType,
    Guid? ActiveGenerationId,
    DerivedIndexRecoveryFailureCategory? FailureCategory,
    int AttemptCount,
    TimeSpan Elapsed,
    DateTimeOffset? NextRetryAtUtc,
    int CleanedCandidateCount);

public interface IDerivedIndexRecoveryStatus
{
    DerivedIndexRecoverySnapshot Snapshot { get; }
}

public interface IDerivedIndexRecoverySignal
{
    void Notify(DerivedIndexRecoveryFault fault);

    ValueTask<DerivedIndexRecoveryFault> WaitAsync(CancellationToken cancellationToken);
}

public static class DerivedIndexRecoveryPolicy
{
    public static DerivedIndexRecoveryDecision Decide(
        DerivedIndexRecoveryFailureCategory category,
        int failedAttemptCount)
    {
        if (category is DerivedIndexRecoveryFailureCategory.SqlMembershipInvalid or
            DerivedIndexRecoveryFailureCategory.SqlSchemaInvalid or
            DerivedIndexRecoveryFailureCategory.ConfigurationInvalid or
            DerivedIndexRecoveryFailureCategory.PermissionsDenied)
        {
            return new(false, null, DerivedIndexRecoveryState.OperatorActionRequired, category);
        }

        TimeSpan? delay = failedAttemptCount switch
        {
            1 => TimeSpan.FromSeconds(2),
            2 => TimeSpan.FromSeconds(5),
            3 => TimeSpan.FromSeconds(15),
            4 => TimeSpan.FromSeconds(30),
            _ => null
        };

        return delay is { } retryDelay
            ? new(true, retryDelay, DerivedIndexRecoveryState.RetryScheduled, category)
            : new(false, null, DerivedIndexRecoveryState.OperatorActionRequired,
                DerivedIndexRecoveryFailureCategory.RetryExhausted);
    }
}
