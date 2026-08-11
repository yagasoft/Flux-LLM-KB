using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using FluxKnowledge.Application.Gpu;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence;

/// <summary>
/// Private, SQL-fenced persistence for native-worker attestation and sanitised lifecycle observations.
/// </summary>
public sealed class SqlNativeWorkerInstanceStore : INativeWorkerInstanceStore
{
    private readonly IDbContextFactory<FluxKnowledgeDbContext> _contextFactory;
    private readonly TimeProvider _timeProvider;
    private readonly IGpuExecutorDispatchStore? _dispatchStore;
    private readonly Func<CancellationToken, ValueTask>? _afterSchedulerUncertaintyCommitted;

    public SqlNativeWorkerInstanceStore(
        IDbContextFactory<FluxKnowledgeDbContext> contextFactory,
        TimeProvider? timeProvider = null,
        IGpuExecutorDispatchStore? dispatchStore = null,
        Func<CancellationToken, ValueTask>? afterSchedulerUncertaintyCommitted = null)
    {
        _contextFactory = contextFactory;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _dispatchStore = dispatchStore;
        _afterSchedulerUncertaintyCommitted = afterSchedulerUncertaintyCommitted;
    }

    public async ValueTask<IReadOnlyList<NativeWorkerRecoveryCandidate>> ReadRecoveryCandidatesAsync(
        string executorKey,
        CancellationToken cancellationToken)
    {
        GpuSchedulerOpaqueKeyValidator.RequireCanonical(
            executorKey,
            nameof(executorKey),
            GpuSchedulerOpaqueKeyValidator.MaximumExecutorFenceKeyLength);
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var rows = await context.NativeWorkerInstances
            .AsNoTracking()
            .Include(instance => instance.ActiveDispatch)
            .Where(instance => instance.ExecutorKey == executorKey &&
                instance.State != (int)NativeWorkerLifecycleClass.Exited &&
                instance.State != (int)NativeWorkerLifecycleClass.TerminationConfirmed)
            .OrderBy(instance => instance.InstanceId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var candidates = new List<NativeWorkerRecoveryCandidate>(rows.Count);
        foreach (var row in rows)
        {
            NativeWorkerInstanceHandle? attested = null;
            if (row.ProcessId is not null && row.ProcessStartedAtUtc is not null)
            {
                attested = NativeWorkerInstanceHandle.Create(
                    row.InstanceId, row.ExecutorKey, row.ProcessId.Value, row.ProcessStartedAtUtc.Value, row.ProtocolVersion);
            }

            GpuExecutorBatchHandle? handle = null;
            if (row.ActiveDispatch is { } dispatch)
            {
                handle = new GpuExecutorBatchHandle(
                    dispatch.BatchId,
                    dispatch.CapacitySlotKey,
                    dispatch.ExecutorKey,
                    dispatch.AdmissionGeneration,
                    dispatch.DispatchId);
            }

            var candidate = new NativeWorkerRecoveryCandidate(
                row.InstanceId,
                (NativeWorkerLifecycleClass)row.State,
                attested,
                handle);
            candidate.Validate(executorKey);
            candidates.Add(candidate);
        }

        return candidates;
    }

    public async ValueTask<NativeWorkerStoreMutationResult> CreateAsync(
        Guid operationId,
        NativeWorkerLaunchRequest launch,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(launch);
        launch.Validate();
        var now = _timeProvider.GetUtcNow();
        var fingerprint = CreateRequestFingerprint(
            "create", launch.InstanceId.ToString("N"), launch.ExecutorKey, launch.ExecutableFingerprint, launch.ProtocolVersion);
        var evidence = new NativeWorkerLifecycleEvidence(
            operationId, launch.InstanceId, NativeWorkerLifecycleClass.LaunchRequested, now, null, fingerprint);

        return await ExecuteMutationAsync(
                evidence,
                async (context, token) =>
                {
                    var existing = await context.NativeWorkerInstances.SingleOrDefaultAsync(
                        candidate => candidate.InstanceId == launch.InstanceId, token).ConfigureAwait(false);
                    if (existing is not null)
                    {
                        if (existing.ExecutorKey != launch.ExecutorKey || existing.ExecutableFingerprint != launch.ExecutableFingerprint || existing.ProtocolVersion != launch.ProtocolVersion)
                        {
                            throw new InvalidOperationException("The native worker instance ID is already attested to a different process.");
                        }

                        return false;
                    }

                    context.NativeWorkerInstances.Add(new NativeWorkerInstanceEntity
                    {
                        InstanceId = launch.InstanceId, ExecutorKey = launch.ExecutorKey,
                        ExecutableFingerprint = launch.ExecutableFingerprint, ProtocolVersion = launch.ProtocolVersion,
                        State = (int)NativeWorkerLifecycleClass.LaunchRequested,
                        LaunchedAtUtc = now
                    });
                    return true;
                },
                cancellationToken)
            .ConfigureAwait(false);
    }

    public ValueTask<NativeWorkerStoreMutationResult> AppendEvidenceAsync(
        NativeWorkerLifecycleEvidence evidence,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        evidence.Validate();
        RequireSha256(evidence.RequestFingerprint, nameof(evidence.RequestFingerprint));
        return ExecuteMutationAsync(
            evidence,
            async (context, token) =>
            {
                var instance = await context.NativeWorkerInstances.SingleOrDefaultAsync(
                    candidate => candidate.InstanceId == evidence.InstanceId, token).ConfigureAwait(false);
                if (instance is null || !CanTransition(instance, evidence))
                {
                    return false;
                }

                ApplyState(instance, evidence);
                return true;
            },
            cancellationToken);
    }

    public ValueTask<NativeWorkerStoreMutationResult> RecordConnectionAsync(
        Guid operationId,
        NativeWorkerConnectionAttestation attestation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(attestation); attestation.Validate();
        var instance = attestation.Instance;
        var evidence = new NativeWorkerLifecycleEvidence(
            operationId,
            instance.InstanceId,
            NativeWorkerLifecycleClass.Connected,
            _timeProvider.GetUtcNow(),
            null,
            CreateRequestFingerprint(
                "connection", instance.InstanceId.ToString("N"), instance.ExecutorKey,
                instance.ProcessId.ToString(CultureInfo.InvariantCulture),
                instance.ProcessStartedAtUtc.UtcDateTime.Ticks.ToString(CultureInfo.InvariantCulture), instance.ProtocolVersion, attestation.ExecutableFingerprint));
        return ExecuteMutationAsync(
            evidence,
            async (context, token) =>
            {
                var persisted = await context.NativeWorkerInstances.SingleOrDefaultAsync(
                    candidate => candidate.InstanceId == instance.InstanceId, token).ConfigureAwait(false);
                if (persisted is null || persisted.ExecutorKey != instance.ExecutorKey ||
                    (persisted.ProcessId is not null && persisted.ProcessId != instance.ProcessId) ||
                    (persisted.ProcessStartedAtUtc is not null && persisted.ProcessStartedAtUtc != instance.ProcessStartedAtUtc) ||
                    persisted.ProtocolVersion != instance.ProtocolVersion || persisted.ExecutableFingerprint != attestation.ExecutableFingerprint || !CanTransition(persisted, evidence))
                {
                    return false;
                }

                persisted.ProcessId ??= instance.ProcessId;
                persisted.ProcessStartedAtUtc ??= instance.ProcessStartedAtUtc;
                if (persisted.ProcessId != instance.ProcessId || persisted.ProcessStartedAtUtc != instance.ProcessStartedAtUtc) return false;
                ApplyState(persisted, evidence);
                return true;
            },
            cancellationToken);
    }

    public ValueTask<NativeWorkerStoreMutationResult> RecordHeartbeatAsync(
        Guid operationId,
        Guid instanceId,
        DateTimeOffset observedAtUtc,
        CancellationToken cancellationToken)
    {
        NativeWorkerInstanceHandle.RequireUtcTimestamp(observedAtUtc, nameof(observedAtUtc));
        return AppendEvidenceAsync(
            new NativeWorkerLifecycleEvidence(
                operationId, instanceId, NativeWorkerLifecycleClass.HeartbeatObserved, observedAtUtc, null,
                CreateRequestFingerprint("heartbeat", instanceId.ToString("N"), observedAtUtc.UtcDateTime.Ticks.ToString(CultureInfo.InvariantCulture))),
            cancellationToken);
    }

    public ValueTask<NativeWorkerStoreMutationResult> RecordExitAsync(
        Guid operationId,
        Guid instanceId,
        DateTimeOffset observedAtUtc,
        int? exitCode,
        CancellationToken cancellationToken)
    {
        NativeWorkerInstanceHandle.RequireUtcTimestamp(observedAtUtc, nameof(observedAtUtc));
        RequireOutcomeCode(exitCode, nameof(exitCode));
        return AppendEvidenceAsync(
            new NativeWorkerLifecycleEvidence(
                operationId, instanceId, NativeWorkerLifecycleClass.Exited, observedAtUtc, exitCode,
                CreateRequestFingerprint(
                    "exit", instanceId.ToString("N"), observedAtUtc.UtcDateTime.Ticks.ToString(CultureInfo.InvariantCulture),
                    exitCode?.ToString(CultureInfo.InvariantCulture) ?? string.Empty)),
            cancellationToken);
    }

    public ValueTask<NativeWorkerStoreMutationResult> BindExactActiveDispatchAsync(Guid operationId, NativeWorkerInstanceHandle instance, GpuExecutorBatchHandle handle, DateTimeOffset observedAtUtc, CancellationToken cancellationToken)
    {
        instance.Validate(); handle.Validate(); NativeWorkerInstanceHandle.RequireUtcTimestamp(observedAtUtc, nameof(observedAtUtc));
        var fingerprint = CreateHandleFingerprint("bind", instance, handle);
        return ExecuteAssociationMutationAsync(operationId, instance.InstanceId, fingerprint, async (context, token) =>
        {
            var persisted = await context.NativeWorkerInstances.SingleOrDefaultAsync(x => x.InstanceId == instance.InstanceId, token).ConfigureAwait(false);
            if (persisted is null || !MatchesAttestation(persisted, instance) || persisted.ExecutorKey != handle.ExecutorKey)
            {
                return new NativeWorkerStoreMutationResult(false, false);
            }

            var recordedOperation = await context.GpuExecutorDispatches.SingleOrDefaultAsync(x => x.NativeWorkerBindOperationId == operationId, token).ConfigureAwait(false);
            if (recordedOperation is not null)
            {
                ValidateAssociationReplay(recordedOperation.NativeWorkerBindRequestFingerprint, fingerprint);
                return new NativeWorkerStoreMutationResult(true, true, IsIdempotentReplay: true);
            }

            var dispatch = await context.GpuExecutorDispatches.SingleOrDefaultAsync(x => x.DispatchId == handle.DispatchId && x.BatchId == handle.BatchId && x.CapacitySlotKey == handle.CapacitySlotKey && x.ExecutorKey == handle.ExecutorKey && x.AdmissionGeneration == handle.AdmissionGeneration, token).ConfigureAwait(false);
            if (dispatch is null)
            {
                return new NativeWorkerStoreMutationResult(false, false);
            }

            if (dispatch.NativeWorkerBindOperationId is not null || persisted.ActiveDispatchId is not null || !IsBindable(persisted.State))
            {
                return new NativeWorkerStoreMutationResult(false, false);
            }

            if (dispatch.State is not ((int)GpuExecutorDispatchState.PendingDelivery or (int)GpuExecutorDispatchState.Acknowledged or (int)GpuExecutorDispatchState.ReceiptRecorded))
            {
                return new NativeWorkerStoreMutationResult(false, false);
            }

            persisted.ActiveDispatchId = handle.DispatchId;
            dispatch.NativeWorkerBindOperationId = operationId;
            dispatch.NativeWorkerBindRequestFingerprint = fingerprint;
            return new NativeWorkerStoreMutationResult(true, true);
        }, cancellationToken);
    }

    public ValueTask<NativeWorkerStoreMutationResult> ClearExactActiveDispatchAsync(Guid operationId, NativeWorkerInstanceHandle instance, GpuExecutorBatchHandle handle, DateTimeOffset observedAtUtc, CancellationToken cancellationToken)
    {
        instance.Validate(); handle.Validate(); NativeWorkerInstanceHandle.RequireUtcTimestamp(observedAtUtc, nameof(observedAtUtc));
        var fingerprint = CreateHandleFingerprint("clear", instance, handle);
        return ExecuteAssociationMutationAsync(operationId, instance.InstanceId, fingerprint, async (context, token) =>
        {
            var persisted = await context.NativeWorkerInstances.SingleOrDefaultAsync(x => x.InstanceId == instance.InstanceId, token).ConfigureAwait(false);
            if (persisted is null || !MatchesAttestation(persisted, instance))
            {
                return new NativeWorkerStoreMutationResult(false, false);
            }

            var recordedOperation = await context.GpuExecutorDispatches.SingleOrDefaultAsync(x => x.NativeWorkerClearOperationId == operationId, token).ConfigureAwait(false);
            if (recordedOperation is not null)
            {
                ValidateAssociationReplay(recordedOperation.NativeWorkerClearRequestFingerprint, fingerprint);
                return new NativeWorkerStoreMutationResult(true, true, IsIdempotentReplay: true);
            }

            var dispatch = await context.GpuExecutorDispatches.SingleOrDefaultAsync(x => x.DispatchId == handle.DispatchId && x.BatchId == handle.BatchId && x.CapacitySlotKey == handle.CapacitySlotKey && x.ExecutorKey == handle.ExecutorKey && x.AdmissionGeneration == handle.AdmissionGeneration, token).ConfigureAwait(false);
            if (dispatch is null)
            {
                return new NativeWorkerStoreMutationResult(false, false);
            }

            if (dispatch.NativeWorkerClearOperationId is not null || persisted.ActiveDispatchId != handle.DispatchId)
            {
                return new NativeWorkerStoreMutationResult(false, false);
            }

            persisted.ActiveDispatchId = null;
            dispatch.NativeWorkerClearOperationId = operationId;
            dispatch.NativeWorkerClearRequestFingerprint = fingerprint;
            return new NativeWorkerStoreMutationResult(true, true);
        }, cancellationToken);
    }

    public async ValueTask<NativeWorkerStoreMutationResult> MarkExactHandleUncertainAsync(
        Guid operationId,
        NativeWorkerInstanceHandle instance,
        GpuExecutorBatchHandle handle,
        DateTimeOffset observedAtUtc,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(instance);
        ArgumentNullException.ThrowIfNull(handle);
        instance.Validate();
        handle.Validate();
        NativeWorkerInstanceHandle.RequireUtcTimestamp(observedAtUtc, nameof(observedAtUtc));
        if (!string.Equals(instance.ExecutorKey, handle.ExecutorKey, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The worker and durable executor handle do not share an executor key.");
        }

        var evidence = new NativeWorkerLifecycleEvidence(
                    operationId, instance.InstanceId, NativeWorkerLifecycleClass.Lost, observedAtUtc, null,
                    CreateRequestFingerprint(
                        "exact-handle-uncertain", instance.InstanceId.ToString("N"), handle.DispatchId.ToString("N"),
                        handle.BatchId.ToString("N"), handle.CapacitySlotKey, handle.ExecutorKey,
                        handle.AdmissionGeneration.ToString(CultureInfo.InvariantCulture)));
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false);
        await AcquireMutationFencesAsync(context, transaction.GetDbTransaction(), operationId, instance.InstanceId, cancellationToken).ConfigureAwait(false);
        var existing = await context.NativeWorkerLifecycleEvidence.SingleOrDefaultAsync(x => x.OperationId == operationId, cancellationToken).ConfigureAwait(false);
        if (existing is not null) { ValidateReplay(existing, evidence); await transaction.CommitAsync(cancellationToken).ConfigureAwait(false); return new NativeWorkerStoreMutationResult(true, true, true); }
        var persisted = await context.NativeWorkerInstances.SingleOrDefaultAsync(x => x.InstanceId == instance.InstanceId, cancellationToken).ConfigureAwait(false);
        if (!MatchesAttestation(persisted, instance) || persisted!.ActiveDispatchId != handle.DispatchId) { await transaction.CommitAsync(cancellationToken).ConfigureAwait(false); return new NativeWorkerStoreMutationResult(false, false); }
        var dispatchResult = await (_dispatchStore ?? new SqlGpuSchedulerStore(_contextFactory)).MarkDeliveryUncertainAsync(new GpuExecutorDeliveryUncertainty(operationId, handle), cancellationToken).ConfigureAwait(false);
        if (!dispatchResult.Accepted) { await transaction.CommitAsync(cancellationToken).ConfigureAwait(false); return new NativeWorkerStoreMutationResult(false, dispatchResult.Committed); }
        if (_afterSchedulerUncertaintyCommitted is not null) await _afterSchedulerUncertaintyCommitted(cancellationToken).ConfigureAwait(false);
        persisted.ActiveDispatchId = null; ApplyState(persisted, evidence);
        context.NativeWorkerLifecycleEvidence.Add(new NativeWorkerLifecycleEvidenceEntity { OperationId = evidence.OperationId, InstanceId = evidence.InstanceId, LifecycleClass = (int)evidence.Class, ObservedAtUtc = evidence.ObservedAtUtc, OutcomeCode = evidence.OutcomeCode, RequestFingerprint = evidence.RequestFingerprint, CreatedAtUtc = _timeProvider.GetUtcNow() });
        OperatorEventAppender.Add(context, OperatorEventDraft.NativeWorkerLifecycle(
            evidence.Class,
            evidence.InstanceId,
            evidence.OutcomeCode,
            evidence.ObservedAtUtc));
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false); await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new NativeWorkerStoreMutationResult(true, true);
    }

