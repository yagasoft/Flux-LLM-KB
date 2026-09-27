using System.Data;
using FluxKnowledge.Application.Gpu;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence;

/// <summary>A private SQL session pins membership, vector rows and derived files across instances.</summary>
public sealed class SqlCorpusGenerationLeaseStore(
    IDbContextFactory<FluxKnowledgeDbContext> contextFactory, TimeProvider timeProvider) : ICorpusGenerationLeaseStore
{
    internal static async Task<bool> TryAcquireCleanupTransactionAsync(FluxKnowledgeDbContext context,
        IGpuInteractiveOwnerProbe? probe, CancellationToken cancellationToken)
    {
        var transaction = context.Database.CurrentTransaction
            ?? throw new InvalidOperationException("Query cleanup ownership requires a transaction.");
        await using var command = new SqlCommand("""
            DECLARE @result int;
            EXEC @result = sp_getapplock @Resource = @resource, @LockMode = 'Exclusive',
                @LockOwner = 'Transaction', @LockTimeout = 0;
            SELECT @result;
            """, (SqlConnection)context.Database.GetDbConnection(), (SqlTransaction)transaction.GetDbTransaction());
        command.Parameters.Add("@resource", SqlDbType.NVarChar, 255).Value = SqlDerivedIndexRecoveryStore.LockResource;
        var result = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
        if (result == -1) return false;
        if (result == -2 && cancellationToken.IsCancellationRequested) throw new OperationCanceledException(cancellationToken);
        if (result < 0) throw new InvalidOperationException($"corpus-cleanup-lock-failed:{result}");
        return await SqlCorpusQueryLeaseRecovery.TryDrainAbandonedAsync(context, probe, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<ICorpusGenerationLease?> TryAcquireAsync(Guid ownerInstanceId,
        GpuInteractiveOwnerIdentity owner, string modelFingerprint, int dimensions, CancellationToken cancellationToken)
    {
        owner.Validate();
        if (ownerInstanceId == Guid.Empty || dimensions <= 0 || string.IsNullOrWhiteSpace(modelFingerprint) ||
            modelFingerprint.Length > 256 || modelFingerprint != modelFingerprint.Trim())
            throw new ArgumentException("corpus-query-profile-invalid");
        string connectionString;
        await using (var factoryContext = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false))
            connectionString = factoryContext.Database.GetConnectionString()
                ?? throw new InvalidOperationException("corpus-query-sql-unavailable");

        // Never return a connection with uncertain session locks to a shared pool.
        var connection = new SqlConnection(new SqlConnectionStringBuilder(connectionString) { Pooling = false }.ConnectionString);
        SqlLease? lease = null;
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = new SqlCommand("""
                DECLARE @result int;
                EXEC @result = sp_getapplock @Resource = @resource, @LockMode = 'Shared',
                    @LockOwner = 'Session', @LockTimeout = 0;
                SELECT @result;
                """, connection);
            command.Parameters.Add("@resource", SqlDbType.NVarChar, 255).Value = SqlDerivedIndexRecoveryStore.LockResource;
            var result = (int)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) ?? -999);
            if (result == -1) return null;
            if (result == -2 && cancellationToken.IsCancellationRequested) throw new OperationCanceledException(cancellationToken);
            if (result < 0) throw new InvalidOperationException($"corpus-query-lock-failed:{result}");
            await using var context = new FluxKnowledgeDbContext(new DbContextOptionsBuilder<FluxKnowledgeDbContext>()
                .UseSqlServer(connection).Options);
            await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false);
            await SqlPublishedPassageSelection.AcquireFenceAsync(context, cancellationToken).ConfigureAwait(false);
            var state = await context.IndexState.AsNoTracking().SingleAsync(value => value.Id == 1, cancellationToken).ConfigureAwait(false);
            if (state.CorpusRebuildOperationId is not null) return null;
            var generation = await context.IndexGenerations.AsNoTracking().SingleOrDefaultAsync(value =>
                value.Id == state.ActiveIndexGenerationId && value.RetiredAtUtc == null && value.IndexPath != string.Empty &&
                EF.Functions.Collate(value.ModelFingerprint, "Latin1_General_100_BIN2") == modelFingerprint && value.Dimensions == dimensions &&
                value.CorpusEpoch == state.CorpusEpoch && value.CorpusVersion == state.CorpusVersion, cancellationToken).ConfigureAwait(false);
            if (generation is null || generation.VectorCount <= 0 || generation.ValidatedAtUtc is null ||
                await context.IndexGenerationVectors.LongCountAsync(value => value.GenerationId == generation.Id, cancellationToken).ConfigureAwait(false) != generation.VectorCount)
                return null;
            await using var sessionCommand = new SqlCommand("SELECT @@SPID", connection, (SqlTransaction)transaction.GetDbTransaction());
            var sessionId = Convert.ToInt32(await sessionCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
            var id = Guid.NewGuid();
            context.CorpusQueryLeases.Add(new CorpusQueryLeaseEntity
            {
                Id = id, GenerationId = generation.Id, CorpusEpoch = state.CorpusEpoch, CorpusVersion = state.CorpusVersion,
                ModelFingerprint = modelFingerprint, Dimensions = dimensions, OwnerInstanceId = ownerInstanceId,
                OwnerProcessId = owner.ProcessId, OwnerStartedAtUtc = owner.StartedAtUtc,
                OwnerMachineFingerprint = owner.MachineFingerprint, SqlSessionId = sessionId, CreatedAtUtc = timeProvider.GetUtcNow()
            });
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            lease = new SqlLease(connection, id, ownerInstanceId, owner, sessionId,
                new SqlConnectionStringBuilder(connectionString) { Pooling = false }.ConnectionString,
                new IndexGenerationDescriptor(generation.Id, generation.ModelFingerprint,
                generation.Dimensions, generation.IndexPath, generation.MetadataChecksum, generation.VectorCount,
                new CorpusPublicationStamp(state.CorpusEpoch, state.CorpusVersion)));
            return lease;
        }
        finally
        {
            if (lease is null) await connection.DisposeAsync().ConfigureAwait(false);
        }
    }

    private sealed class SqlLease(SqlConnection connection, Guid id, Guid ownerInstanceId, GpuInteractiveOwnerIdentity owner,
        int sessionId, string releaseConnectionString, IndexGenerationDescriptor generation) : ICorpusGenerationLease
    {
        private SqlConnection? _connection = connection;
        private readonly SemaphoreSlim _gate = new(1, 1);
        private bool _released;
        public Guid LeaseId => id;
        public IndexGenerationDescriptor Generation => generation;

        public async ValueTask<bool> IsCurrentAsync(CancellationToken cancellationToken)
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (_connection is not { State: ConnectionState.Open } held) return false;
                await using var command = new SqlCommand("""
                    SELECT CONVERT(int, CASE WHEN APPLOCK_MODE('public', @resource, 'Session') = 'Shared'
                        AND EXISTS (SELECT 1 FROM [CorpusQueryLeases] AS lease JOIN [IndexState] AS state ON state.Id = 1
                            JOIN [IndexGenerations] AS generation ON generation.Id = lease.GenerationId
                            WHERE lease.Id = @leaseId AND lease.SqlSessionId = @@SPID
                                AND state.ActiveIndexGenerationId = lease.GenerationId
                                AND state.CorpusEpoch = lease.CorpusEpoch AND state.CorpusVersion = lease.CorpusVersion
                                AND generation.CorpusEpoch = lease.CorpusEpoch AND generation.CorpusVersion = lease.CorpusVersion
                                AND generation.ModelFingerprint COLLATE Latin1_General_100_BIN2 = lease.ModelFingerprint
                                AND generation.Dimensions = lease.Dimensions
                                AND generation.RetiredAtUtc IS NULL)
                        THEN 1 ELSE 0 END);
                    """, held);
                command.Parameters.Add("@resource", SqlDbType.NVarChar, 255).Value = SqlDerivedIndexRecoveryStore.LockResource;
                command.Parameters.Add("@leaseId", SqlDbType.UniqueIdentifier).Value = id;
                return (int)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) ?? 0) == 1;
            }
            catch (SqlException) when (!cancellationToken.IsCancellationRequested) { return false; }
            finally { _gate.Release(); }
        }

        public async ValueTask DisposeAsync()
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_released) return;
                if (_connection is { } held)
                {
                    await held.DisposeAsync().ConfigureAwait(false);
                    _connection = null;
                }
                // Explicit release follows native disposal. Use a fresh connection so a
                // lost/reconnected query session cannot strand its authoritative record.
                await using var release = new SqlConnection(releaseConnectionString);
                await release.OpenAsync(CancellationToken.None).ConfigureAwait(false);
                await using var command = new SqlCommand("""
                    DELETE FROM [CorpusQueryLeases] WHERE [Id] = @leaseId AND [GenerationId] = @generationId
                        AND [OwnerInstanceId] = @ownerInstance AND [OwnerProcessId] = @processId
                        AND [OwnerStartedAtUtc] = @startedAt AND [OwnerMachineFingerprint] = @machine
                        AND [SqlSessionId] = @sessionId;
                    """, release);
                command.Parameters.Add("@leaseId", SqlDbType.UniqueIdentifier).Value = id;
                command.Parameters.Add("@generationId", SqlDbType.UniqueIdentifier).Value = generation.Id;
                command.Parameters.Add("@ownerInstance", SqlDbType.UniqueIdentifier).Value = ownerInstanceId;
                command.Parameters.Add("@processId", SqlDbType.Int).Value = owner.ProcessId;
                command.Parameters.Add("@startedAt", SqlDbType.DateTimeOffset).Value = owner.StartedAtUtc;
                command.Parameters.Add("@machine", SqlDbType.NVarChar, 64).Value = owner.MachineFingerprint;
                command.Parameters.Add("@sessionId", SqlDbType.Int).Value = sessionId;
                await command.ExecuteNonQueryAsync(CancellationToken.None).ConfigureAwait(false);
                _released = true;
            }
            finally { _gate.Release(); }
        }
    }
}
