using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using FluxKnowledge.Application.Gpu;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Domain.Gpu;
using FluxKnowledge.Domain.Jobs;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence;

public sealed class SqlGpuSchedulerStore : IGpuSchedulerStore
{
    private readonly IDbContextFactory<FluxKnowledgeDbContext> _contextFactory;
    private readonly Func<CancellationToken, ValueTask>? _afterMiniTaskPersisted;
    private readonly Func<CancellationToken, ValueTask>? _beforeIdempotencyRead;
    private readonly Func<CancellationToken, ValueTask>? _beforeAdmissionLockAttempt;
    private readonly Func<CancellationToken, ValueTask>? _afterAdmissionLockAcquired;
    private readonly Func<CancellationToken, ValueTask>? _afterAdmissionCommitted;
    private readonly Func<CancellationToken, ValueTask>? _afterLifecycleCommitted;
    private readonly Func<CancellationToken, ValueTask>? _afterWakeConsumptionCommitted;
    private readonly TimeProvider _timeProvider;

    public SqlGpuSchedulerStore(
        IDbContextFactory<FluxKnowledgeDbContext> contextFactory,
        Func<CancellationToken, ValueTask>? afterMiniTaskPersisted = null,
        TimeProvider? timeProvider = null,
        Func<CancellationToken, ValueTask>? beforeIdempotencyRead = null,
        Func<CancellationToken, ValueTask>? afterAdmissionLockAcquired = null,
        Func<CancellationToken, ValueTask>? beforeAdmissionLockAttempt = null,
        Func<CancellationToken, ValueTask>? afterAdmissionCommitted = null,
        Func<CancellationToken, ValueTask>? afterLifecycleCommitted = null,
        Func<CancellationToken, ValueTask>? afterWakeConsumptionCommitted = null)
    {
        _contextFactory = contextFactory;
        _afterMiniTaskPersisted = afterMiniTaskPersisted;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _beforeIdempotencyRead = beforeIdempotencyRead;
        _beforeAdmissionLockAttempt = beforeAdmissionLockAttempt;
        _afterAdmissionLockAcquired = afterAdmissionLockAcquired;
        _afterAdmissionCommitted = afterAdmissionCommitted;
        _afterLifecycleCommitted = afterLifecycleCommitted;
        _afterWakeConsumptionCommitted = afterWakeConsumptionCommitted;
    }

