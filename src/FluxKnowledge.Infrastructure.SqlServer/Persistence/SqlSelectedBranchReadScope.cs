using System.Data;
using System.Data.Common;
using System.Text.Json;
using FluxKnowledge.Application.Sources;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

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
    private bool _gpuFactsAttempted;
    private DateTimeOffset _gpuFactsAt;
    private Guid? _gpuFactsTransactionId;
    private SqlGpuActivityReadEvidence? _gpuEvidence;
    private bool _jobFactsAttempted;
    private Guid? _jobFactsTransactionId;
    private DateTimeOffset _jobFactsAt;
    private DeploymentHoldAdmission? _jobAdmission;
    private SqlGpuActivityReadEvidence? _jobEvidence;
    internal bool HasGpuFacts { get; private set; }
    internal bool HasJobFacts { get; private set; }

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

    /// <summary>Build the four independent job facts once within the caller's read attempt.</summary>
    public async ValueTask MaterializeGpuFactsAsync(DateTimeOffset now, SqlGpuActivityReadEvidence evidence,
        CancellationToken cancellationToken)
    {
        EnsureFor(_context);
        if (_gpuFactsAttempted) throw new InvalidOperationException("GPU facts require a fresh read scope for each attempt.");
        _gpuFactsAttempted = true;
        var transactionId = _context.Database.CurrentTransaction?.TransactionId;
        await _context.Database.ExecuteSqlRawAsync("""
            CREATE TABLE #FluxPipelineGpuFacts (
                ParentJobId uniqueidentifier NOT NULL, SourceRevision bigint NOT NULL,
                HasActiveGpu bit NOT NULL, HasReadyGpu bit NOT NULL,
                HasUncertainGpu bit NOT NULL, HasWaitingGpu bit NOT NULL,
                PRIMARY KEY CLUSTERED (ParentJobId, SourceRevision));
            """, cancellationToken).ConfigureAwait(false);
        await _context.Database.ExecuteSqlRawAsync("""
            INSERT INTO #FluxPipelineGpuFacts
                (ParentJobId, SourceRevision, HasActiveGpu, HasReadyGpu, HasUncertainGpu, HasWaitingGpu)
            SELECT t.ParentJobId, t.SourceRevision,
                MAX(CASE WHEN t.State = 1 AND proof.HasActive = 1 THEN 1 ELSE 0 END),
                MAX(CASE WHEN t.State = 0 AND (t.DeferredUntilUtc IS NULL OR t.DeferredUntilUtc <= @nowUtc) THEN 1 ELSE 0 END),
                MAX(CASE WHEN t.State = 3 THEN 1 ELSE 0 END),
                MAX(CASE WHEN (t.State = 0 AND t.DeferredUntilUtc > @nowUtc)
                    OR (t.State = 1 AND proof.HasPending = 1) THEN 1 ELSE 0 END)
            FROM GpuMiniTasks t
            LEFT JOIN (
                SELECT Id, BatchId, AdmissionGeneration,
                    MAX(CONVERT(int, HasActive)) AS HasActive, MAX(CONVERT(int, HasPending)) AS HasPending
                FROM OPENJSON(@ownedGpuTasks) WITH (
                    Id uniqueidentifier '$.Id', BatchId uniqueidentifier '$.BatchId', AdmissionGeneration bigint '$.AdmissionGeneration',
                    HasActive bit '$.HasActive', HasPending bit '$.HasPending')
                GROUP BY Id, BatchId, AdmissionGeneration
            ) proof ON proof.Id = t.Id AND proof.BatchId = t.BatchId AND proof.AdmissionGeneration = t.AdmissionGeneration
            WHERE t.ParentJobId IS NOT NULL AND t.State IN (0, 1, 3)
            GROUP BY t.ParentJobId, t.SourceRevision;
            """, [new SqlParameter("@nowUtc", SqlDbType.DateTimeOffset) { Value = now },
                new SqlParameter("@ownedGpuTasks", SqlDbType.NVarChar, -1) { Value = evidence.OwnedTasksJson }], cancellationToken).ConfigureAwait(false);
        if (_context.Database.CurrentTransaction?.TransactionId != transactionId)
            throw new InvalidOperationException("GPU facts cannot leave their original transaction.");
        HasGpuFacts = true;
        _gpuFactsAt = now;
        _gpuFactsTransactionId = transactionId;
        _gpuEvidence = evidence;
    }

    /// <summary>Reuse complete job classification only inside one actual Snapshot transaction.</summary>
    public async ValueTask MaterializeJobFactsAsync(DateTimeOffset now, DeploymentHoldAdmission admission,
        SqlGpuActivityReadEvidence evidence, CancellationToken cancellationToken)
    {
        EnsureFor(_context);
        if (_jobFactsAttempted) throw new InvalidOperationException("Job facts require a fresh read scope for each attempt.");
        var transaction = _context.Database.CurrentTransaction;
        if (transaction is null || transaction.GetDbTransaction().IsolationLevel != IsolationLevel.Snapshot ||
            !HasGpuFacts || _gpuFactsTransactionId != transaction.TransactionId || _gpuFactsAt != now || _gpuEvidence != evidence)
            throw new InvalidOperationException("Job facts require the original Snapshot transaction and GPU evidence.");
        _jobFactsAttempted = true;
        _jobFactsTransactionId = transaction.TransactionId;
        _jobFactsAt = now;
        _jobAdmission = admission;
        _jobEvidence = evidence;
        await _context.Database.ExecuteSqlRawAsync("""
            CREATE TABLE #FluxPipelineJobFacts (
                Id uniqueidentifier NOT NULL PRIMARY KEY CLUSTERED,
                PipelineRecordId uniqueidentifier NOT NULL, SourceRootId uniqueidentifier NULL,
                LogicalSourceRevisionId uniqueidentifier NULL, SourceRevision bigint NOT NULL,
                CurrentStage int NOT NULL, MatchesSelectedFileBranch bit NOT NULL,
                Stage int NOT NULL, PublicState int NOT NULL, IsHistorical bit NOT NULL,
                SourceAdmitted bit NOT NULL, HasWorkerLease bit NOT NULL, HasActiveGpu bit NOT NULL,
                HasReadyGpu bit NOT NULL, HasUncertainGpu bit NOT NULL, HasWaitingGpu bit NOT NULL,
                HasPendingDispatch bit NOT NULL, IsDue bit NOT NULL,
                Reason nvarchar(512) COLLATE DATABASE_DEFAULT NULL);
            """, cancellationToken).ConfigureAwait(false);
        await SqlPipelineActivityQuery.PopulateJobFactsAsync(_context, now, admission, evidence, cancellationToken).ConfigureAwait(false);
        HasJobFacts = true;
    }

    internal void EnsureJobFactsFor(FluxKnowledgeDbContext context, DateTimeOffset now,
        DeploymentHoldAdmission admission, SqlGpuActivityReadEvidence evidence)
    {
        if (!_jobFactsAttempted) return;
        EnsureFor(context);
        if (!HasJobFacts || _context.Database.CurrentTransaction?.TransactionId != _jobFactsTransactionId ||
            now != _jobFactsAt || admission != _jobAdmission || evidence != _jobEvidence)
            throw new InvalidOperationException("Job facts cannot leave their original Snapshot read attempt.");
    }

    /// <summary>Normal completion uses the read deadline; cleanup errors remain read failures.</summary>
    public async ValueTask CompleteAsync(CancellationToken cancellationToken)
    {
        EnsureFor(_context);
        await _context.Database.ExecuteSqlRawAsync("DROP TABLE IF EXISTS #FluxPipelineJobFacts; DROP TABLE IF EXISTS #FluxPipelineGpuFacts; DROP TABLE #FluxPipelineSelectedBranches;", cancellationToken).ConfigureAwait(false);
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
                    await _context.Database.ExecuteSqlRawAsync("DROP TABLE IF EXISTS #FluxPipelineJobFacts; DROP TABLE IF EXISTS #FluxPipelineGpuFacts; DROP TABLE IF EXISTS #FluxPipelineSelectedBranches;", cleanup.Token).ConfigureAwait(false);
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
