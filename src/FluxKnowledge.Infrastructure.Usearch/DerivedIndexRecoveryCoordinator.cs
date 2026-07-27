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
    private readonly Channel<DerivedIndexRecoveryFault> _signals = Channel.CreateUnbounded<DerivedIndexRecoveryFault>();
    private DerivedIndexRecoverySnapshot _snapshot = new(DerivedIndexRecoveryState.Starting, null, null, null, null, 0);
    private int _attempts;

    public DerivedIndexRecoveryCoordinator(IServiceScopeFactory scopeFactory,
        DerivedIndexFileSystem fileSystem, TimeProvider timeProvider, IStatusEventPublisher? statusPublisher = null,
        DerivedIndexRecoveryOptions? options = null)
    {
        _scopeFactory = scopeFactory; _fileSystem = fileSystem; _timeProvider = timeProvider; _statusPublisher = statusPublisher;
        _options = options ?? DerivedIndexRecoveryOptions.Default;
    }
    public DerivedIndexRecoverySnapshot Snapshot => Volatile.Read(ref _snapshot);
    public void Notify(DerivedIndexRecoveryFault fault) => _signals.Writer.TryWrite(fault);
    public ValueTask<DerivedIndexRecoveryFault> WaitAsync(CancellationToken cancellationToken) => _signals.Reader.ReadAsync(cancellationToken);

    public async ValueTask RunOnceAsync(CancellationToken cancellationToken)
    {
        IDerivedIndexRecoveryStore? recoveryStore = null;
        Guid? activeId = null;
        string? replacementPath = null;
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            recoveryStore = scope.ServiceProvider.GetRequiredService<IDerivedIndexRecoveryStore>();
            var generationStore = scope.ServiceProvider.GetRequiredService<IIndexGenerationStore>();
            var builder = scope.ServiceProvider.GetRequiredService<UsearchGenerationBuilder>();
            var validator = scope.ServiceProvider.GetRequiredService<UsearchGenerationValidator>();
            var started = _timeProvider.GetUtcNow();
            await using var lease = await recoveryStore.TryAcquireExclusiveLeaseAsync(TimeSpan.Zero, cancellationToken);
            if (lease is null) return;
            var sql = await recoveryStore.ReadActiveAsync(cancellationToken);
            activeId = sql.ActiveGenerationId;
            ValidateSql(sql);
            if (!_fileSystem.TryCanonicalInRoot(sql.Generation!.IndexPath, out var activePath) ||
                !_fileSystem.IsIntendedGenerationPath(activePath) ||
                (Directory.Exists(activePath) && !_fileSystem.IsValidDirectory(activePath)))
            {
                throw new InvalidOperationException("The active derived-index path configuration is invalid.");
            }
            if (Directory.Exists(activePath))
            {
                try
                {
                    validator.Validate(activePath, sql.Generation with { IndexPath = activePath }, sql.Membership);
                    await CompleteAsync(recoveryStore, sql.ActiveGenerationId, started, 0, cancellationToken);
                    return;
                }
                catch (IndexGenerationValidationException)
                {
                    // A corrupt derived directory is replaceable from the already validated SQL snapshot.
                }
            }
            var oldPath = sql.Generation!.IndexPath;
            var replacement = await builder.BuildRecoveryCandidateAsync(sql.Generation, sql.Membership, cancellationToken);
            replacementPath = replacement.IndexPath;
            await generationStore.UpdateGenerationMetadataAsync(replacement, cancellationToken);
            var fresh = await recoveryStore.ReadActiveAsync(cancellationToken);
            // The path reference set is deliberately re-read after metadata mutation while the SQL lease is held.
            _fileSystem.TryQuarantine(oldPath, fresh.ReferencedIndexPaths);
            var cleaned = _fileSystem.Cleanup("staging", _options.StagingRetention, _timeProvider.GetUtcNow(), fresh.ReferencedIndexPaths) +
                _fileSystem.Cleanup("quarantine", _options.QuarantineRetention, _timeProvider.GetUtcNow(), fresh.ReferencedIndexPaths);
            await CompleteAsync(recoveryStore, sql.ActiveGenerationId, started, cleaned, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            if (exception is RecoveryCandidatePlacementException placement) replacementPath = placement.Path;
            if (replacementPath is not null && recoveryStore is not null)
            {
                try { _fileSystem.TryQuarantine(replacementPath, (await recoveryStore.ReadActiveAsync(cancellationToken)).ReferencedIndexPaths); }
                catch (Exception) { }
            }
            await RecordFaultAsync(recoveryStore, exception, activeId, cancellationToken);
        }
    }

    private async ValueTask RecordFaultAsync(IDerivedIndexRecoveryStore? recoveryStore, Exception exception, Guid? activeGenerationId, CancellationToken cancellationToken)
    {
        var category = exception switch
        {
            UnauthorizedAccessException => DerivedIndexRecoveryFailureCategory.PermissionsDenied,
            SqlMembershipValidationException => DerivedIndexRecoveryFailureCategory.SqlMembershipInvalid,
            IndexGenerationValidationException => DerivedIndexRecoveryFailureCategory.InvalidDerivedIndex,
            InvalidOperationException => DerivedIndexRecoveryFailureCategory.ConfigurationInvalid,
            _ => DerivedIndexRecoveryFailureCategory.TransientIo
        };
        var decision = DerivedIndexRecoveryPolicy.Decide(category, ++_attempts);
        var now = _timeProvider.GetUtcNow();
        Volatile.Write(ref _snapshot, new(decision.NextState, activeGenerationId, null,
            decision.Delay is { } delay ? now + delay : null, decision.FailureCategory, 0));
        if (recoveryStore is not null)
        {
            try { await recoveryStore.AppendAuditAsync(new("recovery_failed", activeGenerationId,
                decision.FailureCategory, _attempts, TimeSpan.Zero, Snapshot.NextRetryAtUtc, 0), cancellationToken); }
            catch (Exception) { }
        }
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
            throw new SqlMembershipValidationException();
    }

    private async ValueTask CompleteAsync(IDerivedIndexRecoveryStore recoveryStore, Guid? activeId, DateTimeOffset started, int cleaned, CancellationToken cancellationToken)
    {
        _attempts = 0;
        Volatile.Write(ref _snapshot, new(DerivedIndexRecoveryState.Healthy, activeId, _timeProvider.GetUtcNow(), null, null, cleaned));
        await recoveryStore.AppendAuditAsync(new("recovery_healthy", activeId, null, 0,
            _timeProvider.GetUtcNow() - started, null, cleaned), cancellationToken);
        await PublishAsync(cancellationToken);
    }

    private ValueTask PublishAsync(CancellationToken cancellationToken) => _statusPublisher?.PublishAsync(
        new StatusChanged(null, "index-recovery", _timeProvider.GetUtcNow()), cancellationToken) ?? ValueTask.CompletedTask;
}

internal sealed class SqlMembershipValidationException : Exception;