    public async ValueTask<GpuMiniTaskHandoffResult> GpuTaskHandoffAsync(
        GpuMiniTaskHandoffRequest request,
        CancellationToken cancellationToken)
    {
        ValidateHandoffRequest(request);

        await using var executionContext = await _contextFactory
            .CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);
        var strategy = executionContext.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(
                async () =>
                {
                    await using var context = await _contextFactory
                        .CreateDbContextAsync(cancellationToken)
                        .ConfigureAwait(false);
                    return await HandoffWithinTransactionAsync(context, request, cancellationToken)
                        .ConfigureAwait(false);
                })
            .ConfigureAwait(false);
    }

    public ValueTask<GpuSchedulerAdmissionRoundResult> RunAdmissionRoundAsync(
        GpuSchedulerWakeReason wakeReason,
        GpuSchedulerOptions options,
        Func<GpuBatchCandidate, CancellationToken, ValueTask<GpuAdmissionDecision>> decideAdmission,
        CancellationToken cancellationToken) =>
        RunAdmissionRoundAsync(
            Guid.NewGuid(),
            wakeReason,
            options,
            decideAdmission,
            cancellationToken);

    public async ValueTask<GpuSchedulerAdmissionRoundResult> RunAdmissionRoundAsync(
        Guid operationId,
        GpuSchedulerWakeReason wakeReason,
        GpuSchedulerOptions options,
        Func<GpuBatchCandidate, CancellationToken, ValueTask<GpuAdmissionDecision>> decideAdmission,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(decideAdmission);
        if (operationId == Guid.Empty)
        {
            throw new ArgumentException("A scheduler admission operation ID is required.", nameof(operationId));
        }

        if (wakeReason == 0 || (wakeReason & ~KnownWakeReasons) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(wakeReason));
        }

        await using var executionContext = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var strategy = executionContext.Database.CreateExecutionStrategy();
        var batchId = Guid.NewGuid();
        return await strategy.ExecuteAsync(
                async () =>
                {
                    await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
                    return await RunAdmissionRoundWithinTransactionAsync(
                            context, wakeReason, options, decideAdmission, operationId, batchId, cancellationToken)
                        .ConfigureAwait(false);
                })
            .ConfigureAwait(false);
    }

    private async Task<GpuSchedulerAdmissionRoundResult> RunAdmissionRoundWithinTransactionAsync(
        FluxKnowledgeDbContext context,
        GpuSchedulerWakeReason wakeReason,
        GpuSchedulerOptions options,
        Func<GpuBatchCandidate, CancellationToken, ValueTask<GpuAdmissionDecision>> decideAdmission,
        Guid operationId,
        Guid batchId,
        CancellationToken cancellationToken)
    {
        await using var transaction = await context.Database
            .BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken)
            .ConfigureAwait(false);
        if (_beforeAdmissionLockAttempt is not null)
        {
            await _beforeAdmissionLockAttempt(cancellationToken).ConfigureAwait(false);
        }

        await AcquireAdmissionLockAsync(context, transaction.GetDbTransaction(), cancellationToken).ConfigureAwait(false);
        var receipt = await context.GpuSchedulerOperationReceipts
            .SingleOrDefaultAsync(candidate => candidate.OperationId == operationId, cancellationToken)
            .ConfigureAwait(false);
        if (receipt is not null)
        {
            var replay = AdmissionResultFromReceipt(receipt, wakeReason, options);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return replay;
        }

        if (_afterAdmissionLockAcquired is not null)
        {
            await _afterAdmissionLockAcquired(cancellationToken).ConfigureAwait(false);
        }

        var now = _timeProvider.GetUtcNow();
        var selected = await SelectBatchAsync(context, wakeReason, options, now, cancellationToken).ConfigureAwait(false);
        if (selected.Count == 0)
        {
            RecordAdmissionReceipt(
                context,
                operationId,
                batchId,
                wakeReason,
                options,
                new GpuSchedulerAdmissionRoundResult(false, GpuAdmissionDisposition.Busy, null));
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            if (_afterAdmissionCommitted is not null)
            {
                await _afterAdmissionCommitted(cancellationToken).ConfigureAwait(false);
            }

            return new GpuSchedulerAdmissionRoundResult(false, GpuAdmissionDisposition.Busy, null);
        }

        var head = selected[0];
        var candidate = new GpuBatchCandidate(
            (GpuPriorityLane)head.PriorityLane,
            head.ModelRuntimeKey,
            head.SettingsFingerprint,
            selected.Count,
            selected.Sum(task => task.EstimatedBytes));
        var gateDecision = await decideAdmission(candidate, cancellationToken).ConfigureAwait(false);
        if (gateDecision is null)
        {
            throw new InvalidOperationException("The GPU admission gate returned no decision.");
        }

        var decision = gateDecision.Validate(options);

        return decision.Disposition switch
        {
            GpuAdmissionDisposition.Busy => await CommitBusyAsync(
                    context, transaction, selected, now, wakeReason, options, operationId, batchId, cancellationToken)
                .ConfigureAwait(false),
            GpuAdmissionDisposition.Defer => await CommitDeferralAsync(
                    context, transaction, selected, now, decision.RetryAfter!.Value, wakeReason, options, operationId, batchId, cancellationToken)
                .ConfigureAwait(false),
            GpuAdmissionDisposition.Admit => await CommitAdmissionAsync(
                    context, transaction, selected, now, decision, wakeReason, options, operationId, batchId, cancellationToken)
                .ConfigureAwait(false),
            _ => throw new ArgumentOutOfRangeException(nameof(decision))
        };
    }

    public async ValueTask<GpuBatchCallbackResult> ApplyBatchCallbackAsync(
        Guid operationId,
        GpuBatchCallback callback,
        CancellationToken cancellationToken)
    {
        RequireLifecycleOperationId(operationId);
        ArgumentNullException.ThrowIfNull(callback);
        ArgumentNullException.ThrowIfNull(callback.Outcomes);
        callback = callback with { Outcomes = callback.Outcomes.ToArray() };
        callback.Validate();
        var requestFingerprint = CreateCallbackRequestFingerprint(callback);
        await using var executionContext = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var strategy = executionContext.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(() => ApplyBatchCallbackWithinTransactionAsync(
            callback, operationId, requestFingerprint, cancellationToken)).ConfigureAwait(false);
    }

    private async Task<GpuBatchCallbackResult> ApplyBatchCallbackWithinTransactionAsync(
        GpuBatchCallback callback,
        Guid operationId,
        string requestFingerprint,
        CancellationToken cancellationToken)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false);
        var receipt = await context.GpuSchedulerOperationReceipts.SingleOrDefaultAsync(candidate => candidate.OperationId == operationId, cancellationToken)
            .ConfigureAwait(false);
        if (receipt is not null)
        {
            ValidateReceiptForRequest(receipt, "callback", requestFingerprint);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new GpuBatchCallbackResult(receipt.Accepted, receipt.Committed);
        }
        var batch = await context.GpuBatches.SingleOrDefaultAsync(candidate =>
                candidate.Id == callback.BatchId &&
                candidate.CapacitySlotKey == callback.CapacitySlotKey &&
                candidate.OwnerKey == callback.OwnerKey &&
                candidate.AdmissionGeneration == callback.AdmissionGeneration,
            cancellationToken).ConfigureAwait(false);
        var slot = await context.GpuCapacitySlots.SingleOrDefaultAsync(candidate =>
                candidate.SlotKey == callback.CapacitySlotKey &&
                candidate.ActiveBatchId == callback.BatchId &&
                candidate.OwnerKey == callback.OwnerKey &&
                candidate.State == (int)GpuCapacitySlotState.Reserved,
            cancellationToken).ConfigureAwait(false);
        if (batch is null || slot is null || !IsCallbackStateEligible(batch, callback.Kind))
        {
            RecordReceipt(context, operationId, "callback", callback.BatchId, callback.CapacitySlotKey, callback.OwnerKey, callback.AdmissionGeneration, false, false, 0, requestFingerprint);
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new GpuBatchCallbackResult(Accepted: false, Committed: false);
        }

        var activeTasks = await context.GpuMiniTasks.Where(task =>
                task.BatchId == callback.BatchId &&
                task.AdmissionGeneration == callback.AdmissionGeneration &&
                task.ExecutionState == (int)GpuMiniTaskExecutionState.Active)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        if (!CallbackOutcomesMatch(callback, activeTasks))
        {
            RecordReceipt(context, operationId, "callback", callback.BatchId, callback.CapacitySlotKey, callback.OwnerKey, callback.AdmissionGeneration, false, false, 0, requestFingerprint);
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new GpuBatchCallbackResult(Accepted: false, Committed: false);
        }

        var now = _timeProvider.GetUtcNow();
        var releasesCapacity = callback.CapacityReleased;
        if (callback.Kind != GpuBatchCallbackKind.SafeBoundary || releasesCapacity)
        {
            var outcomes = callback.Outcomes.ToDictionary(outcome => outcome.MiniTaskId);
            foreach (var task in activeTasks)
            {
                task.ExecutionState = outcomes[task.Id].Disposition == GpuMiniTaskBoundaryDisposition.Completed
                    ? (int)GpuMiniTaskExecutionState.Completed
                    : (int)GpuMiniTaskExecutionState.OutcomeUncertain;
            }
        }

        batch.State = callback.Kind switch
        {
            GpuBatchCallbackKind.SafeBoundary when !releasesCapacity => (int)GpuBatchState.AtSafeBoundary,
            GpuBatchCallbackKind.Completed => (int)GpuBatchState.Completed,
            _ => (int)GpuBatchState.Released
        };
        batch.UpdatedAtUtc = now;
        if (callback.Kind == GpuBatchCallbackKind.SafeBoundary && !releasesCapacity)
        {
            // A retained boundary is explicit liveness evidence.  Updating both tracked rows
            // invalidates any stale diagnostic snapshot without freeing this reservation.
            batch.LastHeartbeatAtUtc = now;
            slot.LastHeartbeatAtUtc = now;
            slot.UpdatedAtUtc = now;
        }
        if (releasesCapacity)
        {
            slot.State = (int)GpuCapacitySlotState.Available;
            slot.ActiveBatchId = null;
            slot.OwnerKey = null;
            slot.LastHeartbeatAtUtc = now;
            slot.UpdatedAtUtc = now;
        }

        var reasons = callback.Kind == GpuBatchCallbackKind.SafeBoundary
            ? GpuSchedulerWakeReason.SafeBoundary
            : (GpuSchedulerWakeReason)0;
        if (releasesCapacity)
        {
            reasons |= GpuSchedulerWakeReason.CapacityReleased;
        }

        await RecordWakeAsync(context, reasons, now, cancellationToken).ConfigureAwait(false);
        RecordReceipt(context, operationId, "callback", callback.BatchId, callback.CapacitySlotKey, callback.OwnerKey, callback.AdmissionGeneration, true, true, (int)reasons, requestFingerprint);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        if (_afterLifecycleCommitted is not null)
        {
            await _afterLifecycleCommitted(cancellationToken).ConfigureAwait(false);
        }
        return new GpuBatchCallbackResult(Accepted: true, Committed: true);
    }

    public async ValueTask<GpuDiagnosticTransitionResult> MarkCapacityUncertainAsync(
        Guid operationId,
        GpuCapacityUncertaintyRequest request,
        CancellationToken cancellationToken)
    {
        RequireLifecycleOperationId(operationId);
        ArgumentNullException.ThrowIfNull(request);
        GpuSchedulerOpaqueKeyValidator.RequireCanonical(request.CapacitySlotKey, nameof(request.CapacitySlotKey));
        GpuSchedulerOpaqueKeyValidator.RequireCanonical(request.OwnerKey, nameof(request.OwnerKey));
        if (request.BatchId == Guid.Empty || request.AdmissionGeneration <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(request));
        }

        ArgumentNullException.ThrowIfNull(request.ObservedSlotRowVersion);
        if (request.ObservedSlotRowVersion.Length != 8)
        {
            throw new ArgumentException(
                "Capacity uncertainty requires the observed eight-byte slot rowversion.",
                nameof(request));
        }

        request = request with { ObservedSlotRowVersion = request.ObservedSlotRowVersion.ToArray() };

        var requestFingerprint = CreateCapacityUncertaintyRequestFingerprint(request);
        await using var executionContext = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var strategy = executionContext.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(() => MarkCapacityUncertainWithinTransactionAsync(request, operationId, requestFingerprint, cancellationToken)).ConfigureAwait(false);
    }

    private async Task<GpuDiagnosticTransitionResult> MarkCapacityUncertainWithinTransactionAsync(
        GpuCapacityUncertaintyRequest request,
        Guid operationId,
        string requestFingerprint,
        CancellationToken cancellationToken)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false);
        var receipt = await context.GpuSchedulerOperationReceipts.SingleOrDefaultAsync(candidate => candidate.OperationId == operationId, cancellationToken).ConfigureAwait(false);
        if (receipt is not null)
        {
            ValidateReceiptForRequest(receipt, "uncertain", requestFingerprint);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new GpuDiagnosticTransitionResult(receipt.Committed);
        }
        var batch = await context.GpuBatches.SingleOrDefaultAsync(candidate =>
                candidate.Id == request.BatchId &&
                candidate.AdmissionGeneration == request.AdmissionGeneration &&
                candidate.OwnerKey == request.OwnerKey &&
                candidate.CapacitySlotKey == request.CapacitySlotKey &&
                (candidate.State == (int)GpuBatchState.Active || candidate.State == (int)GpuBatchState.AtSafeBoundary),
            cancellationToken).ConfigureAwait(false);
        if (batch is null)
        {
            RecordReceipt(context, operationId, "uncertain", request.BatchId, request.CapacitySlotKey, request.OwnerKey, request.AdmissionGeneration, false, false, 0, requestFingerprint);
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new GpuDiagnosticTransitionResult(false);
        }

        var now = _timeProvider.GetUtcNow();
        var marked = await context.Database.ExecuteSqlInterpolatedAsync(
                $"""
                 UPDATE [GpuCapacitySlots]
                 SET [State] = {(int)GpuCapacitySlotState.Uncertain},
                     [UpdatedAtUtc] = {now}
                 WHERE [SlotKey] = {request.CapacitySlotKey}
                   AND [OwnerKey] = {request.OwnerKey}
                   AND [State] = {(int)GpuCapacitySlotState.Reserved}
                   AND [ActiveBatchId] = {request.BatchId}
                   AND [LastHeartbeatAtUtc] = {request.ObservedLastHeartbeatAtUtc}
                   AND [RowVersion] = {request.ObservedSlotRowVersion};
                 """,
                cancellationToken)
            .ConfigureAwait(false);
        if (marked != 1)
        {
            RecordReceipt(context, operationId, "uncertain", request.BatchId, request.CapacitySlotKey, request.OwnerKey, request.AdmissionGeneration, false, false, 0, requestFingerprint);
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new GpuDiagnosticTransitionResult(false);
        }

        batch.State = (int)GpuBatchState.CapacityUncertain;
        batch.UpdatedAtUtc = now;
        RecordReceipt(context, operationId, "uncertain", request.BatchId, request.CapacitySlotKey, request.OwnerKey, request.AdmissionGeneration, true, true, 0, requestFingerprint);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        if (_afterLifecycleCommitted is not null)
        {
            await _afterLifecycleCommitted(cancellationToken).ConfigureAwait(false);
        }
        return new GpuDiagnosticTransitionResult(true);
    }

    public async ValueTask<GpuTrustedReconciliationResult> ReconcileCapacityAsync(
        Guid operationId,
        GpuTrustedCapacityReconciliation request,
        CancellationToken cancellationToken)
    {
        RequireLifecycleOperationId(operationId);
        ArgumentNullException.ThrowIfNull(request);
        GpuSchedulerOpaqueKeyValidator.RequireCanonical(request.CapacitySlotKey, nameof(request.CapacitySlotKey));
        GpuSchedulerOpaqueKeyValidator.RequireCanonical(request.OwnerKey, nameof(request.OwnerKey));
        GpuSchedulerOpaqueKeyValidator.RequireCanonical(request.EvidenceClass, nameof(request.EvidenceClass));
        if (request.BatchId == Guid.Empty || request.AdmissionGeneration <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(request));
        }
        if (!string.Equals(request.EvidenceClass, TrustedCapacityReleaseEvidenceClass, StringComparison.Ordinal))
        {
            throw new ArgumentException("Capacity reconciliation requires verified termination and driver-absence evidence.", nameof(request));
        }

        var requestFingerprint = CreateCapacityReconciliationRequestFingerprint(request);
        await using var executionContext = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var strategy = executionContext.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(() => ReconcileCapacityWithinTransactionAsync(request, operationId, requestFingerprint, cancellationToken)).ConfigureAwait(false);
    }

    private async Task<GpuTrustedReconciliationResult> ReconcileCapacityWithinTransactionAsync(
        GpuTrustedCapacityReconciliation request,
        Guid operationId,
        string requestFingerprint,
        CancellationToken cancellationToken)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false);
        var receipt = await context.GpuSchedulerOperationReceipts.SingleOrDefaultAsync(candidate => candidate.OperationId == operationId, cancellationToken).ConfigureAwait(false);
        if (receipt is not null)
        {
            ValidateReceiptForRequest(receipt, "capacity-reconciliation", requestFingerprint);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new GpuTrustedReconciliationResult(receipt.Committed);
        }
        var slot = await context.GpuCapacitySlots.SingleOrDefaultAsync(candidate =>
                candidate.SlotKey == request.CapacitySlotKey &&
                candidate.State == (int)GpuCapacitySlotState.Uncertain &&
                candidate.ActiveBatchId == request.BatchId &&
                candidate.OwnerKey == request.OwnerKey,
            cancellationToken).ConfigureAwait(false);
        if (slot is null)
        {
            RecordReceipt(context, operationId, "capacity-reconciliation", request.BatchId, request.CapacitySlotKey, request.OwnerKey, request.AdmissionGeneration, false, false, 0, requestFingerprint);
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new GpuTrustedReconciliationResult(false);
        }

        var batch = await context.GpuBatches.SingleOrDefaultAsync(candidate =>
                candidate.Id == request.BatchId &&
                candidate.CapacitySlotKey == request.CapacitySlotKey &&
                candidate.OwnerKey == request.OwnerKey &&
                candidate.AdmissionGeneration == request.AdmissionGeneration &&
                candidate.State == (int)GpuBatchState.CapacityUncertain,
            cancellationToken).ConfigureAwait(false);
        if (batch is null)
        {
            RecordReceipt(context, operationId, "capacity-reconciliation", request.BatchId, request.CapacitySlotKey, request.OwnerKey, request.AdmissionGeneration, false, false, 0, requestFingerprint);
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new GpuTrustedReconciliationResult(false);
        }

        var now = _timeProvider.GetUtcNow();
        slot.State = (int)GpuCapacitySlotState.Available;
        slot.ActiveBatchId = null;
        slot.OwnerKey = null;
        slot.UpdatedAtUtc = now;
        var reasons = GpuSchedulerWakeReason.Reconciliation | GpuSchedulerWakeReason.CapacityReleased;
        await RecordWakeAsync(context, reasons, now, cancellationToken).ConfigureAwait(false);
        RecordReceipt(context, operationId, "capacity-reconciliation", request.BatchId, request.CapacitySlotKey, request.OwnerKey, request.AdmissionGeneration, true, true, (int)reasons, requestFingerprint);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        if (_afterLifecycleCommitted is not null)
        {
            await _afterLifecycleCommitted(cancellationToken).ConfigureAwait(false);
        }
        return new GpuTrustedReconciliationResult(true);
    }

    public async ValueTask<GpuTrustedReconciliationResult> ReconcileTaskOutcomeAsync(
        Guid operationId,
        GpuTaskOutcomeReconciliation request,
        CancellationToken cancellationToken)
    {
        RequireLifecycleOperationId(operationId);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.MiniTaskIds);
        request = request with { MiniTaskIds = request.MiniTaskIds.ToArray() };
        GpuSchedulerOpaqueKeyValidator.RequireCanonical(request.CapacitySlotKey, nameof(request.CapacitySlotKey));
        GpuSchedulerOpaqueKeyValidator.RequireCanonical(request.OwnerKey, nameof(request.OwnerKey));
        GpuSchedulerOpaqueKeyValidator.RequireCanonical(request.EvidenceClass, nameof(request.EvidenceClass));
        if (request.BatchId == Guid.Empty || request.AdmissionGeneration <= 0 ||
            request.MiniTaskIds is null || request.MiniTaskIds.Count == 0 ||
            request.MiniTaskIds.Any(id => id == Guid.Empty) || request.MiniTaskIds.Distinct().Count() != request.MiniTaskIds.Count)
        {
            throw new ArgumentException("Task-outcome reconciliation requires a complete, distinct fenced task set.", nameof(request));
        }

        if (!string.Equals(request.EvidenceClass, TrustedOutcomeUncertainEvidenceClass, StringComparison.Ordinal))
        {
            throw new ArgumentException("Task-outcome reconciliation requires verified unresolved-outcome evidence.", nameof(request));
        }

        var requestFingerprint = CreateTaskOutcomeReconciliationRequestFingerprint(request);
        await using var executionContext = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var strategy = executionContext.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(() => ReconcileTaskOutcomeWithinTransactionAsync(request, operationId, requestFingerprint, cancellationToken)).ConfigureAwait(false);
    }

    private async Task<GpuTrustedReconciliationResult> ReconcileTaskOutcomeWithinTransactionAsync(
        GpuTaskOutcomeReconciliation request,
        Guid operationId,
        string requestFingerprint,
        CancellationToken cancellationToken)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false);
        var receipt = await context.GpuSchedulerOperationReceipts.SingleOrDefaultAsync(candidate => candidate.OperationId == operationId, cancellationToken).ConfigureAwait(false);
        if (receipt is not null)
        {
            ValidateReceiptForRequest(receipt, "outcome-reconciliation", requestFingerprint);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new GpuTrustedReconciliationResult(receipt.Committed);
        }
        var batch = await context.GpuBatches.SingleOrDefaultAsync(candidate =>
                candidate.Id == request.BatchId &&
                candidate.CapacitySlotKey == request.CapacitySlotKey &&
                candidate.OwnerKey == request.OwnerKey &&
                candidate.AdmissionGeneration == request.AdmissionGeneration &&
                candidate.State == (int)GpuBatchState.CapacityUncertain,
            cancellationToken).ConfigureAwait(false);
        if (batch is null)
        {
            RecordReceipt(context, operationId, "outcome-reconciliation", request.BatchId, request.CapacitySlotKey, request.OwnerKey, request.AdmissionGeneration, false, false, 0, requestFingerprint);
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new GpuTrustedReconciliationResult(false);
        }

        var activeTasks = await context.GpuMiniTasks.Where(task =>
                task.BatchId == request.BatchId &&
                task.AdmissionGeneration == request.AdmissionGeneration &&
                task.ExecutionState == (int)GpuMiniTaskExecutionState.Active)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        if (activeTasks.Count != request.MiniTaskIds.Count ||
            !activeTasks.Select(task => task.Id).ToHashSet().SetEquals(request.MiniTaskIds))
        {
            RecordReceipt(context, operationId, "outcome-reconciliation", request.BatchId, request.CapacitySlotKey, request.OwnerKey, request.AdmissionGeneration, false, false, 0, requestFingerprint);
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new GpuTrustedReconciliationResult(false);
        }

        foreach (var task in activeTasks)
        {
            task.ExecutionState = (int)GpuMiniTaskExecutionState.OutcomeUncertain;
        }

        await RecordWakeAsync(context, GpuSchedulerWakeReason.Reconciliation, _timeProvider.GetUtcNow(), cancellationToken)
            .ConfigureAwait(false);
        RecordReceipt(context, operationId, "outcome-reconciliation", request.BatchId, request.CapacitySlotKey, request.OwnerKey, request.AdmissionGeneration, true, true, (int)GpuSchedulerWakeReason.Reconciliation, requestFingerprint);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        if (_afterLifecycleCommitted is not null)
        {
            await _afterLifecycleCommitted(cancellationToken).ConfigureAwait(false);
        }
        return new GpuTrustedReconciliationResult(true);
    }

    public async ValueTask<IReadOnlyList<GpuCapacityUncertaintyRequest>> ReadStaleCapacityReservationsAsync(
        DateTimeOffset heartbeatNotAfterUtc,
        CancellationToken cancellationToken)
    {
        await using var context = await _contextFactory
            .CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);
        var activeBatchStates = new[] { (int)GpuBatchState.Active, (int)GpuBatchState.AtSafeBoundary };

        var staleReservations = await context.GpuCapacitySlots.AsNoTracking()
            .Where(slot =>
                slot.State == (int)GpuCapacitySlotState.Reserved &&
                slot.ActiveBatchId != null &&
                slot.OwnerKey != null &&
                slot.LastHeartbeatAtUtc != null &&
                slot.LastHeartbeatAtUtc <= heartbeatNotAfterUtc)
            .Join(
                context.GpuBatches.AsNoTracking(),
                slot => slot.ActiveBatchId,
                batch => batch.Id,
                (slot, batch) => new { Slot = slot, Batch = batch })
            .Where(candidate =>
                candidate.Batch.CapacitySlotKey == candidate.Slot.SlotKey &&
                candidate.Batch.OwnerKey == candidate.Slot.OwnerKey &&
                activeBatchStates.Contains(candidate.Batch.State))
            .OrderBy(candidate => candidate.Slot.LastHeartbeatAtUtc)
            .ThenBy(candidate => candidate.Slot.SlotKey)
            .ThenBy(candidate => candidate.Batch.Id)
            .Select(candidate => new GpuCapacityUncertaintyRequest(
                candidate.Batch.Id,
                candidate.Slot.SlotKey,
                candidate.Slot.OwnerKey!,
                candidate.Batch.AdmissionGeneration,
                candidate.Slot.LastHeartbeatAtUtc!.Value,
                candidate.Slot.RowVersion))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return staleReservations
            .Select(request => request with
            {
                ObservedSlotRowVersion = request.ObservedSlotRowVersion.ToArray()
            })
            .ToArray();
    }

    public async ValueTask<GpuSchedulerWakeSnapshot> ReadWakeStateAsync(
        CancellationToken cancellationToken)
    {
        await using var context = await _contextFactory
            .CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);
        var state = await context.GpuSchedulerStates.AsNoTracking()
            .SingleAsync(candidate => candidate.Id == 1, cancellationToken)
            .ConfigureAwait(false);
        return WakeSnapshotFromState(state);
    }

    public async ValueTask<GpuSchedulerWakeConsumption> ConsumeWakeAsync(
        Guid operationId,
        long expectedGeneration,
        CancellationToken cancellationToken)
    {
        RequireLifecycleOperationId(operationId);
        if (expectedGeneration < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(expectedGeneration));
        }

        var requestFingerprint = CreateWakeConsumptionRequestFingerprint(expectedGeneration);
        await using var executionContext = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var strategy = executionContext.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(() => ConsumeWakeWithinTransactionAsync(
            expectedGeneration,
            operationId,
            requestFingerprint,
            cancellationToken)).ConfigureAwait(false);
    }

    private async Task<GpuSchedulerWakeConsumption> ConsumeWakeWithinTransactionAsync(
        long expectedGeneration,
        Guid operationId,
        string requestFingerprint,
        CancellationToken cancellationToken)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await context.Database
            .BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken)
            .ConfigureAwait(false);
        var receipt = await context.GpuSchedulerOperationReceipts
            .SingleOrDefaultAsync(candidate => candidate.OperationId == operationId, cancellationToken)
            .ConfigureAwait(false);
        if (receipt is not null)
        {
            ValidateReceiptForRequest(receipt, "wake-consumption", requestFingerprint);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return WakeConsumptionFromReceipt(receipt);
        }

        var state = await context.GpuSchedulerStates
            .SingleAsync(candidate => candidate.Id == 1, cancellationToken)
            .ConfigureAwait(false);
        if (state.InFlightWakeOperationId is not null)
        {
            var inFlightSnapshot = WakeSnapshotFromState(state);
            RecordReceipt(
                context,
                operationId,
                "wake-consumption",
                null,
                null,
                null,
                null,
                true,
                true,
                (int)inFlightSnapshot.Reasons,
                requestFingerprint,
                wakeGeneration: inFlightSnapshot.Generation,
                nextDeferredAtUtc: inFlightSnapshot.NextDeferredAtUtc,
                wakeConsumptionOperationId: inFlightSnapshot.ConsumptionOperationId,
                effectiveAdmissionReasons: (int?)inFlightSnapshot.EffectiveAdmissionReasons);
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new GpuSchedulerWakeConsumption(true, inFlightSnapshot);
        }

        var snapshot = WakeSnapshotFromState(state);
        var consumed = state.WakeGeneration == expectedGeneration;
        if (consumed)
        {
            var effectiveAdmissionReasons = AdmissionReasonsForWakeAt(
                snapshot,
                _timeProvider.GetUtcNow());
            state.InFlightWakeOperationId = operationId;
            state.InFlightWakeGeneration = snapshot.Generation;
            state.InFlightWakeReasons = (int)snapshot.Reasons;
            state.InFlightNextDeferredAtUtc = snapshot.NextDeferredAtUtc;
            state.InFlightEffectiveAdmissionReasons = (int)effectiveAdmissionReasons;
            state.PendingWakeReasons = 0;
            state.NextDeferredAtUtc = null;
            state.UpdatedAtUtc = _timeProvider.GetUtcNow();
            snapshot = snapshot with
            {
                ConsumptionOperationId = operationId,
                EffectiveAdmissionReasons = effectiveAdmissionReasons
            };
        }

        RecordReceipt(
            context,
            operationId,
            "wake-consumption",
            null,
            null,
            null,
            null,
            consumed,
            true,
            (int)snapshot.Reasons,
            requestFingerprint,
            wakeGeneration: snapshot.Generation,
            nextDeferredAtUtc: snapshot.NextDeferredAtUtc,
            wakeConsumptionOperationId: snapshot.ConsumptionOperationId,
            effectiveAdmissionReasons: (int?)snapshot.EffectiveAdmissionReasons);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        if (_afterWakeConsumptionCommitted is not null)
        {
            await _afterWakeConsumptionCommitted(cancellationToken).ConfigureAwait(false);
        }

        return new GpuSchedulerWakeConsumption(consumed, snapshot);
    }

    public async ValueTask<bool> AcknowledgeWakeAsync(
        Guid operationId,
        Guid consumptionOperationId,
        CancellationToken cancellationToken)
    {
        RequireLifecycleOperationId(operationId);
        if (consumptionOperationId == Guid.Empty)
        {
            throw new ArgumentException("A consumed GPU scheduler wake operation ID is required.", nameof(consumptionOperationId));
        }

        await using var executionContext = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var strategy = executionContext.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(() => AcknowledgeWakeWithinTransactionAsync(
            operationId, consumptionOperationId, cancellationToken)).ConfigureAwait(false);
    }

    private async Task<bool> AcknowledgeWakeWithinTransactionAsync(
        Guid operationId,
        Guid consumptionOperationId,
        CancellationToken cancellationToken)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false);
        var receipt = await context.GpuSchedulerOperationReceipts.SingleOrDefaultAsync(
            candidate => candidate.OperationId == operationId,
            cancellationToken).ConfigureAwait(false);
        if (receipt is not null)
        {
            ValidateReceiptForRequest(receipt, "wake-acknowledgement", CreateWakeAcknowledgementRequestFingerprint(consumptionOperationId));
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return receipt.Accepted;
        }

        var state = await context.GpuSchedulerStates.SingleAsync(candidate => candidate.Id == 1, cancellationToken).ConfigureAwait(false);
        var acknowledged = state.InFlightWakeOperationId == consumptionOperationId;
        if (acknowledged)
        {
            state.InFlightWakeOperationId = null;
            state.InFlightWakeGeneration = null;
            state.InFlightWakeReasons = 0;
            state.InFlightNextDeferredAtUtc = null;
            state.InFlightEffectiveAdmissionReasons = null;
            state.UpdatedAtUtc = _timeProvider.GetUtcNow();
        }

        RecordReceipt(
            context, operationId, "wake-acknowledgement", null, null, null, null,
            acknowledged, true, 0, CreateWakeAcknowledgementRequestFingerprint(consumptionOperationId));
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return acknowledged;
    }

    public async ValueTask<GpuSchedulerStatusSnapshot> ReadGpuSchedulerStatusAsync(
        CancellationToken cancellationToken)
    {
        await using var context = await _contextFactory
            .CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);
        var tasks = context.GpuMiniTasks.AsNoTracking();
        var readyState = (int)GpuMiniTaskExecutionState.Ready;
        var activeState = (int)GpuMiniTaskExecutionState.Active;
        var uncertainState = (int)GpuMiniTaskExecutionState.OutcomeUncertain;
        var completedState = (int)GpuMiniTaskExecutionState.Completed;
        var activeBatchStates = new[] { (int)GpuBatchState.Active, (int)GpuBatchState.AtSafeBoundary };
        var activeBatch = await context.GpuBatches.AsNoTracking()
            .Where(batch => activeBatchStates.Contains(batch.State))
            .OrderBy(batch => batch.CreatedAtUtc)
            .Select(batch => new { batch.PriorityLane })
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        var laneCounts = await tasks
            .Where(task => task.ExecutionState != completedState)
            .GroupBy(task => task.PriorityLane)
            .ToDictionaryAsync(group => (GpuPriorityLane)group.Key, group => group.Count(), cancellationToken)
            .ConfigureAwait(false);
        var oldestUncertainHeartbeat = await context.GpuCapacitySlots.AsNoTracking()
            .Where(slot => slot.State == (int)GpuCapacitySlotState.Uncertain && slot.LastHeartbeatAtUtc != null)
            .Select(slot => slot.LastHeartbeatAtUtc)
            .MinAsync(cancellationToken)
            .ConfigureAwait(false);
        var now = _timeProvider.GetUtcNow();
        TimeSpan? uncertainAge = oldestUncertainHeartbeat is not { } heartbeat
            ? null
            : now > heartbeat
                ? now - heartbeat
                : TimeSpan.Zero;

        return new GpuSchedulerStatusSnapshot(
            await tasks.CountAsync(task => task.ExecutionState == readyState, cancellationToken).ConfigureAwait(false),
            await tasks.CountAsync(task => task.ExecutionState == activeState, cancellationToken).ConfigureAwait(false),
            await tasks.CountAsync(
                    task => task.ExecutionState == readyState && task.DeferredUntilUtc != null,
                    cancellationToken)
                .ConfigureAwait(false),
            await tasks.CountAsync(task => task.ExecutionState == uncertainState, cancellationToken).ConfigureAwait(false),
            laneCounts,
            activeBatch is not null,
            activeBatch is null ? null : (GpuPriorityLane)activeBatch.PriorityLane,
            await context.GpuCapacitySlots.CountAsync(
                    slot => slot.State == (int)GpuCapacitySlotState.Available,
                    cancellationToken)
                .ConfigureAwait(false),
            await context.GpuCapacitySlots.CountAsync(
                    slot => slot.State == (int)GpuCapacitySlotState.Reserved,
                    cancellationToken)
                .ConfigureAwait(false),
            await context.GpuCapacitySlots.CountAsync(
                    slot => slot.State == (int)GpuCapacitySlotState.Uncertain,
                    cancellationToken)
                .ConfigureAwait(false),
            await context.GpuSchedulerStates.AsNoTracking()
                .Where(state => state.Id == 1)
                .Select(state => state.NextDeferredAtUtc)
                .SingleAsync(cancellationToken)
                .ConfigureAwait(false),
            uncertainAge);
    }

    private async Task<GpuMiniTaskHandoffResult> HandoffWithinTransactionAsync(
        FluxKnowledgeDbContext context,
        GpuMiniTaskHandoffRequest request,
        CancellationToken cancellationToken)
    {
        await using var transaction = await context.Database
            .BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken)
            .ConfigureAwait(false);

        if (_beforeIdempotencyRead is not null)
        {
            await _beforeIdempotencyRead(cancellationToken).ConfigureAwait(false);
        }

        var existing = await context.GpuMiniTasks.SingleOrDefaultAsync(
                task => task.IdempotencyKey == request.IdempotencyKey,
                cancellationToken)
            .ConfigureAwait(false);
        if (existing is not null)
        {
            await ValidateIdempotentReplayAsync(context, existing, request, cancellationToken)
                .ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new GpuMiniTaskHandoffResult(existing.Id, IsIdempotentReplay: true, Committed: true);
        }

        var parent = request.ParentJob;
        var claimed = await context.Jobs.AsNoTracking().AnyAsync(
                job =>
                    job.Id == parent.JobId.Value &&
                    job.PipelineRecordId == parent.PipelineRecordId.Value &&
                    job.SourceRevision == parent.SourceRevision &&
                    job.Stage == (int)parent.Stage &&
                    job.Operation == parent.Operation &&
                    job.PublicState == (int)PublicJobState.WorkerProcessing &&
                    job.LeaseOwner == parent.LeaseOwner &&
                    job.LeaseGeneration == parent.LeaseGeneration,
                cancellationToken)
            .ConfigureAwait(false);
        if (!claimed)
        {
            throw new InvalidOperationException("The claimed parent Job lease does not match durable worker processing work.");
        }

        var miniTask = new GpuMiniTaskEntity
        {
            Id = Guid.NewGuid(),
            ParentJobId = parent.JobId.Value,
            SourceRevision = parent.SourceRevision,
            PriorityLane = (int)request.PriorityLane,
            ModelRuntimeKey = request.ModelRuntimeKey,
            SettingsFingerprint = request.SettingsFingerprint,
            EstimatedBytes = request.EstimatedBytes,
            AdmissionGeneration = 0,
            IdempotencyKey = request.IdempotencyKey,
            HandoffLeaseOwner = parent.LeaseOwner,
            ExecutionState = (int)GpuMiniTaskExecutionState.Ready,
            CreatedAtUtc = _timeProvider.GetUtcNow()
        };
        context.GpuMiniTasks.Add(miniTask);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        if (_afterMiniTaskPersisted is not null)
        {
            await _afterMiniTaskPersisted(cancellationToken).ConfigureAwait(false);
        }

        var transitioned = await context.Database.ExecuteSqlInterpolatedAsync(
                $"""
                 UPDATE [Jobs]
                 SET [PublicState] = {(int)PublicJobState.GpuQueued},
                     [LeaseOwner] = NULL,
                     [LeaseExpiresAtUtc] = NULL,
                     [Reason] = NULL,
                     [ErrorDetails] = NULL
                 WHERE [Id] = {parent.JobId.Value}
                   AND [PipelineRecordId] = {parent.PipelineRecordId.Value}
                   AND [SourceRevision] = {parent.SourceRevision}
                   AND [Stage] = {(int)parent.Stage}
                   AND [Operation] = {parent.Operation}
                   AND [PublicState] = {(int)PublicJobState.WorkerProcessing}
                   AND [LeaseOwner] = {parent.LeaseOwner}
                   AND [LeaseGeneration] = {parent.LeaseGeneration};
                 """,
                cancellationToken)
            .ConfigureAwait(false);
        if (transitioned != 1)
        {
            throw new InvalidOperationException("The parent Job lease was lost before GPU hand-off completed.");
        }

        var schedulerState = await context.GpuSchedulerStates.SingleAsync(
                state => state.Id == 1,
                cancellationToken)
            .ConfigureAwait(false);
        schedulerState.WakeGeneration++;
        schedulerState.PendingWakeReasons |= (int)GpuSchedulerWakeReason.WorkReady;
        schedulerState.UpdatedAtUtc = _timeProvider.GetUtcNow();
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new GpuMiniTaskHandoffResult(miniTask.Id, IsIdempotentReplay: false, Committed: true);
    }

    private static void ValidateHandoffRequest(GpuMiniTaskHandoffRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.ParentJob);
        if (request.ParentJob.PublicState != PublicJobState.WorkerProcessing)
        {
            throw new ArgumentException("GPU hand-off requires a worker-processing parent Job.", nameof(request));
        }

        if (!Enum.IsDefined(request.PriorityLane))
        {
            throw new ArgumentOutOfRangeException(nameof(request), "GPU priority lane is invalid.");
        }

        GpuSchedulerOpaqueKeyValidator.RequireCanonical(request.ParentJob.Operation, nameof(request.ParentJob.Operation));
        GpuSchedulerOpaqueKeyValidator.RequireCanonical(request.ModelRuntimeKey, nameof(request.ModelRuntimeKey));
        GpuSchedulerOpaqueKeyValidator.RequireCanonical(request.SettingsFingerprint, nameof(request.SettingsFingerprint));
        GpuSchedulerOpaqueKeyValidator.RequireCanonical(request.IdempotencyKey, nameof(request.IdempotencyKey));
        GpuSchedulerOpaqueKeyValidator.RequireCanonical(request.ParentJob.LeaseOwner, nameof(request.ParentJob.LeaseOwner));
        if (request.EstimatedBytes <= 0 || request.ParentJob.SourceRevision <= 0 ||
            request.ParentJob.LeaseGeneration <= 0)
        {
            throw new ArgumentException("GPU hand-off requires a valid claimed parent Job and memory estimate.", nameof(request));
        }
    }

    private async Task<List<GpuMiniTaskEntity>> SelectBatchAsync(
        FluxKnowledgeDbContext context,
        GpuSchedulerWakeReason wakeReason,
        GpuSchedulerOptions options,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var readyState = (int)GpuMiniTaskExecutionState.Ready;
        var includeFutureDeferrals = wakeReason.HasFlag(GpuSchedulerWakeReason.CapacityReleased);
        var eligibility = context.GpuMiniTasks.Where(task => task.ExecutionState == readyState &&
            (includeFutureDeferrals || task.DeferredUntilUtc == null || task.DeferredUntilUtc <= now));

        GpuMiniTaskEntity? head = null;
        foreach (var lane in Enum.GetValues<GpuPriorityLane>())
        {
            head = await eligibility
                .Where(task => task.PriorityLane == (int)lane)
                .OrderBy(task => task.CreatedSequence)
                .ThenBy(task => task.Id)
                .FirstOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);
            if (head is not null)
            {
                break;
            }
        }

        if (head is null)
        {
            return [];
        }

        if (head.EstimatedBytes > options.MaxBatchEstimatedBytes)
        {
            throw new InvalidOperationException("The strict-priority GPU head exceeds the configured batch byte limit.");
        }

        var orderedPrefix = await eligibility
            .Where(task => task.PriorityLane == head.PriorityLane)
            .Include(task => task.ParentJob)
            .OrderBy(task => task.CreatedSequence)
            .ThenBy(task => task.Id)
            .Take(options.MaxBatchItems)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var selected = new List<GpuMiniTaskEntity>();
        long estimatedBytes = 0;
        foreach (var task in orderedPrefix)
        {
            if (task.PriorityLane != head.PriorityLane ||
                !string.Equals(task.ModelRuntimeKey, head.ModelRuntimeKey, StringComparison.Ordinal) ||
                !string.Equals(task.SettingsFingerprint, head.SettingsFingerprint, StringComparison.Ordinal) ||
                selected.Count == options.MaxBatchItems ||
                task.EstimatedBytes > options.MaxBatchEstimatedBytes - estimatedBytes)
            {
                break;
            }

            selected.Add(task);
            estimatedBytes += task.EstimatedBytes;
        }

        return selected;
    }

    private async Task<GpuSchedulerAdmissionRoundResult> CommitBusyAsync(
        FluxKnowledgeDbContext context,
        IDbContextTransaction transaction,
        IReadOnlyList<GpuMiniTaskEntity> selected,
        DateTimeOffset now,
        GpuSchedulerWakeReason wakeReason,
        GpuSchedulerOptions options,
        Guid operationId,
        Guid batchId,
        CancellationToken cancellationToken)
    {
        var clearsObsoleteDeferral = wakeReason.HasFlag(GpuSchedulerWakeReason.CapacityReleased);
        var clearedDeferral = false;
        foreach (var task in selected.Where(task =>
                     task.DeferredUntilUtc is not null &&
                     (clearsObsoleteDeferral || task.DeferredUntilUtc <= now)))
        {
            task.DeferredUntilUtc = null;
            clearedDeferral = true;
        }

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        var changedNextDeferred = await RecomputeNextDeferredAsync(context, now, cancellationToken).ConfigureAwait(false);
        var result = new GpuSchedulerAdmissionRoundResult(
            clearedDeferral || changedNextDeferred,
            GpuAdmissionDisposition.Busy,
            null);
        RecordAdmissionReceipt(context, operationId, batchId, wakeReason, options, result);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        if (_afterAdmissionCommitted is not null)
        {
            await _afterAdmissionCommitted(cancellationToken).ConfigureAwait(false);
        }

        return result;
    }

    private async Task<GpuSchedulerAdmissionRoundResult> CommitDeferralAsync(
        FluxKnowledgeDbContext context,
        IDbContextTransaction transaction,
        IReadOnlyList<GpuMiniTaskEntity> selected,
        DateTimeOffset now,
        TimeSpan retryAfter,
        GpuSchedulerWakeReason wakeReason,
        GpuSchedulerOptions options,
        Guid operationId,
        Guid batchId,
        CancellationToken cancellationToken)
    {
        var deferredUntilUtc = now.Add(retryAfter);
        foreach (var task in selected)
        {
            var deferred = await context.Database.ExecuteSqlInterpolatedAsync(
                    $"""
                     UPDATE [GpuMiniTasks]
                     SET [ReservationAttemptCount] = {task.ReservationAttemptCount + 1},
                         [DeferredUntilUtc] = {deferredUntilUtc}
                     WHERE [Id] = {task.Id}
                       AND [State] = {(int)GpuMiniTaskExecutionState.Ready}
                       AND [RowVersion] = {task.RowVersion};
                     """,
                    cancellationToken)
                .ConfigureAwait(false);
            if (deferred != 1)
            {
                throw new InvalidOperationException("A selected GPU mini-task changed before its deferral could be committed.");
            }
        }

        await RecomputeNextDeferredAsync(context, now, cancellationToken).ConfigureAwait(false);
        await RecordWakeAsync(context, GpuSchedulerWakeReason.DeferredRetry, now, cancellationToken)
            .ConfigureAwait(false);
        var result = new GpuSchedulerAdmissionRoundResult(
            true,
            GpuAdmissionDisposition.Defer,
            deferredUntilUtc);
        RecordAdmissionReceipt(context, operationId, batchId, wakeReason, options, result);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        if (_afterAdmissionCommitted is not null)
        {
            await _afterAdmissionCommitted(cancellationToken).ConfigureAwait(false);
        }

        return result;
    }

    private async Task<GpuSchedulerAdmissionRoundResult> CommitAdmissionAsync(
        FluxKnowledgeDbContext context,
        IDbContextTransaction transaction,
        IReadOnlyList<GpuMiniTaskEntity> selected,
        DateTimeOffset now,
        GpuAdmissionDecision decision,
        GpuSchedulerWakeReason wakeReason,
        GpuSchedulerOptions options,
        Guid operationId,
        Guid batchId,
        CancellationToken cancellationToken)
    {
        var slot = await context.GpuCapacitySlots.SingleOrDefaultAsync(
                candidate => candidate.SlotKey == decision.CapacitySlotKey,
                cancellationToken)
            .ConfigureAwait(false);
        if (slot is null || slot.State != (int)GpuCapacitySlotState.Available || slot.ActiveBatchId is not null)
        {
            throw new InvalidOperationException("The admission decision named a capacity slot that is not available.");
        }

        var activeBatchStates = new[] { (int)GpuBatchState.Active, (int)GpuBatchState.AtSafeBoundary };
        if (await context.GpuBatches.AnyAsync(
                batch => batch.CapacitySlotKey == slot.SlotKey && activeBatchStates.Contains(batch.State),
                cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("The admission decision named a capacity slot with an active batch.");
        }

        var generations = selected.Select(task => task.AdmissionGeneration).Distinct().ToList();
        if (generations.Count != 1)
        {
            throw new InvalidOperationException("Selected GPU mini-tasks do not share an admission generation.");
        }

        var batch = new GpuBatchEntity
        {
            Id = batchId,
            CapacitySlotKey = slot.SlotKey,
            PriorityLane = selected[0].PriorityLane,
            ModelRuntimeKey = selected[0].ModelRuntimeKey,
            SettingsFingerprint = selected[0].SettingsFingerprint,
            ItemCount = selected.Count,
            EstimatedBytes = selected.Sum(task => task.EstimatedBytes),
            AdmissionGeneration = checked(generations[0] + 1),
            OwnerKey = decision.OwnerKey!,
            State = (int)GpuBatchState.Active,
            LastHeartbeatAtUtc = now,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };
        context.GpuBatches.Add(batch);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        var reserved = await context.Database.ExecuteSqlInterpolatedAsync(
                $"""
                 UPDATE [GpuCapacitySlots]
                 SET [State] = {(int)GpuCapacitySlotState.Reserved},
                     [ActiveBatchId] = {batch.Id},
                     [OwnerKey] = {decision.OwnerKey},
                     [LastHeartbeatAtUtc] = {now},
                     [UpdatedAtUtc] = {now}
                 WHERE [SlotKey] = {slot.SlotKey}
                   AND [State] = {(int)GpuCapacitySlotState.Available}
                   AND [ActiveBatchId] IS NULL
                   AND [RowVersion] = {slot.RowVersion};
                 """,
                cancellationToken)
            .ConfigureAwait(false);
        if (reserved != 1)
        {
            throw new InvalidOperationException("The GPU capacity slot changed before its reservation could be committed.");
        }

        foreach (var task in selected)
        {
            if (task.ParentJob.PublicState != (int)PublicJobState.GpuQueued)
            {
                throw new InvalidOperationException("A selected GPU mini-task parent Job is not GPU queued.");
            }

            var activated = await context.Database.ExecuteSqlInterpolatedAsync(
                    $"""
                     UPDATE [GpuMiniTasks]
                     SET [State] = {(int)GpuMiniTaskExecutionState.Active},
                         [BatchId] = {batch.Id},
                         [AdmissionGeneration] = {batch.AdmissionGeneration},
                         [DeferredUntilUtc] = NULL
                     WHERE [Id] = {task.Id}
                       AND [State] = {(int)GpuMiniTaskExecutionState.Ready}
                       AND [BatchId] IS NULL
                       AND [RowVersion] = {task.RowVersion};
                     """,
                    cancellationToken)
                .ConfigureAwait(false);
            if (activated != 1)
            {
                throw new InvalidOperationException("A selected GPU mini-task changed before admission could be committed.");
            }
        }

        foreach (var parent in selected.Select(task => task.ParentJob).DistinctBy(job => job.Id))
        {
            var transitioned = await context.Database.ExecuteSqlInterpolatedAsync(
                    $"""
                     UPDATE [Jobs]
                     SET [PublicState] = {(int)PublicJobState.GpuProcessing}
                     WHERE [Id] = {parent.Id}
                       AND [PublicState] = {(int)PublicJobState.GpuQueued}
                       AND [RowVersion] = {parent.RowVersion};
                     """,
                    cancellationToken)
                .ConfigureAwait(false);
            if (transitioned != 1)
            {
                throw new InvalidOperationException("A selected GPU mini-task parent Job changed before admission could be committed.");
            }
        }

        await RecomputeNextDeferredAsync(context, now, cancellationToken).ConfigureAwait(false);
        var result = new GpuSchedulerAdmissionRoundResult(true, GpuAdmissionDisposition.Admit, null);
        RecordAdmissionReceipt(context, operationId, batchId, wakeReason, options, result);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        if (_afterAdmissionCommitted is not null)
        {
            await _afterAdmissionCommitted(cancellationToken).ConfigureAwait(false);
        }

        return result;
    }

    private static async Task AcquireAdmissionLockAsync(
        FluxKnowledgeDbContext context,
        DbTransaction transaction,
        CancellationToken cancellationToken)
    {
        await context.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = context.Database.GetDbConnection().CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "DECLARE @result int; EXEC @result = sp_getapplock @Resource = N'FluxKnowledge.GpuScheduler.Admission', @LockMode = N'Exclusive', @LockOwner = N'Transaction', @LockTimeout = 10000; SELECT @result;";
        var result = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
        if (result < 0)
        {
            throw new InvalidOperationException("Could not acquire the GPU scheduler admission fence. Check SQL Server locking and permissions.");
        }
    }

    private const GpuSchedulerWakeReason KnownWakeReasons =
        GpuSchedulerWakeReason.WorkReady |
        GpuSchedulerWakeReason.SafeBoundary |
        GpuSchedulerWakeReason.CapacityReleased |
        GpuSchedulerWakeReason.DeferredRetry |
        GpuSchedulerWakeReason.StartupRecovery |
        GpuSchedulerWakeReason.Reconciliation;

    private static GpuSchedulerWakeReason AdmissionReasonsForWakeAt(
        GpuSchedulerWakeSnapshot wake,
        DateTimeOffset now)
    {
        var reasons = wake.Reasons;
        if (wake.NextDeferredAtUtc is { } deferred && deferred > now)
        {
            reasons &= ~GpuSchedulerWakeReason.DeferredRetry;
        }

        return reasons;
    }

    public const string TrustedCapacityReleaseEvidenceClass = "verified-process-termination-and-driver-absence";
    public const string TrustedOutcomeUncertainEvidenceClass = "verified-unresolved-task-outcome";

    private static void RequireLifecycleOperationId(Guid operationId)
    {
        if (operationId == Guid.Empty)
        {
            throw new ArgumentException("A lifecycle operation ID is required.", nameof(operationId));
        }
    }

    private static string CreateAdmissionRequestFingerprint(
        GpuSchedulerWakeReason wakeReason,
        GpuSchedulerOptions options) =>
        CreateRequestFingerprint(
            "admission",
            ((int)wakeReason).ToString(CultureInfo.InvariantCulture),
            options.MaxBatchItems.ToString(CultureInfo.InvariantCulture),
            options.MaxBatchEstimatedBytes.ToString(CultureInfo.InvariantCulture),
            options.CapacityDeferralCap.Ticks.ToString(CultureInfo.InvariantCulture),
            options.FallbackInterval.Ticks.ToString(CultureInfo.InvariantCulture),
            options.UnresponsiveDiagnosticAge.Ticks.ToString(CultureInfo.InvariantCulture));

    private static string CreateCallbackRequestFingerprint(GpuBatchCallback callback)
    {
        var fields = new List<string>
        {
            "callback",
            callback.BatchId.ToString("N"),
            callback.CapacitySlotKey,
            callback.OwnerKey,
            callback.AdmissionGeneration.ToString(CultureInfo.InvariantCulture),
            ((int)callback.Kind).ToString(CultureInfo.InvariantCulture),
            callback.CapacityReleased ? "1" : "0",
            callback.Outcomes.Count.ToString(CultureInfo.InvariantCulture)
        };
        fields.AddRange(callback.Outcomes
            .OrderBy(outcome => outcome.MiniTaskId)
            .ThenBy(outcome => (int)outcome.Disposition)
            .Select(outcome => string.Concat(
                outcome.MiniTaskId.ToString("N"),
                ":",
                ((int)outcome.Disposition).ToString(CultureInfo.InvariantCulture))));
        return CreateRequestFingerprint(fields);
    }

    private static string CreateCapacityUncertaintyRequestFingerprint(GpuCapacityUncertaintyRequest request) =>
        CreateRequestFingerprint(
            "uncertain",
            request.BatchId.ToString("N"),
            request.CapacitySlotKey,
            request.OwnerKey,
            request.AdmissionGeneration.ToString(CultureInfo.InvariantCulture),
            request.ObservedLastHeartbeatAtUtc.UtcDateTime.Ticks.ToString(CultureInfo.InvariantCulture),
            Convert.ToHexString(request.ObservedSlotRowVersion));

    private static string CreateCapacityReconciliationRequestFingerprint(GpuTrustedCapacityReconciliation request) =>
        CreateRequestFingerprint(
            "capacity-reconciliation",
            request.BatchId.ToString("N"),
            request.CapacitySlotKey,
            request.OwnerKey,
            request.AdmissionGeneration.ToString(CultureInfo.InvariantCulture),
            request.EvidenceClass);

    private static string CreateTaskOutcomeReconciliationRequestFingerprint(
        GpuTaskOutcomeReconciliation request)
    {
        var fields = new List<string>
        {
            "outcome-reconciliation",
            request.BatchId.ToString("N"),
            request.CapacitySlotKey,
            request.OwnerKey,
            request.AdmissionGeneration.ToString(CultureInfo.InvariantCulture),
            request.EvidenceClass,
            request.MiniTaskIds.Count.ToString(CultureInfo.InvariantCulture)
        };
        fields.AddRange(request.MiniTaskIds.OrderBy(id => id).Select(id => id.ToString("N")));
        return CreateRequestFingerprint(fields);
    }

    private static string CreateWakeConsumptionRequestFingerprint(long expectedGeneration) =>
        CreateRequestFingerprint(
            "wake-consumption",
            expectedGeneration.ToString(CultureInfo.InvariantCulture));

    private static string CreateWakeAcknowledgementRequestFingerprint(Guid consumptionOperationId) =>
        CreateRequestFingerprint(
            "wake-acknowledgement",
            consumptionOperationId.ToString("N"));

    private static string CreateRequestFingerprint(params string[] fields) =>
        CreateRequestFingerprint((IEnumerable<string>)fields);

    private static string CreateRequestFingerprint(IEnumerable<string> fields)
    {
        var canonical = new StringBuilder();
        foreach (var field in fields)
        {
            ArgumentNullException.ThrowIfNull(field);
            canonical
                .Append(field.Length.ToString(CultureInfo.InvariantCulture))
                .Append(':')
                .Append(field)
                .Append('|');
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())));
    }

    private static void ValidateReceiptForRequest(
        GpuSchedulerOperationReceiptEntity receipt,
        string operationKind,
        string requestFingerprint)
    {
        if (!string.Equals(receipt.OperationKind, operationKind, StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(receipt.RequestFingerprint) ||
            !string.Equals(receipt.RequestFingerprint, requestFingerprint, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The private GPU operation receipt does not match the immutable request.");
        }
    }

    private static bool IsCallbackStateEligible(GpuBatchEntity batch, GpuBatchCallbackKind kind) =>
        kind switch
        {
            GpuBatchCallbackKind.SafeBoundary => batch.State == (int)GpuBatchState.Active,
            GpuBatchCallbackKind.Completed or GpuBatchCallbackKind.CapacityReleased =>
                batch.State == (int)GpuBatchState.Active || batch.State == (int)GpuBatchState.AtSafeBoundary,
            _ => false
        };

    private static bool CallbackOutcomesMatch(
        GpuBatchCallback callback,
        IReadOnlyCollection<GpuMiniTaskEntity> activeTasks)
    {
        if (callback.Kind == GpuBatchCallbackKind.SafeBoundary && !callback.CapacityReleased)
        {
            return callback.Outcomes.Count == 0;
        }

        if (activeTasks.Count == 0 || callback.Outcomes.Count != activeTasks.Count)
        {
            return false;
        }

        var activeIds = activeTasks.Select(task => task.Id).ToHashSet();
        return callback.Outcomes.All(outcome => activeIds.Contains(outcome.MiniTaskId));
    }

    private static async Task RecordWakeAsync(
        FluxKnowledgeDbContext context,
        GpuSchedulerWakeReason reasons,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (reasons == 0)
        {
            return;
        }

        var state = await context.GpuSchedulerStates.SingleAsync(candidate => candidate.Id == 1, cancellationToken)
            .ConfigureAwait(false);
        state.WakeGeneration++;
        state.PendingWakeReasons |= (int)reasons;
        state.UpdatedAtUtc = now;
    }

    private void RecordReceipt(
        FluxKnowledgeDbContext context,
        Guid operationId,
        string operationKind,
        Guid? batchId,
        string? capacitySlotKey,
        string? ownerKey,
        long? admissionGeneration,
        bool accepted,
        bool committed,
        int wakeReasons,
        string requestFingerprint,
        int? admissionDisposition = null,
        DateTimeOffset? deferredUntilUtc = null,
        long? wakeGeneration = null,
        DateTimeOffset? nextDeferredAtUtc = null,
        Guid? wakeConsumptionOperationId = null,
        int? effectiveAdmissionReasons = null)
    {
        context.GpuSchedulerOperationReceipts.Add(new GpuSchedulerOperationReceiptEntity
        {
            OperationId = operationId,
            OperationKind = operationKind,
            RequestFingerprint = requestFingerprint,
            BatchId = batchId,
            CapacitySlotKey = capacitySlotKey,
            OwnerKey = ownerKey,
            AdmissionGeneration = admissionGeneration,
            Accepted = accepted,
            Committed = committed,
            WakeReasons = wakeReasons,
            AdmissionDisposition = admissionDisposition,
            DeferredUntilUtc = deferredUntilUtc,
            WakeGeneration = wakeGeneration,
            NextDeferredAtUtc = nextDeferredAtUtc,
            WakeConsumptionOperationId = wakeConsumptionOperationId,
            EffectiveAdmissionReasons = effectiveAdmissionReasons,
            CreatedAtUtc = _timeProvider.GetUtcNow()
        });
    }

    private void RecordAdmissionReceipt(
        FluxKnowledgeDbContext context,
        Guid operationId,
        Guid batchId,
        GpuSchedulerWakeReason wakeReason,
        GpuSchedulerOptions options,
        GpuSchedulerAdmissionRoundResult result) =>
        RecordReceipt(
            context,
            operationId,
            "admission",
            batchId,
            null,
            null,
            null,
            result.Committed,
            result.Committed,
            (int)wakeReason,
            CreateAdmissionRequestFingerprint(wakeReason, options),
            admissionDisposition: (int)result.Disposition,
            deferredUntilUtc: result.DeferredUntilUtc);

    private static GpuSchedulerAdmissionRoundResult AdmissionResultFromReceipt(
        GpuSchedulerOperationReceiptEntity receipt,
        GpuSchedulerWakeReason wakeReason,
        GpuSchedulerOptions options)
    {
        ValidateReceiptForRequest(receipt, "admission", CreateAdmissionRequestFingerprint(wakeReason, options));
        if (receipt.AdmissionDisposition is null ||
            !Enum.IsDefined((GpuAdmissionDisposition)receipt.AdmissionDisposition.Value))
        {
            throw new InvalidOperationException("The private GPU admission receipt is invalid.");
        }

        return new GpuSchedulerAdmissionRoundResult(
            receipt.Committed,
            (GpuAdmissionDisposition)receipt.AdmissionDisposition.Value,
            receipt.DeferredUntilUtc);
    }

    private static GpuSchedulerWakeConsumption WakeConsumptionFromReceipt(
        GpuSchedulerOperationReceiptEntity receipt)
    {
        if (!string.Equals(receipt.OperationKind, "wake-consumption", StringComparison.Ordinal) ||
            receipt.WakeGeneration is null ||
            (receipt.Accepted &&
             (receipt.WakeConsumptionOperationId is null || receipt.EffectiveAdmissionReasons is null)))
        {
            throw new InvalidOperationException("The private GPU wake-consumption receipt is invalid.");
        }

        return new GpuSchedulerWakeConsumption(
            receipt.Accepted,
            new GpuSchedulerWakeSnapshot(
                receipt.WakeGeneration.Value,
                (GpuSchedulerWakeReason)receipt.WakeReasons,
                receipt.NextDeferredAtUtc,
                receipt.Accepted ? receipt.WakeConsumptionOperationId : null,
                receipt.Accepted ? (GpuSchedulerWakeReason?)receipt.EffectiveAdmissionReasons : null));
    }

    private static GpuSchedulerWakeSnapshot WakeSnapshotFromState(GpuSchedulerStateEntity state)
    {
        if (state.InFlightWakeOperationId is { } consumptionOperationId)
        {
            if (state.InFlightWakeGeneration is null || state.InFlightEffectiveAdmissionReasons is null)
            {
                throw new InvalidOperationException("The private GPU scheduler in-flight wake evidence is invalid.");
            }

            return new GpuSchedulerWakeSnapshot(
                state.InFlightWakeGeneration.Value,
                (GpuSchedulerWakeReason)state.InFlightWakeReasons,
                state.InFlightNextDeferredAtUtc,
                consumptionOperationId,
                (GpuSchedulerWakeReason)state.InFlightEffectiveAdmissionReasons.Value);
        }

        return new GpuSchedulerWakeSnapshot(
            state.WakeGeneration,
            (GpuSchedulerWakeReason)state.PendingWakeReasons,
            state.NextDeferredAtUtc);
    }

    private static async Task<bool> RecomputeNextDeferredAsync(
        FluxKnowledgeDbContext context,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var next = await context.GpuMiniTasks
            .Where(task => task.ExecutionState == (int)GpuMiniTaskExecutionState.Ready &&
                           task.DeferredUntilUtc != null && task.DeferredUntilUtc > now)
            .OrderBy(task => task.DeferredUntilUtc)
            .Select(task => task.DeferredUntilUtc)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        var state = await context.GpuSchedulerStates.SingleAsync(candidate => candidate.Id == 1, cancellationToken)
            .ConfigureAwait(false);
        if (state.NextDeferredAtUtc == next)
        {
            return false;
        }

        state.NextDeferredAtUtc = next;
        return true;
    }

    private static async Task ValidateIdempotentReplayAsync(
        FluxKnowledgeDbContext context,
        GpuMiniTaskEntity existing,
        GpuMiniTaskHandoffRequest request,
        CancellationToken cancellationToken)
    {
        if (existing.ParentJobId != request.ParentJob.JobId.Value ||
            existing.SourceRevision != request.ParentJob.SourceRevision ||
            existing.PriorityLane != (int)request.PriorityLane ||
            !string.Equals(existing.ModelRuntimeKey, request.ModelRuntimeKey, StringComparison.Ordinal) ||
            !string.Equals(existing.SettingsFingerprint, request.SettingsFingerprint, StringComparison.Ordinal) ||
            existing.EstimatedBytes != request.EstimatedBytes ||
            !string.Equals(existing.HandoffLeaseOwner, request.ParentJob.LeaseOwner, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The GPU mini-task idempotency key conflicts with a different validated hand-off request.");
        }

        var parent = request.ParentJob;
        var parentIdentityMatches = await context.Jobs.AsNoTracking().AnyAsync(
                job =>
                    job.Id == parent.JobId.Value &&
                    job.PipelineRecordId == parent.PipelineRecordId.Value &&
                    job.SourceRevision == parent.SourceRevision &&
                    job.Stage == (int)parent.Stage &&
                    job.Operation == parent.Operation &&
                    job.LeaseGeneration == parent.LeaseGeneration &&
                    (job.PublicState == (int)PublicJobState.GpuQueued ||
                     job.PublicState == (int)PublicJobState.GpuProcessing),
                cancellationToken)
            .ConfigureAwait(false);
        if (!parentIdentityMatches)
        {
            throw new InvalidOperationException(
                "The GPU mini-task idempotency key does not match the durable parent Job identity.");
        }
    }
}
