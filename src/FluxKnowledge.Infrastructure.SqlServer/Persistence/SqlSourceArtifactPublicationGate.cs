using System.Data;
using System.Globalization;
using System.Text.Json;
using FluxKnowledge.Application.Ports;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence;

/// <summary>
/// Holds one SQL Server application lock from retained-byte publication through its
/// source-reference commit, or exclusively while the same blob is being removed.
/// </summary>
public sealed class SqlSourceArtifactPublicationGate(
    IDbContextFactory<FluxKnowledgeDbContext> contextFactory) : ISourceArtifactPublicationGate
{
    public async ValueTask<ISourceArtifactPublicationLease> AcquireSharedAsync(
        string contentSha256,
        CancellationToken cancellationToken) =>
        await AcquireAsync(contentSha256, "Shared", waitIndefinitely: true, cancellationToken).ConfigureAwait(false)
            ?? throw new IOException("The retained artifact publication lease could not be acquired.");

    public ValueTask<ISourceArtifactPublicationLease?> TryAcquireExclusiveAsync(
        string contentSha256,
        CancellationToken cancellationToken) =>
        AcquireAsync(contentSha256, "Exclusive", waitIndefinitely: false, cancellationToken);


    public async ValueTask<ISourceArtifactPublicationBatchLease?> TryAcquireExclusiveBatchAsync(
        IReadOnlyList<string> orderedContentHashes, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(orderedContentHashes.Count, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(orderedContentHashes.Count, ISourceDeletionStore.FileCleanupBatchSize);
        var resources = orderedContentHashes.Select(hash =>
        {
            ValidateHash(hash);
            return ResourceName(hash);
        }).ToArray();

        var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Transaction ownership releases all locks, including an acquired
            // prefix whose acknowledgement was lost, when this scope rolls back.
            var ownership = await context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            var transaction = (SqlTransaction)ownership.GetDbTransaction();
            var connection = (SqlConnection)context.Database.GetDbConnection();
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                DECLARE @acquired int = 0, @resource nvarchar(255), @result int;
                WHILE @acquired < @count
                BEGIN
                    SET @resource = JSON_VALUE(@resources, CONCAT('$[', @acquired, ']'));
                    EXEC @result = sys.sp_getapplock
                        @Resource = @resource, @LockMode = 'Exclusive', @LockOwner = 'Transaction',
                        @LockTimeout = 0, @DbPrincipal = 'public';
                    IF @result < 0 BREAK;
                    SET @acquired += 1;
                END
                SELECT @acquired;
                """;
            command.Parameters.Add("@resources", SqlDbType.NVarChar, -1).Value = JsonSerializer.Serialize(resources);
            command.Parameters.Add("@count", SqlDbType.Int).Value = resources.Length;
            var acquired = (int)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) ?? 0);
            if (acquired > 0)
                return new BatchLease(context, connection, transaction, acquired);

            await context.DisposeAsync().ConfigureAwait(false);
            return null;
        }
        catch
        {
            await context.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static void ValidateHash(string contentSha256)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contentSha256);
        if (contentSha256.Length != 64 || contentSha256.Any(character => character is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
            throw new InvalidDataException("A source artifact publication lease requires a lowercase SHA-256.");
    }

    private sealed class BatchLease(FluxKnowledgeDbContext context, SqlConnection connection,
        SqlTransaction transaction, int acquiredCount) : ISourceArtifactPublicationBatchLease
    {
        public int AcquiredCount => acquiredCount;

        public async ValueTask<bool> IsHeldAsync(string contentSha256, CancellationToken cancellationToken)
        {
            if (connection.State != ConnectionState.Open || transaction.Connection != connection) return false;
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "SELECT APPLOCK_MODE(N'public', @resource, N'Transaction');";
            command.Parameters.Add("@resource", SqlDbType.NVarChar, 255).Value = ResourceName(contentSha256);
            try
            {
                return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is "Exclusive";
            }
            catch (SqlException)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return false;
            }
        }

        public ValueTask DisposeAsync() => context.DisposeAsync();
    }

    private async ValueTask<ISourceArtifactPublicationLease?> AcquireAsync(
        string contentSha256,
        string mode,
        bool waitIndefinitely,
        CancellationToken cancellationToken)
    {
        ValidateHash(contentSha256);

        var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await context.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            var connection = (SqlConnection)context.Database.GetDbConnection();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                DECLARE @result int;
                EXEC @result = sys.sp_getapplock
                    @Resource = @resource,
                    @LockMode = @mode,
                    @LockOwner = 'Session',
                    @LockTimeout = @lockTimeout,
                    @DbPrincipal = 'public';
                SELECT @result;
                """;
            command.Parameters.AddWithValue("@resource", ResourceName(contentSha256));
            command.Parameters.AddWithValue("@mode", mode);
            command.Parameters.AddWithValue("@lockTimeout", waitIndefinitely ? -1 : 0);
            var result = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
            if (result < 0)
            {
                await context.DisposeAsync().ConfigureAwait(false);
                return null;
            }

            return new Lease(context, connection, contentSha256);
        }
        catch
        {
            await context.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static string ResourceName(string contentSha256) => $"FluxKnowledge.SourceArtifact.{contentSha256}";

    private sealed class Lease(
        FluxKnowledgeDbContext context,
        SqlConnection connection,
        string contentSha256) : ISourceArtifactPublicationLease
    {
        private FluxKnowledgeDbContext? _context = context;
        private SqlConnection? _connection = connection;

        public async ValueTask DisposeAsync()
        {
            var ownedContext = Interlocked.Exchange(ref _context, null);
            var ownedConnection = Interlocked.Exchange(ref _connection, null);
            if (ownedContext is null || ownedConnection is null)
            {
                return;
            }

            try
            {
                await using var command = ownedConnection.CreateCommand();
                command.CommandText = "EXEC sys.sp_releaseapplock @Resource = @resource, @LockOwner = 'Session', @DbPrincipal = 'public';";
                command.Parameters.AddWithValue("@resource", ResourceName(contentSha256));
                _ = await command.ExecuteScalarAsync().ConfigureAwait(false);
            }
            finally
            {
                await ownedContext.DisposeAsync().ConfigureAwait(false);
            }
        }
    }
}
