using System.Globalization;
using FluxKnowledge.Application.Ports;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

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

    private async ValueTask<ISourceArtifactPublicationLease?> AcquireAsync(
        string contentSha256,
        string mode,
        bool waitIndefinitely,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contentSha256);
        if (contentSha256.Length != 64 || contentSha256.Any(character => character is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
        {
            throw new InvalidDataException("A source artifact publication lease requires a lowercase SHA-256.");
        }

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
