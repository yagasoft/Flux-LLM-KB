using System.Security.Cryptography;
using System.Threading.Channels;
using FluxKnowledge.Application.Contracts;
using FluxKnowledge.Application.Indexing;
using FluxKnowledge.Application.Ports;
using Microsoft.Extensions.DependencyInjection;

namespace FluxKnowledge.Infrastructure.Usearch;

public sealed class DerivedIndexRecoveryCoordinator : IDerivedIndexRecoveryStatus, IDerivedIndexRecoverySignal
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly DerivedIndexFileSystem _fileSystem;
    private readonly IStatusEventPublisher? _statusPublisher;
    private readonly TimeProvider _timeProvider;
    private readonly DerivedIndexRecoveryOptions _options;
    private readonly Channel<DerivedIndexRecoveryFault> _signals = Channel.CreateBounded<DerivedIndexRecoveryFault>(1);
    private DerivedIndexRecoverySnapshot _snapshot = new(DerivedIndexRecoveryState.Starting, null, null, null, null, 0);
    private int _attempts;
    private int _episodeDetectionRecorded;

    public DerivedIndexRecoveryCoordinator(IServiceScopeFactory scopeFactory,
        DerivedIndexFileSystem fileSystem, TimeProvider timeProvider, IStatusEventPublisher? statusPublisher = null,
        DerivedIndexRecoveryOptions? options = null)
    {
        _scopeFactory = scopeFactory; _fileSystem = fileSystem; _timeProvider = timeProvider; _statusPublisher = statusPublisher;
        _options = options ?? DerivedIndexRecoveryOptions.Default;
    }

    public DerivedIndexRecoverySnapshot Snapshot => Volatile.Read(ref _snapshot);

    public void Notify(DerivedIndexRecoveryFault fault)
    {
        while (true)
        {
            var current = Snapshot;
            if (current.State is DerivedIndexRecoveryState.RetryScheduled or DerivedIndexRecoveryState.OperatorActionRequired)
            {
                return;
            }
            if (current.State == DerivedIndexRecoveryState.Recovering)
            {
                return;
            }

            var recovering = new DerivedIndexRecoverySnapshot(DerivedIndexRecoveryState.Recovering,
                fault.ActiveGenerationId ?? current.ActiveGenerationId, null, null, fault.Category, 0);
            if (!ReferenceEquals(Interlocked.CompareExchange(ref _snapshot, recovering, current), current)) continue;
            PublishSynchronously();
            _signals.Writer.TryWrite(fault);
            return;
        }
    }

    public ValueTask<DerivedIndexRecoveryFault> WaitAsync(CancellationToken cancellationToken) =>
        _signals.Reader.ReadAsync(cancellationToken);

    public async ValueTask RunOnceAsync(CancellationToken cancellationToken, bool retryDueWaited = false)
    {
        var beforeAttempt = Snapshot;
        if (beforeAttempt.State == DerivedIndexRecoveryState.OperatorActionRequired ||
            !retryDueWaited && beforeAttempt.State == DerivedIndexRecoveryState.RetryScheduled &&
            beforeAttempt.NextRetryAtUtc is { } retryAt && retryAt > _timeProvider.GetUtcNow())
        {
            return;
        }

        IDerivedIndexRecoveryStore? recoveryStore = null;
        Guid? activeId = beforeAttempt.ActiveGenerationId;
        string? replacementPath = null;
        var sqlUpdated = false;
        var recoveryEpisode = IsRecoveryEpisode(beforeAttempt);
        var detectionRecorded = Volatile.Read(ref _episodeDetectionRecorded) != 0;
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            recoveryStore = scope.ServiceProvider.GetRequiredService<IDerivedIndexRecoveryStore>();
            var builder = scope.ServiceProvider.GetRequiredService<UsearchGenerationBuilder>();
            var validator = scope.ServiceProvider.GetRequiredService<UsearchGenerationValidator>();
            var started = _timeProvider.GetUtcNow();
            if (recoveryEpisode)
            {
                await EnsureDetectionAsync(recoveryStore, activeId, beforeAttempt.FailureCategory, cancellationToken);
                detectionRecorded = true;
            }
            await using var lease = await recoveryStore.TryAcquireExclusiveLeaseAsync(TimeSpan.Zero, cancellationToken);
            if (lease is null)
            {
                if (recoveryEpisode)
                {
                    await recoveryStore.AppendAuditAsync(new("recovery_lock_contended", activeId, beforeAttempt.FailureCategory, _attempts,
                        TimeSpan.Zero, null, 0), cancellationToken);
                    if (detectionRecorded) await MarkRecoveringAsync(activeId, beforeAttempt.FailureCategory ?? DerivedIndexRecoveryFailureCategory.TransientIo, cancellationToken);
                }
                return;
            }

            var sql = await recoveryStore.ReadActiveAsync(cancellationToken);
            activeId = sql.ActiveGenerationId;
            ValidateSql(sql);
            if (!_fileSystem.AreAllReferencedGenerationPathsSafe(sql.ReferencedIndexPaths) ||
                !_fileSystem.TryCanonicalInRoot(sql.Generation!.IndexPath, out var activePath) ||
                !_fileSystem.IsIntendedGenerationPath(activePath) ||
                Directory.Exists(activePath) && !_fileSystem.IsValidDirectory(activePath))
            {
                throw new InvalidOperationException("The derived-index path configuration is invalid.");
            }

            DerivedIndexRecoveryFailureCategory? detectedCategory = null;
            if (Directory.Exists(activePath))
            {
                try
                {
                    validator.Validate(activePath, sql.Generation with { IndexPath = activePath }, sql.Membership);
                }
                catch (Exception exception) when (exception is IndexGenerationValidationException or
                    FileNotFoundException or DirectoryNotFoundException or IOException)
                {
                    detectedCategory = exception is FileNotFoundException or DirectoryNotFoundException
                        ? DerivedIndexRecoveryFailureCategory.MissingDerivedIndex
                        : DerivedIndexRecoveryFailureCategory.InvalidDerivedIndex;
                }
            }
            else
            {
                detectedCategory = DerivedIndexRecoveryFailureCategory.MissingDerivedIndex;
            }

            if (detectedCategory is null)
            {
                if (recoveryEpisode)
                {
                    await recoveryStore.AppendAuditAsync(new("recovery_attempt", activeId, null, _attempts + 1,
                        TimeSpan.Zero, null, 0), cancellationToken);
                    await recoveryStore.AppendAuditAsync(new("recovery_validation_succeeded", activeId, null, _attempts + 1,
                        _timeProvider.GetUtcNow() - started, null, 0), cancellationToken);
                    await CompleteAsync(recoveryStore, activeId, started, 0, cancellationToken);
                }
                else
                {
                    await CompleteProbeAsync(beforeAttempt, activeId, cancellationToken);
                }
                return;
            }

            if (!detectionRecorded)
            {
                await EnsureDetectionAsync(recoveryStore, activeId, detectedCategory, cancellationToken);
                detectionRecorded = true;
            }
            await MarkRecoveringAsync(activeId, detectedCategory.Value, cancellationToken);
            await recoveryStore.AppendAuditAsync(new("recovery_attempt", activeId, detectedCategory, _attempts + 1,
                TimeSpan.Zero, null, 0), cancellationToken);

            var oldPath = sql.Generation!.IndexPath;
            var replacement = await builder.BuildRecoveryCandidateAsync(sql.Generation, sql.Membership, cancellationToken);
            replacementPath = replacement.IndexPath;
            if (!await recoveryStore.TryUpdateRecoveryPathAsync(activeId!.Value, oldPath, replacement.IndexPath,
                    _timeProvider.GetUtcNow(), cancellationToken))
            {
                throw new DerivedIndexRecoveryActiveGenerationChangedException();
            }
            sqlUpdated = true;
            await recoveryStore.AppendAuditAsync(new("recovery_rebuild_succeeded", activeId, null, _attempts + 1,
                _timeProvider.GetUtcNow() - started, null, 0), cancellationToken);

            var fresh = await recoveryStore.ReadActiveAsync(cancellationToken);
            if (fresh.ActiveGenerationId != activeId || fresh.Generation is null ||
                !string.Equals(fresh.Generation.IndexPath, replacement.IndexPath, StringComparison.OrdinalIgnoreCase))
            {
                throw new DerivedIndexRecoveryActiveGenerationChangedException();
            }
            if (!_fileSystem.AreAllReferencedGenerationPathsSafe(fresh.ReferencedIndexPaths))
            {
                throw new InvalidOperationException("The derived-index path configuration is invalid.");
            }
            var cleaned = 0;
            var cleanupSucceeded = true;
            try
            {
                _fileSystem.TryQuarantine(oldPath, fresh.ReferencedIndexPaths);
                cleaned = _fileSystem.Cleanup("staging", _options.StagingRetention, _timeProvider.GetUtcNow(), fresh.ReferencedIndexPaths) +
                    _fileSystem.Cleanup("quarantine", _options.QuarantineRetention, _timeProvider.GetUtcNow(), fresh.ReferencedIndexPaths);
            }
            catch (Exception) when (sqlUpdated)
            {
                cleanupSucceeded = false;
            }
            await recoveryStore.AppendAuditAsync(new(cleanupSucceeded ? "recovery_cleanup_completed" : "recovery_cleanup_retained",
                activeId, null, _attempts + 1, _timeProvider.GetUtcNow() - started, null, cleaned), cancellationToken);
            await CompleteAsync(recoveryStore, activeId, started, cleaned, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            if (!sqlUpdated && exception is RecoveryCandidatePlacementException placement) replacementPath = placement.Path;
            if (!sqlUpdated && replacementPath is not null && recoveryStore is not null)
            {
                try { _fileSystem.TryQuarantine(replacementPath, (await recoveryStore.ReadActiveAsync(cancellationToken)).ReferencedIndexPaths); }
                catch (Exception) { }
            }
            await RecordFaultAsync(recoveryStore, exception, activeId, detectionRecorded, cancellationToken);
        }
    }

    private async ValueTask MarkRecoveringAsync(Guid? activeId, DerivedIndexRecoveryFailureCategory category, CancellationToken cancellationToken)
    {
        Volatile.Write(ref _snapshot, new(DerivedIndexRecoveryState.Recovering, activeId, null, null, category, 0));
        await PublishAsync(cancellationToken);
    }

    private async ValueTask RecordFaultAsync(IDerivedIndexRecoveryStore? recoveryStore, Exception exception, Guid? activeGenerationId,
        bool detectionRecorded, CancellationToken cancellationToken)
    {
        var category = exception switch
        {
            UnauthorizedAccessException or DerivedIndexRecoverySqlPermissionException => DerivedIndexRecoveryFailureCategory.PermissionsDenied,
            DerivedIndexRecoverySqlSchemaException => DerivedIndexRecoveryFailureCategory.SqlSchemaInvalid,
            SqlMembershipValidationException => DerivedIndexRecoveryFailureCategory.SqlMembershipInvalid,
            IndexGenerationValidationException => DerivedIndexRecoveryFailureCategory.InvalidDerivedIndex,
            InvalidOperationException => DerivedIndexRecoveryFailureCategory.ConfigurationInvalid,
            _ => DerivedIndexRecoveryFailureCategory.TransientIo
        };
        var decision = DerivedIndexRecoveryPolicy.Decide(category, ++_attempts);
        var now = _timeProvider.GetUtcNow();
        DateTimeOffset? nextRetry = decision.Delay is { } delay ? now + delay : null;
        if (recoveryStore is not null)
        {
            try
            {
                if (!detectionRecorded)
                {
                    await EnsureDetectionAsync(recoveryStore, activeGenerationId, category, cancellationToken);
                }
                await recoveryStore.AppendAuditAsync(new(decision.ShouldRetry ? "recovery_retry_scheduled" : "recovery_operator_required",
                    activeGenerationId, decision.FailureCategory, _attempts, TimeSpan.Zero, nextRetry, 0), cancellationToken);
            }
            catch (Exception) { }
        }
        Volatile.Write(ref _snapshot, new(decision.NextState, activeGenerationId, null, nextRetry,
            decision.FailureCategory, 0));
        await PublishAsync(cancellationToken);
    }

    private void ValidateSql(DerivedIndexRecoverySqlSnapshot sql)
    {
        if (sql.ActiveGenerationId is null || sql.Generation is null || sql.Generation.Id != sql.ActiveGenerationId ||
            sql.Membership.IsDefaultOrEmpty || sql.Generation.VectorCount != sql.Membership.Length ||
            sql.Membership.Any(vector => vector.Dimensions != sql.Generation.Dimensions || vector.Values.Length != vector.Dimensions * sizeof(float) ||
                !string.Equals(vector.ModelFingerprint, sql.Generation.ModelFingerprint, StringComparison.Ordinal) ||
                !string.Equals(vector.PayloadChecksum, Convert.ToHexStringLower(SHA256.HashData(vector.Values)), StringComparison.Ordinal)) ||
            !string.Equals(sql.Generation.MetadataChecksum, UsearchGenerationValidator.ComputeChecksum(sql.Generation.ModelFingerprint, sql.Generation.Dimensions, sql.Membership), StringComparison.Ordinal))
        {
            throw new SqlMembershipValidationException();
        }
    }

    private async ValueTask CompleteAsync(IDerivedIndexRecoveryStore recoveryStore, Guid? activeId, DateTimeOffset started, int cleaned, CancellationToken cancellationToken)
    {
        await recoveryStore.AppendAuditAsync(new("recovery_healthy", activeId, null, 0,
            _timeProvider.GetUtcNow() - started, null, cleaned), cancellationToken);
        Volatile.Write(ref _episodeDetectionRecorded, 0);
        _attempts = 0;
        Volatile.Write(ref _snapshot, new(DerivedIndexRecoveryState.Healthy, activeId, _timeProvider.GetUtcNow(), null, null, cleaned));
        await PublishAsync(cancellationToken);
    }

    private async ValueTask CompleteProbeAsync(DerivedIndexRecoverySnapshot expectedSnapshot, Guid? activeId,
        CancellationToken cancellationToken)
    {
        var completed = new DerivedIndexRecoverySnapshot(DerivedIndexRecoveryState.Healthy, activeId,
            _timeProvider.GetUtcNow(), null, null, 0);
        if (!ReferenceEquals(Interlocked.CompareExchange(ref _snapshot, completed, expectedSnapshot), expectedSnapshot))
        {
            return;
        }
        Volatile.Write(ref _episodeDetectionRecorded, 0);
        _attempts = 0;
        await PublishAsync(cancellationToken);
    }

    private static bool IsRecoveryEpisode(DerivedIndexRecoverySnapshot snapshot) =>
        snapshot.State is DerivedIndexRecoveryState.Recovering or DerivedIndexRecoveryState.RetryScheduled;

    private async ValueTask EnsureDetectionAsync(IDerivedIndexRecoveryStore recoveryStore, Guid? activeId,
        DerivedIndexRecoveryFailureCategory? category, CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _episodeDetectionRecorded) != 0) return;
        await recoveryStore.AppendAuditAsync(new("recovery_detected", activeId, category, 0, TimeSpan.Zero, null, 0), cancellationToken);
        Volatile.Write(ref _episodeDetectionRecorded, 1);
    }

    private ValueTask PublishAsync(CancellationToken cancellationToken) => _statusPublisher?.PublishAsync(
        new StatusChanged(null, "index-recovery", _timeProvider.GetUtcNow()), cancellationToken) ?? ValueTask.CompletedTask;

    private void PublishSynchronously()
    {
        try { PublishAsync(CancellationToken.None).AsTask().GetAwaiter().GetResult(); }
        catch (Exception) { }
    }
}

internal sealed class SqlMembershipValidationException : Exception;

internal sealed class DerivedIndexRecoveryActiveGenerationChangedException : Exception;
