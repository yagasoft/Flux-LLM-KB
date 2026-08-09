using FluxKnowledge.Domain.Common;

namespace FluxKnowledge.Domain.Sources;

public sealed record SourceActivityId(Guid Value)
{
    public static SourceActivityId New() => new(Guid.NewGuid());
}

public sealed record SourceActivity
{
    private const int MaximumReasonLength = 1024;

    public SourceActivityId Id { get; private init; }

    public SourceRevisionId SourceRevisionId { get; private init; }

    public SourceActivityKind Kind { get; private init; }

    public ExecutionClass ExecutionClass { get; private init; }

    public string ProcessorVersion { get; private init; }

    public string InputFingerprint { get; private init; }

    public string? RequiredCapability { get; private init; }

    public string IdempotencyKey { get; private init; }

    public SourceActivityState State { get; private init; }

    public string? Reason { get; private init; }

    public static SourceActivity Create(
        SourceRevisionId sourceRevisionId,
        SourceActivityKind kind,
        ExecutionClass executionClass,
        string processorVersion,
        string inputFingerprint,
        string? requiredCapability,
        string? reason,
        SourceActivityState? initialState = null)
    {
        ArgumentNullException.ThrowIfNull(sourceRevisionId);
        EnsureDefined(kind, nameof(kind));
        EnsureDefined(executionClass, nameof(executionClass));
        var effectiveInitialState = initialState ?? (executionClass == ExecutionClass.InProcess
            ? SourceActivityState.Pending
            : SourceActivityState.DeferredUnsupported);
        EnsureDefined(effectiveInitialState, nameof(initialState));
        EnsureOpaqueValue(processorVersion, nameof(processorVersion));
        EnsureOpaqueValue(inputFingerprint, nameof(inputFingerprint));
        EnsureOptionalOpaqueValue(requiredCapability, nameof(requiredCapability));
        EnsureOptionalReason(reason);

        if (executionClass is ExecutionClass.DeferredCapability or ExecutionClass.NativeExecutorLater &&
            effectiveInitialState is SourceActivityState.Pending or SourceActivityState.Running)
        {
            throw new DomainInvariantException("Deferred and native executor activities cannot be runnable.");
        }

        return new SourceActivity(
            SourceActivityId.New(),
            sourceRevisionId,
            kind,
            executionClass,
            processorVersion,
            inputFingerprint,
            requiredCapability,
            CanonicalIdempotencyKey(sourceRevisionId, kind, processorVersion, inputFingerprint),
            effectiveInitialState,
            reason);
    }

    /// <summary>Reconstitutes an activity read from the authoritative persistence store.</summary>
    public static SourceActivity Restore(
        SourceActivityId id,
        SourceRevisionId sourceRevisionId,
        SourceActivityKind kind,
        ExecutionClass executionClass,
        string processorVersion,
        string inputFingerprint,
        string? requiredCapability,
        SourceActivityState state,
        string? reason,
        bool hasDurablePipelineReceipt = false)
    {
        ArgumentNullException.ThrowIfNull(id);
        if (executionClass == ExecutionClass.DeferredCapability && state == SourceActivityState.Running &&
            !hasDurablePipelineReceipt)
        {
            throw new DomainInvariantException("A deferred activity can be restored as running only with a durable pipeline receipt.");
        }

        var effectiveState = executionClass == ExecutionClass.DeferredCapability && state == SourceActivityState.Running
            ? SourceActivityState.DeferredUnsupported
            : state;
        var validated = Create(
            sourceRevisionId,
            kind,
            executionClass,
            processorVersion,
            inputFingerprint,
            requiredCapability,
            reason,
            effectiveState);
        return validated with { Id = id, State = state };
    }

    public SourceActivity DeferUnsupported(string reason) => Defer(SourceActivityState.DeferredUnsupported, reason);

    public SourceActivity DeferPolicy(string reason) => Defer(SourceActivityState.DeferredPolicy, reason);

    public SourceActivity ReconsiderAfterElapsedTime() => State == SourceActivityState.FailedRetryable
        ? this with { State = SourceActivityState.Pending }
        : this;

    private SourceActivity Defer(SourceActivityState state, string reason)
    {
        EnsureReason(reason);
        return this with { State = state, Reason = reason };
    }

    private static string CanonicalIdempotencyKey(
        SourceRevisionId sourceRevisionId,
        SourceActivityKind kind,
        string processorVersion,
        string inputFingerprint) =>
        FormattableString.Invariant(
            $"{sourceRevisionId.Value:N}|{(int)kind}|{processorVersion.Length}:{processorVersion}|{inputFingerprint.Length}:{inputFingerprint}");

    private static void EnsureDefined<TEnum>(TEnum value, string parameterName)
        where TEnum : struct, Enum
    {
        if (!Enum.IsDefined(value))
        {
            throw new DomainInvariantException($"{parameterName} is invalid.");
        }
    }

    private static void EnsureOpaqueValue(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (char.IsWhiteSpace(value[^1]))
        {
            throw new ArgumentException("Opaque values cannot end with whitespace.", parameterName);
        }
    }

    private static void EnsureOptionalOpaqueValue(string? value, string parameterName)
    {
        if (value is not null)
        {
            EnsureOpaqueValue(value, parameterName);
        }
    }

    private static void EnsureOptionalReason(string? reason)
    {
        if (reason is not null)
        {
            EnsureReason(reason);
        }
    }

    private static void EnsureReason(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        if (reason.Length > MaximumReasonLength)
        {
            throw new DomainInvariantException("Source activity reason must be at most 1024 characters.");
        }
    }

    private SourceActivity(
        SourceActivityId id,
        SourceRevisionId sourceRevisionId,
        SourceActivityKind kind,
        ExecutionClass executionClass,
        string processorVersion,
        string inputFingerprint,
        string? requiredCapability,
        string idempotencyKey,
        SourceActivityState state,
        string? reason)
    {
        Id = id;
        SourceRevisionId = sourceRevisionId;
        Kind = kind;
        ExecutionClass = executionClass;
        ProcessorVersion = processorVersion;
        InputFingerprint = inputFingerprint;
        RequiredCapability = requiredCapability;
        IdempotencyKey = idempotencyKey;
        State = state;
        Reason = reason;
    }
}
