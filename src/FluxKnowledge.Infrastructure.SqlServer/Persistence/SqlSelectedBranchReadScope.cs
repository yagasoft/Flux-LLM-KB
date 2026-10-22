using System.Data;
using System.Data.Common;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence;

/// <summary>
/// Materialises selected parent branches once for composed activity queries on one connection.
/// The caller owns the context/transaction and must enumerate queries before completing the scope.
/// </summary>
public sealed class SqlSelectedBranchReadScope : IAsyncDisposable
{
    private readonly FluxKnowledgeDbContext _context;
    private readonly Guid _connectionId;
    private bool _dropped;
    private bool _disposed;

    private SqlSelectedBranchReadScope(FluxKnowledgeDbContext context)
    {
        _context = context;
        _connectionId = ((SqlConnection)context.Database.GetDbConnection()).ClientConnectionId;
    }

    public static async ValueTask<SqlSelectedBranchReadScope> CreateAsync(FluxKnowledgeDbContext context,
        IReadOnlyCollection<Guid> selectedBranchIds, CancellationToken cancellationToken)
    {
        await context.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var scope = new SqlSelectedBranchReadScope(context);
        try
        {
            // Creation must be a separate, parameter-free batch: a table created inside
            // sp_executesql would disappear before the composed consumer queries execute.
            await context.Database.ExecuteSqlRawAsync("""
                CREATE TABLE #FluxPipelineSelectedBranches (
                    SourceRevisionId uniqueidentifier NOT NULL,
                    Id uniqueidentifier NOT NULL,
                    PRIMARY KEY CLUSTERED (SourceRevisionId, Id));
                """, cancellationToken).ConfigureAwait(false);
            await context.Database.ExecuteSqlRawAsync("""
                INSERT INTO #FluxPipelineSelectedBranches (SourceRevisionId, Id)
                SELECT DISTINCT branch.SourceRevisionId, branch.Id
                FROM OPENJSON(@selectedBranches) WITH (Id uniqueidentifier '$') chosen
                INNER JOIN SourceProcessorBranches branch ON branch.Id = chosen.Id;
                """, [new SqlParameter("@selectedBranches", SqlDbType.NVarChar, -1)
                    { Value = JsonSerializer.Serialize(selectedBranchIds.Distinct()) }], cancellationToken).ConfigureAwait(false);
            return scope;
        }
        catch
        {
            await scope.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    internal void EnsureFor(FluxKnowledgeDbContext context)
    {
        if (_disposed || _dropped || !ReferenceEquals(context, _context) ||
            ((SqlConnection)context.Database.GetDbConnection()).ClientConnectionId != _connectionId)
            throw new InvalidOperationException("Selected branches require their original live read connection.");
    }

    /// <summary>Normal completion uses the read deadline; cleanup errors remain read failures.</summary>
    public async ValueTask CompleteAsync(CancellationToken cancellationToken)
    {
        EnsureFor(_context);
        await _context.Database.ExecuteSqlRawAsync("DROP TABLE #FluxPipelineSelectedBranches;", cancellationToken).ConfigureAwait(false);
        _dropped = true;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        try
        {
            if (!_dropped)
            {
                // A failed read keeps its original exception. Attempt only bounded cleanup;
                // the owning attempt then disposes its context/transaction before returning.
                using var cleanup = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
                try
                {
                    await _context.Database.ExecuteSqlRawAsync("DROP TABLE IF EXISTS #FluxPipelineSelectedBranches;", cleanup.Token).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is DbException or OperationCanceledException or InvalidOperationException) { }
            }
        }
        finally
        {
            _disposed = true;
            // Balance only our explicit open; never commit/dispose a caller's transaction.
            await _context.Database.CloseConnectionAsync().ConfigureAwait(false);
        }
    }
}
