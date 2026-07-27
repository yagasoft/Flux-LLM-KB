using System.Collections.Immutable;
using System.Data;
using System.Text.Json;
using FluxKnowledge.Application.Indexing;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence;

public sealed class SqlDerivedIndexRecoveryStore(
    IDbContextFactory<FluxKnowledgeDbContext> contextFactory,
    TimeProvider timeProvider) : IDerivedIndexRecoveryStore
{
    private const string LockResource = "FluxKnowledge.DerivedIndexRecovery";
    private const string AuditEventType = "derived_index_recovery";
    private const string AuditActor = "DerivedIndexRecoveryService";

    public async ValueTask<DerivedIndexRecoverySqlSnapshot> ReadActiveAsync(
        CancellationToken cancellationToken)
    {
        await using var context = await contextFactory
            .CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);
        var activeGenerationId = await context.IndexState.AsNoTracking()
            .Where(state => state.Id == 1)
            .Select(state => state.ActiveIndexGenerationId)
            .SingleAsync(cancellationToken)
            .ConfigureAwait(false);
        var generation = activeGenerationId is null
            ? null
            : await context.IndexGenerations.AsNoTracking()
                .Where(candidate => candidate.Id == activeGenerationId)
                .Select(candidate => new IndexGenerationDescriptor(
                    candidate.Id,
                    candidate.ModelFingerprint,
                    candidate.Dimensions,
                    candidate.IndexPath,
                    candidate.MetadataChecksum,
                    candidate.VectorCount))
                .SingleOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);
        var membership = activeGenerationId is null
            ? []
            : await (
                    from item in context.IndexGenerationVectors.AsNoTracking()
                    join vector in context.Vectors.AsNoTracking() on item.VectorId equals vector.VectorId
                    where item.GenerationId == activeGenerationId
                    orderby vector.VectorId
                    select new CanonicalVector(
                        vector.VectorId,
                        vector.TextChunkId,
                        vector.ModelFingerprint,
                        vector.Dimensions,
                        vector.Values,
                        vector.TextChunkContentHash,
                        vector.PayloadChecksum,
                        vector.SourceRevision))
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
        var referencedGenerationIds = await context.Vectors.AsNoTracking()
            .Select(vector => vector.IndexGenerationId)
            .Union(context.IndexGenerationVectors.AsNoTracking().Select(item => item.GenerationId))
            .Distinct()
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (activeGenerationId is { } activeId)
        {
            referencedGenerationIds.Add(activeId);
        }

        return new DerivedIndexRecoverySqlSnapshot(
            activeGenerationId,
            generation,
            [.. membership],
            referencedGenerationIds.ToImmutableHashSet());
    }

    public async ValueTask<IDerivedIndexRecoveryLease?> TryAcquireExclusiveLeaseAsync(
        TimeSpan lockTimeout,
        CancellationToken cancellationToken)
    {
        if (lockTimeout < TimeSpan.Zero || lockTimeout.TotalMilliseconds > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(lockTimeout));
        }

        string connectionString;
        await using (var context = await contextFactory
                         .CreateDbContextAsync(cancellationToken)
                         .ConfigureAwait(false))
        {
            connectionString = context.Database.GetConnectionString()
                ?? throw new InvalidOperationException("The recovery SQL connection string is unavailable.");
        }

        var connection = new SqlConnection(connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = new SqlCommand(
                """
                DECLARE @result int;
                EXEC @result = sp_getapplock
                    @Resource = @resource,
                    @LockMode = 'Exclusive',
                    @LockOwner = 'Session',
                    @LockTimeout = @lockTimeout;
                SELECT @result;
                """,
                connection);
            command.Parameters.Add("@resource", SqlDbType.NVarChar, 255).Value = LockResource;
            command.Parameters.Add("@lockTimeout", SqlDbType.Int).Value = (int)Math.Ceiling(lockTimeout.TotalMilliseconds);
            var result = (int)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) ?? -999);
            if (result < 0)
            {
                await connection.DisposeAsync().ConfigureAwait(false);
                return null;
            }

            return new SqlDerivedIndexRecoveryLease(connection);
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async ValueTask AppendAuditAsync(
        DerivedIndexRecoveryAuditEvent auditEvent,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(auditEvent);
        await using var context = await contextFactory
            .CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);
        context.AuditEvents.Add(new AuditEventEntity
        {
            PipelineRecordId = null,
            EventType = AuditEventType,
            Actor = AuditActor,
            DetailsJson = CreateAuditDetails(auditEvent),
            OccurredAtUtc = timeProvider.GetUtcNow()
        });
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string CreateAuditDetails(DerivedIndexRecoveryAuditEvent auditEvent) =>
        JsonSerializer.Serialize(new
        {
            category = SanitizeCategory(auditEvent.EventType),
            activeGenerationId = auditEvent.ActiveGenerationId?.ToString("D"),
            failureCategory = auditEvent.FailureCategory?.ToString(),
            attemptCount = Math.Clamp(auditEvent.AttemptCount, 0, 100_000),
            elapsedMilliseconds = Math.Clamp((long)auditEvent.Elapsed.TotalMilliseconds, 0, 86_400_000),
            nextRetryAtUtc = auditEvent.NextRetryAtUtc,
            cleanedCandidateCount = Math.Clamp(auditEvent.CleanedCandidateCount, 0, 1_000_000)
        });

    private static string SanitizeCategory(string category)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(category);
        var sanitised = new string(category
            .Take(128)
            .Select(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-'
                ? char.ToLowerInvariant(character)
                : '_')
            .ToArray());
        return sanitised.Trim('_') is { Length: > 0 } result ? result : "unknown";
    }

    private sealed class SqlDerivedIndexRecoveryLease(SqlConnection connection)
        : IDerivedIndexRecoveryLease
    {
        private SqlConnection? _connection = connection;

        public async ValueTask DisposeAsync()
        {
            var heldConnection = Interlocked.Exchange(ref _connection, null);
            if (heldConnection is null)
            {
                return;
            }

            try
            {
                await using var command = new SqlCommand(
                    """
                    EXEC sp_releaseapplock
                        @Resource = @resource,
                        @LockOwner = 'Session';
                    """,
                    heldConnection);
                command.Parameters.Add("@resource", SqlDbType.NVarChar, 255).Value = LockResource;
                await command.ExecuteNonQueryAsync(CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                await heldConnection.DisposeAsync().ConfigureAwait(false);
            }
        }
    }
}
