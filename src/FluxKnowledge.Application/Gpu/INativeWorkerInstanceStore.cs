namespace FluxKnowledge.Application.Gpu;

/// <summary>
/// SQL-authoritative private persistence boundary for native-worker instance observations.
/// </summary>
public interface INativeWorkerInstanceStore
{
    ValueTask<IReadOnlyList<NativeWorkerRecoveryCandidate>> ReadRecoveryCandidatesAsync(
        string executorKey,
        CancellationToken cancellationToken);

    ValueTask<NativeWorkerStoreMutationResult> CreateAsync(
        Guid operationId,
        NativeWorkerLaunchRequest launch,
        CancellationToken cancellationToken);

    ValueTask<NativeWorkerStoreMutationResult> AppendEvidenceAsync(
        NativeWorkerLifecycleEvidence evidence,
        CancellationToken cancellationToken);

    ValueTask<NativeWorkerStoreMutationResult> RecordConnectionAsync(
        Guid operationId,
        NativeWorkerConnectionAttestation attestation,
        CancellationToken cancellationToken);

    ValueTask<NativeWorkerStoreMutationResult> BindExactActiveDispatchAsync(Guid operationId, NativeWorkerInstanceHandle instance, GpuExecutorBatchHandle handle, DateTimeOffset observedAtUtc, CancellationToken cancellationToken);
    ValueTask<NativeWorkerStoreMutationResult> ClearExactActiveDispatchAsync(Guid operationId, NativeWorkerInstanceHandle instance, GpuExecutorBatchHandle handle, DateTimeOffset observedAtUtc, CancellationToken cancellationToken);

    ValueTask<NativeWorkerStoreMutationResult> RecordHeartbeatAsync(
        Guid operationId,
        Guid instanceId,
        DateTimeOffset observedAtUtc,
        CancellationToken cancellationToken);

    ValueTask<NativeWorkerStoreMutationResult> RecordExitAsync(
        Guid operationId,
        Guid instanceId,
        DateTimeOffset observedAtUtc,
        int? exitCode,
        CancellationToken cancellationToken);

    ValueTask<NativeWorkerStoreMutationResult> MarkExactHandleUncertainAsync(
        Guid operationId,
        NativeWorkerInstanceHandle instance,
        GpuExecutorBatchHandle handle,
        DateTimeOffset observedAtUtc,
        CancellationToken cancellationToken);
}

/// <summary>
/// Private recovery view. It intentionally contains only worker/dispatch fences needed to fail
/// closed at host start; it never carries executable paths, pipe data or diagnostics.
/// </summary>
public sealed record NativeWorkerRecoveryCandidate(
    Guid InstanceId,
    NativeWorkerLifecycleClass State,
    NativeWorkerInstanceHandle? AttestedInstance,
    GpuExecutorBatchHandle? ActiveHandle)
{
    public void Validate(string executorKey)
    {
        if (InstanceId == Guid.Empty)
        {
            throw new ArgumentException("A recovery candidate instance ID is required.", nameof(InstanceId));
        }

        GpuSchedulerOpaqueKeyValidator.RequireCanonical(
            executorKey,
            nameof(executorKey),
            GpuSchedulerOpaqueKeyValidator.MaximumExecutorFenceKeyLength);
        if (!Enum.IsDefined(State))
        {
            throw new ArgumentOutOfRangeException(nameof(State));
        }

        if (AttestedInstance is not null)
        {
            AttestedInstance.Validate();
            if (AttestedInstance.InstanceId != InstanceId || !string.Equals(AttestedInstance.ExecutorKey, executorKey, StringComparison.Ordinal))
            {
                throw new ArgumentException("Recovery candidate attestation does not match its opaque instance fence.");
            }
        }

        if (ActiveHandle is not null)
        {
            ActiveHandle.Validate();
            if (!string.Equals(ActiveHandle.ExecutorKey, executorKey, StringComparison.Ordinal))
            {
                throw new ArgumentException("Recovery candidate active handle does not match its executor fence.");
            }
        }
    }
}

public sealed record NativeWorkerLaunchRequest(Guid InstanceId, string ExecutorKey, string ExecutableFingerprint, string ProtocolVersion)
{
    public void Validate()
    {
        if (InstanceId == Guid.Empty) throw new ArgumentException("A worker instance ID is required.", nameof(InstanceId));
        GpuSchedulerOpaqueKeyValidator.RequireCanonical(ExecutorKey, nameof(ExecutorKey), GpuSchedulerOpaqueKeyValidator.MaximumExecutorFenceKeyLength);
        NativeWorkerProtocol.RequireVersion(ProtocolVersion, nameof(ProtocolVersion));
        if (ExecutableFingerprint is null || ExecutableFingerprint.Length != 64 || ExecutableFingerprint.Any(c => c is < '0' or > '9' and < 'a' or > 'f')) throw new ArgumentException("A canonical executable SHA-256 fingerprint is required.", nameof(ExecutableFingerprint));
    }
}

public sealed record NativeWorkerConnectionAttestation(NativeWorkerInstanceHandle Instance, string ExecutableFingerprint)
{
    public void Validate() { ArgumentNullException.ThrowIfNull(Instance); Instance.Validate(); new NativeWorkerLaunchRequest(Instance.InstanceId, Instance.ExecutorKey, ExecutableFingerprint, Instance.ProtocolVersion).Validate(); }
}

public sealed record NativeWorkerLifecycleEvidence(
    Guid OperationId,
    Guid InstanceId,
    NativeWorkerLifecycleClass Class,
    DateTimeOffset ObservedAtUtc,
    int? OutcomeCode,
    string RequestFingerprint)
{
    public void Validate()
    {
        if (OperationId == Guid.Empty)
        {
            throw new ArgumentException("A worker lifecycle operation ID is required.", nameof(OperationId));
        }

        if (InstanceId == Guid.Empty)
        {
            throw new ArgumentException("A worker lifecycle instance ID is required.", nameof(InstanceId));
        }

        if (!Enum.IsDefined(Class))
        {
            throw new ArgumentOutOfRangeException(nameof(Class));
        }

        NativeWorkerInstanceHandle.RequireUtcTimestamp(ObservedAtUtc, nameof(ObservedAtUtc));
        if (RequestFingerprint is null || RequestFingerprint.Length != 64 || RequestFingerprint.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new ArgumentException("A worker lifecycle request fingerprint must be a SHA-256 hex value.", nameof(RequestFingerprint));
        }
    }
}

public sealed record NativeWorkerStoreMutationResult(bool Accepted, bool Committed, bool IsIdempotentReplay = false);