    private async ValueTask<NativeWorkerStoreMutationResult> ExecuteMutationAsync(
        NativeWorkerLifecycleEvidence evidence,
        Func<FluxKnowledgeDbContext, CancellationToken, Task<bool>> mutate,
        CancellationToken cancellationToken)
    {
        evidence.Validate();
        if (evidence.OperationId == Guid.Empty)
        {
            throw new ArgumentException("A native worker operation ID is required.", nameof(evidence));
        }

        await using var executionContext = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var strategy = executionContext.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false);
            await AcquireMutationFencesAsync(context, transaction.GetDbTransaction(), evidence.OperationId, evidence.InstanceId, cancellationToken).ConfigureAwait(false);
            var existing = await context.NativeWorkerLifecycleEvidence.SingleOrDefaultAsync(
                candidate => candidate.OperationId == evidence.OperationId, cancellationToken).ConfigureAwait(false);
            if (existing is not null)
            {
                ValidateReplay(existing, evidence);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return new NativeWorkerStoreMutationResult(true, true, IsIdempotentReplay: true);
            }

            if (!await mutate(context, cancellationToken).ConfigureAwait(false))
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return new NativeWorkerStoreMutationResult(false, false);
            }

            context.NativeWorkerLifecycleEvidence.Add(new NativeWorkerLifecycleEvidenceEntity
            {
                OperationId = evidence.OperationId,
                InstanceId = evidence.InstanceId,
                LifecycleClass = (int)evidence.Class,
                ObservedAtUtc = evidence.ObservedAtUtc,
                OutcomeCode = evidence.OutcomeCode,
                RequestFingerprint = evidence.RequestFingerprint,
                CreatedAtUtc = _timeProvider.GetUtcNow()
            });
            OperatorEventAppender.Add(
                context,
                OperatorEventDraft.NativeWorkerLifecycle(
                    evidence.Class,
                    evidence.InstanceId,
                    evidence.OutcomeCode,
                    evidence.ObservedAtUtc));
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new NativeWorkerStoreMutationResult(true, true);
        }).ConfigureAwait(false);
    }

    private async ValueTask<NativeWorkerStoreMutationResult> ExecuteAssociationMutationAsync(
        Guid operationId,
        Guid instanceId,
        string requestFingerprint,
        Func<FluxKnowledgeDbContext, CancellationToken, Task<NativeWorkerStoreMutationResult>> mutate,
        CancellationToken cancellationToken)
    {
        if (operationId == Guid.Empty)
        {
            throw new ArgumentException("A native worker association operation ID is required.", nameof(operationId));
        }

        if (instanceId == Guid.Empty)
        {
            throw new ArgumentException("A native worker association instance ID is required.", nameof(instanceId));
        }

        RequireSha256(requestFingerprint, nameof(requestFingerprint));
        await using var executionContext = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var strategy = executionContext.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false);
            await AcquireMutationFencesAsync(context, transaction.GetDbTransaction(), operationId, instanceId, cancellationToken).ConfigureAwait(false);
            var result = await mutate(context, cancellationToken).ConfigureAwait(false);
            if (result.Accepted && !result.IsIdempotentReplay)
            {
                await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return result;
        }).ConfigureAwait(false);
    }

    private static bool CanTransition(NativeWorkerInstanceEntity instance, NativeWorkerLifecycleEvidence evidence)
    {
        if (instance.State is (int)NativeWorkerLifecycleClass.Exited or (int)NativeWorkerLifecycleClass.TerminationConfirmed)
        {
            return false;
        }

        return evidence.Class switch
        {
            NativeWorkerLifecycleClass.HeartbeatObserved =>
                instance.LastHeartbeatAtUtc is null || evidence.ObservedAtUtc >= instance.LastHeartbeatAtUtc,
            NativeWorkerLifecycleClass.Connected => instance.ConnectedAtUtc is null,
            NativeWorkerLifecycleClass.Exited => instance.ExitedAtUtc is null,
            _ => true
        };
    }

    private static void ApplyState(NativeWorkerInstanceEntity instance, NativeWorkerLifecycleEvidence evidence)
    {
        instance.State = (int)evidence.Class;
        switch (evidence.Class)
        {
            case NativeWorkerLifecycleClass.Connected:
                instance.ConnectedAtUtc = evidence.ObservedAtUtc;
                break;
            case NativeWorkerLifecycleClass.HeartbeatObserved:
                instance.LastHeartbeatAtUtc = evidence.ObservedAtUtc;
                break;
            case NativeWorkerLifecycleClass.Exited:
            case NativeWorkerLifecycleClass.TerminationConfirmed:
                instance.ExitedAtUtc = evidence.ObservedAtUtc;
                break;
        }
    }

    private static bool MatchesAttestation(NativeWorkerInstanceEntity? persisted, NativeWorkerInstanceHandle instance) =>
        persisted is not null && persisted.ExecutorKey == instance.ExecutorKey && persisted.ProcessId == instance.ProcessId && persisted.ProcessStartedAtUtc == instance.ProcessStartedAtUtc && persisted.ProtocolVersion == instance.ProtocolVersion;

    private static bool IsBindable(int state) => state is (int)NativeWorkerLifecycleClass.Connected or (int)NativeWorkerLifecycleClass.Ready or (int)NativeWorkerLifecycleClass.HeartbeatObserved;

    private static async Task AcquireMutationFencesAsync(
        FluxKnowledgeDbContext context,
        DbTransaction transaction,
        Guid operationId,
        Guid instanceId,
        CancellationToken cancellationToken)
    {
        await AcquireTransactionApplicationLockAsync(context, transaction, $"FluxKnowledge.NativeWorker.Operation:{operationId:N}", cancellationToken).ConfigureAwait(false);
        await AcquireTransactionApplicationLockAsync(context, transaction, $"FluxKnowledge.NativeWorker.Instance:{instanceId:N}", cancellationToken).ConfigureAwait(false);
    }

    private static async Task AcquireTransactionApplicationLockAsync(
        FluxKnowledgeDbContext context,
        DbTransaction transaction,
        string resource,
        CancellationToken cancellationToken)
    {
        await context.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = context.Database.GetDbConnection().CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "DECLARE @result int; EXEC @result = sp_getapplock @Resource = @resource, @LockMode = N'Exclusive', @LockOwner = N'Transaction', @LockTimeout = 10000; SELECT @result;";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "@resource";
        parameter.DbType = DbType.String;
        parameter.Value = resource;
        command.Parameters.Add(parameter);
        var result = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
        if (result is not 0 and not 1)
        {
            throw new InvalidOperationException("Could not acquire the native worker lifecycle fence. Check SQL Server locking and permissions.");
        }
    }

    private static void ValidateReplay(NativeWorkerLifecycleEvidenceEntity existing, NativeWorkerLifecycleEvidence evidence)
    {
        if (existing.InstanceId != evidence.InstanceId || existing.LifecycleClass != (int)evidence.Class ||
            (evidence.Class is not NativeWorkerLifecycleClass.LaunchRequested and not NativeWorkerLifecycleClass.Connected && existing.ObservedAtUtc != evidence.ObservedAtUtc) || existing.OutcomeCode != evidence.OutcomeCode ||
            !string.Equals(existing.RequestFingerprint, evidence.RequestFingerprint, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The native worker lifecycle operation does not match its immutable request.");
        }
    }

    private static void ValidateAssociationReplay(string? existingFingerprint, string requestFingerprint)
    {
        if (!string.Equals(existingFingerprint, requestFingerprint, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The native worker association operation does not match its immutable request.");
        }
    }

    private static string CreateRequestFingerprint(params string[] fields)
    {
        var canonical = new StringBuilder();
        foreach (var field in fields)
        {
            ArgumentNullException.ThrowIfNull(field);
            canonical.Append(field.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(field).Append('|');
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString()))).ToLowerInvariant();
    }

    private static string CreateHandleFingerprint(string kind, NativeWorkerInstanceHandle instance, GpuExecutorBatchHandle handle) =>
        CreateRequestFingerprint(kind, instance.InstanceId.ToString("N"), instance.ExecutorKey, instance.ProcessId.ToString(CultureInfo.InvariantCulture), instance.ProcessStartedAtUtc.UtcDateTime.Ticks.ToString(CultureInfo.InvariantCulture), instance.ProtocolVersion, handle.DispatchId.ToString("N"), handle.BatchId.ToString("N"), handle.CapacitySlotKey, handle.ExecutorKey, handle.AdmissionGeneration.ToString(CultureInfo.InvariantCulture));

    private static void RequireSha256(string value, string parameterName)
    {
        if (value.Length != 64 || value.Any(character => character is < '0' or > '9' and < 'a' or > 'f'))
        {
            throw new ArgumentException("A canonical SHA-256 fingerprint is required.", parameterName);
        }
    }

    private static void RequireOutcomeCode(int? value, string parameterName)
    {
        if (value is < -32768 or > 65535)
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }
    }
}
