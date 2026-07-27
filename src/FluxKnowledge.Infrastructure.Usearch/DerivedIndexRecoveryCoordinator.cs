using System.Security.Cryptography;
using System.Threading.Channels;
using FluxKnowledge.Application.Contracts;
using FluxKnowledge.Application.Indexing;
using FluxKnowledge.Application.Ports;

namespace FluxKnowledge.Infrastructure.Usearch;

public sealed class DerivedIndexRecoveryCoordinator : IDerivedIndexRecoveryStatus, IDerivedIndexRecoverySignal
{
    private readonly IDerivedIndexRecoveryStore? _recoveryStore;
    private readonly IIndexGenerationStore? _generationStore;
    private readonly UsearchGenerationBuilder? _builder;
    private readonly UsearchGenerationValidator? _validator;
    private readonly DerivedIndexFileSystem? _fileSystem;
    private readonly IStatusEventPublisher? _statusPublisher;
    private readonly TimeProvider _timeProvider;
    private readonly DerivedIndexRecoveryOptions _options;
    private readonly Channel<DerivedIndexRecoveryFault> _signals = Channel.CreateUnbounded<DerivedIndexRecoveryFault>();
    private DerivedIndexRecoverySnapshot _snapshot = new(DerivedIndexRecoveryState.Starting, null, null, null, null, 0);
    private int _attempts;

    public DerivedIndexRecoveryCoordinator(IDerivedIndexRecoveryStore recoveryStore, IIndexGenerationStore generationStore,
        UsearchGenerationBuilder builder, UsearchGenerationValidator validator, UsearchIndexOptions indexOptions,
        DerivedIndexFileSystem fileSystem, TimeProvider timeProvider, IStatusEventPublisher? statusPublisher = null,
        DerivedIndexRecoveryOptions? options = null)
    {
        _recoveryStore = recoveryStore; _generationStore = generationStore; _builder = builder; _validator = validator;
        _fileSystem = fileSystem; _timeProvider = timeProvider; _statusPublisher = statusPublisher;
        _options = options ?? DerivedIndexRecoveryOptions.Default;
    }

    private DerivedIndexRecoveryCoordinator(UsearchIndexOptions options, TimeProvider timeProvider)
    { _timeProvider = timeProvider; _options = DerivedIndexRecoveryOptions.Default; }

    public static DerivedIndexRecoveryCoordinator ForTesting(UsearchIndexOptions options, TimeProvider timeProvider) => new(options, timeProvider);
    public DerivedIndexRecoverySnapshot Snapshot => Volatile.Read(ref _snapshot);
    public void Notify(DerivedIndexRecoveryFault fault) => _signals.Writer.TryWrite(fault);
    public ValueTask<DerivedIndexRecoveryFault> WaitAsync(CancellationToken cancellationToken) => _signals.Reader.ReadAsync(cancellationToken);

    public async ValueTask RunOnceAsync(CancellationToken cancellationToken)
    {
        if (_recoveryStore is null || _generationStore is null || _builder is null || _validator is null || _fileSystem is null)
            throw new InvalidOperationException("Recovery coordinator is not configured.");
        var started = _timeProvider.GetUtcNow();
        await using var lease = await _recoveryStore.TryAcquireExclusiveLeaseAsync(TimeSpan.Zero, cancellationToken);
        if (lease is null) return;
        try
        {
            var sql = await _recoveryStore.ReadActiveAsync(cancellationToken);
            ValidateSql(sql);
            if (_fileSystem.IsValidDirectory(sql.Generation!.IndexPath))
            {
                _validator.Validate(sql.Generation.IndexPath, sql.Generation, sql.Membership);
                await CompleteAsync(sql.ActiveGenerationId, started, 0, cancellationToken);
                return;
            }
            var oldPath = sql.Generation!.IndexPath;
            var replacement = await _builder.BuildRecoveryCandidateAsync(sql.Generation, sql.Membership, cancellationToken);
            await _generationStore.UpdateGenerationMetadataAsync(replacement, cancellationToken);
            var fresh = await _recoveryStore.ReadActiveAsync(cancellationToken);
            // The path reference set is deliberately re-read after metadata mutation while the SQL lease is held.
            _fileSystem.TryQuarantine(oldPath, fresh.ReferencedIndexPaths);
            var cleaned = _fileSystem.Cleanup("staging", _options.StagingRetention, _timeProvider.GetUtcNow(), fresh.ReferencedIndexPaths) +
                _fileSystem.Cleanup("quarantine", _options.QuarantineRetention, _timeProvider.GetUtcNow(), fresh.ReferencedIndexPaths);
            await CompleteAsync(sql.ActiveGenerationId, started, cleaned, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await RecordFaultAsync(exception, Snapshot.ActiveGenerationId, cancellationToken);
        }
    }

    public async ValueTask RecordFaultAsync(Exception exception, Guid? activeGenerationId, CancellationToken cancellationToken)
    {
        var category = exception switch
        {
            UnauthorizedAccessException => DerivedIndexRecoveryFailureCategory.PermissionsDenied,
            IndexGenerationValidationException => DerivedIndexRecoveryFailureCategory.InvalidDerivedIndex,
            InvalidOperationException => DerivedIndexRecoveryFailureCategory.SqlMembershipInvalid,
            _ => DerivedIndexRecoveryFailureCategory.TransientIo
        };
        var decision = DerivedIndexRecoveryPolicy.Decide(category, ++_attempts);
        var now = _timeProvider.GetUtcNow();
        Volatile.Write(ref _snapshot, new(decision.NextState, activeGenerationId, null,
            decision.Delay is { } delay ? now + delay : null, decision.FailureCategory, 0));
        if (_recoveryStore is not null) await _recoveryStore.AppendAuditAsync(new("recovery_failed", activeGenerationId,
            decision.FailureCategory, _attempts, TimeSpan.Zero, Snapshot.NextRetryAtUtc, 0), cancellationToken);
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
            throw new InvalidOperationException("The SQL immutable membership is invalid.");
    }

    private async ValueTask CompleteAsync(Guid? activeId, DateTimeOffset started, int cleaned, CancellationToken cancellationToken)
    {
        _attempts = 0;
        Volatile.Write(ref _snapshot, new(DerivedIndexRecoveryState.Healthy, activeId, _timeProvider.GetUtcNow(), null, null, cleaned));
        if (_recoveryStore is not null) await _recoveryStore.AppendAuditAsync(new("recovery_healthy", activeId, null, 0,
            _timeProvider.GetUtcNow() - started, null, cleaned), cancellationToken);
        await PublishAsync(cancellationToken);
    }

    private ValueTask PublishAsync(CancellationToken cancellationToken) => _statusPublisher?.PublishAsync(
        new StatusChanged(null, "index-recovery", _timeProvider.GetUtcNow()), cancellationToken) ?? ValueTask.CompletedTask;
}
