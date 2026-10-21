using System.Data;
using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using FluxKnowledge.Application.Gpu;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence;

/// <summary>SQL-only retirement admission backed by a privileged Recovery reference audit.</summary>
public sealed class SqlIndexRecoveryReferences(IDbContextFactory<FluxKnowledgeDbContext> factory,
    TimeProvider clock, IGpuInteractiveOwnerProbe owners)
{
    public const string LockResource = "FluxKnowledge.RecoveryReferences";
    internal Func<CancellationToken, ValueTask>? AfterCapture { get; init; }
    internal Func<SqlConnection, CancellationToken, ValueTask>? BeforeProtocolTransaction { get; init; }
    internal Func<SqlConnection, CancellationToken, ValueTask>? BeforeProtocolSave { get; init; }
    internal Func<SqlConnection, CancellationToken, ValueTask>? AfterProtocolTransaction { get; init; }
    internal Func<SqlConnection, CancellationToken, ValueTask>? AfterAuditCommit { get; init; }

    public async ValueTask<RecoveryReferenceWriter> BeginWriteAsync(string recoveryRoot, CancellationToken token,
        bool ownerExitSufficient = false)
    {
        var root = Canonical(recoveryRoot);
        var owner = owners.Current; owner.Validate();
        var connection = await OpenSessionAsync("Shared", token);
        try
        {
            if (BeforeProtocolTransaction is { } before) await before(connection, token);
            await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable, token);
            await RequireSessionAsync(connection, "Shared", token, transaction);
            await using var db = Context(connection);
            await db.Database.UseTransactionAsync(transaction, token);
            if (AfterProtocolTransaction is { } bound) await bound(connection, token);
            if (await db.IndexRecoveryAudit.Where(value => value.Id == 1).ExecuteUpdateAsync(setters =>
                setters.SetProperty(value => value.IsValid, false).SetProperty(value => value.FailurePath, (string?)null), token) != 1)
                throw new InvalidOperationException("index-recovery-audit-state-missing");
            var id = Guid.NewGuid();
            db.IndexRecoveryWriters.Add(new() { Id = id, RecoveryRoot = root, OwnerProcessId = owner.ProcessId,
                OwnerStartedAtUtc = owner.StartedAtUtc, OwnerMachineFingerprint = owner.MachineFingerprint,
                OwnerExitSufficient = ownerExitSufficient, CreatedAtUtc = clock.GetUtcNow() });
            if (BeforeProtocolSave is { } saving) await saving(connection, token);
            await db.SaveChangesAsync(token);
            await transaction.CommitAsync(token);
            return new(connection, id);
        }
        catch { await connection.DisposeAsync(); throw; }
    }

    public async ValueTask<RecoveryIndexAudit> AuditAsync(string recoveryRoot, string indexRoot, CancellationToken token)
    {
        var root = Canonical(recoveryRoot);
        await using var connection = await OpenSessionAsync("Exclusive", token);
        await using (var invalidation = Context(connection))
            await invalidation.IndexRecoveryAudit.Where(value => value.Id == 1).ExecuteUpdateAsync(setters =>
                setters.SetProperty(value => value.IsValid, false).SetProperty(value => value.FailurePath, (string?)null), token);
        try
        {
            await using var db = Context(connection);
            var unfinished = await db.IndexRecoveryWriters.AsNoTracking().OrderBy(value => value.Id).Take(1025).ToArrayAsync(token);
            if (unfinished.Length > 1024) throw new InvalidOperationException("index-recovery-writer-audit-budget");
            foreach (var writer in unfinished)
            {
                if (!string.Equals(writer.RecoveryRoot, root, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("index-recovery-writer-root-mismatch");
                if (owners.Observe(new(writer.OwnerProcessId, writer.OwnerStartedAtUtc, writer.OwnerMachineFingerprint)) != GpuInteractiveOwnerObservation.Exited)
                    throw new InvalidOperationException("index-recovery-writer-exit-not-proven");
                if (!writer.OwnerExitSufficient)
                    throw new InvalidOperationException("index-recovery-writer-reconciliation-required");
            }
            var captured = new RecoveryIndexReferenceAudit(indexRoot).Capture(root, token);
            if (captured.Pins.Count > 8192) throw new RecoveryIndexAuditException("index-recovery-audit-budget", ".");
            if (AfterCapture is { } pause) await pause(token);
            await RequireSessionAsync(connection, "Exclusive", token);
            if (BeforeProtocolTransaction is { } before) await before(connection, token);
            await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable, token);
            await RequireSessionAsync(connection, "Exclusive", token, transaction);
            await db.Database.UseTransactionAsync(transaction, token);
            if (AfterProtocolTransaction is { } bound) await bound(connection, token);
            var audit = await db.IndexRecoveryAudit.SingleAsync(value => value.Id == 1, token);
            audit.IsValid = true; audit.ClassifierVersion = RecoveryIndexReferenceAudit.ClassifierVersion;
            audit.DatabaseIncarnation = await DatabaseIdentityAsync(db, token);
            audit.RecoveryRoot = root; audit.IndexRoot = Canonical(indexRoot); audit.RootFingerprint = captured.RootFingerprint;
            audit.InventoryChecksum = captured.InventoryChecksum; audit.SealedAtUtc = clock.GetUtcNow(); audit.FailurePath = null;
            await db.IndexRecoveryPins.ExecuteDeleteAsync(token);
            foreach (var pin in captured.Pins.Distinct())
                db.IndexRecoveryPins.Add(new() { Id = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
                    pin.Packet + "\n" + pin.GenerationId + "\n" + pin.IndexPath))), Packet = pin.Packet, GenerationId = pin.GenerationId, IndexPath = pin.IndexPath });
            var exited = unfinished.Select(value => value.Id).ToArray();
            if (exited.Length != 0) await db.IndexRecoveryWriters.Where(value => exited.Contains(value.Id)).ExecuteDeleteAsync(token);
            if (BeforeProtocolSave is { } saving) await saving(connection, token);
            await db.SaveChangesAsync(token);
            await transaction.CommitAsync(token);
            // Commit does not release a Session-owned application lock. A checked
            // server acknowledgement makes the seal available before disconnect.
            await RequireSessionAsync(connection, "Exclusive", CancellationToken.None);
            await using (var release = connection.CreateCommand())
            {
                release.CommandTimeout = 5;
                release.CommandText = "DECLARE @r int; EXEC @r=sys.sp_releaseapplock @Resource=N'FluxKnowledge.RecoveryReferences',@LockOwner=N'Session'; IF @r<0 OR ISNULL(APPLOCK_MODE(N'public',N'FluxKnowledge.RecoveryReferences',N'Session'),N'NoLock')<>N'NoLock' THROW 51000,'index-recovery-audit-lock-release-unconfirmed',1;";
                await release.ExecuteNonQueryAsync(CancellationToken.None);
            }
            if (AfterAuditCommit is { } committed) await committed(connection, token);
            return captured;
        }
        catch (RecoveryIndexAuditException exception)
        {
            try
            {
                await using var failure = Context(connection);
                await failure.IndexRecoveryAudit.Where(value => value.Id == 1).ExecuteUpdateAsync(setters =>
                    setters.SetProperty(value => value.IsValid, false).SetProperty(value => value.FailurePath,
                        exception.RelativePath.Length <= 2048 ? exception.RelativePath : "."), token);
            }
            catch (Exception) { /* The previously committed invalidation remains authoritative. */ }
            throw;
        }
    }

    internal static async Task AcquireReadTransactionAsync(FluxKnowledgeDbContext context, CancellationToken token)
    {
        var transaction = context.Database.CurrentTransaction ?? throw new InvalidOperationException("index-recovery-audit-transaction-required");
        if (transaction.GetDbTransaction().IsolationLevel != IsolationLevel.Serializable)
            throw new InvalidOperationException("index-recovery-audit-serializable-required");
        await using var command = new SqlCommand("""
            DECLARE @result int;
            EXEC @result=sys.sp_getapplock @Resource=@resource,@LockMode=N'Shared',@LockOwner=N'Transaction',@LockTimeout=0;
            SELECT @result;
            """, (SqlConnection)context.Database.GetDbConnection(), (SqlTransaction)transaction.GetDbTransaction());
        command.Parameters.Add("@resource", SqlDbType.NVarChar, 255).Value = LockResource;
        if (Convert.ToInt32(await command.ExecuteScalarAsync(token)) < 0)
            throw new InvalidOperationException("segment-retirement-recovery-reference-owned");
    }

    internal static async Task RequireCurrentAsync(FluxKnowledgeDbContext context, string recoveryRoot, string indexRoot, CancellationToken token)
    {
        await AcquireReadTransactionAsync(context, token);
        var audit = await context.IndexRecoveryAudit.AsNoTracking().SingleOrDefaultAsync(value => value.Id == 1, token);
        if (audit is null || !audit.IsValid || audit.ClassifierVersion != RecoveryIndexReferenceAudit.ClassifierVersion ||
            audit.SealedAtUtc is null || audit.InventoryChecksum.Length != 64 || string.IsNullOrWhiteSpace(audit.RootFingerprint) ||
            !string.Equals(audit.RecoveryRoot, Canonical(recoveryRoot), StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(audit.IndexRoot, Canonical(indexRoot), StringComparison.OrdinalIgnoreCase) ||
            audit.DatabaseIncarnation != await DatabaseIdentityAsync(context, token))
            throw new InvalidOperationException("segment-retirement-recovery-reference-audit-required");
    }

    private async Task<SqlConnection> OpenSessionAsync(string mode, CancellationToken token)
    {
        await using var configured = await factory.CreateDbContextAsync(token);
        var builder = new SqlConnectionStringBuilder(configured.Database.GetConnectionString())
            { Pooling = false, Enlist = false, ConnectRetryCount = 0, ConnectTimeout = 5, ApplicationName = "FluxKnowledge.RecoveryReferences" };
        var connection = new SqlConnection(builder.ConnectionString);
        try
        {
            await connection.OpenAsync(token);
            await using var command = connection.CreateCommand(); command.CommandTimeout = 10;
            command.CommandText = """
                DECLARE @result int;
                EXEC @result=sys.sp_getapplock @Resource=@resource,@LockMode=@mode,@LockOwner=N'Session',@LockTimeout=5000;
                SELECT @result;
                """;
            command.Parameters.Add("@resource", SqlDbType.NVarChar, 255).Value = LockResource;
            command.Parameters.Add("@mode", SqlDbType.NVarChar, 32).Value = mode;
            if (Convert.ToInt32(await command.ExecuteScalarAsync(token)) < 0) throw new InvalidOperationException("index-recovery-reference-busy");
            return connection;
        }
        catch (SqlException exception) when (token.IsCancellationRequested)
        { await connection.DisposeAsync(); throw new OperationCanceledException("Recovery reference admission was cancelled.", exception, token); }
        catch { await connection.DisposeAsync(); throw; }
    }
    private static FluxKnowledgeDbContext Context(SqlConnection connection) => new(new DbContextOptionsBuilder<FluxKnowledgeDbContext>()
        .UseSqlServer(connection, contextOwnsConnection: false).AddInterceptors(ExistingSessionOnly.Instance).Options);
    // These contexts receive the already-open, lock-owning session. EF reopening
    // it would discard the exclusion on which the captured filesystem proof relies.
    private sealed class ExistingSessionOnly : DbConnectionInterceptor
    {
        internal static readonly ExistingSessionOnly Instance = new();
        public override InterceptionResult ConnectionOpening(DbConnection connection, ConnectionEventData eventData, InterceptionResult result) =>
            throw new InvalidOperationException("index-recovery-reference-session-lost");
        public override ValueTask<InterceptionResult> ConnectionOpeningAsync(DbConnection connection, ConnectionEventData eventData,
            InterceptionResult result, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("index-recovery-reference-session-lost");
    }
    private static string Canonical(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    private static async Task RequireSessionAsync(SqlConnection connection, string mode, CancellationToken token, SqlTransaction? transaction = null)
    {
        await using var command = connection.CreateCommand(); command.CommandTimeout = 5;
        command.Transaction = transaction;
        command.CommandText = "SELECT APPLOCK_MODE(N'public',@resource,N'Session');";
        command.Parameters.Add("@resource", SqlDbType.NVarChar, 255).Value = LockResource;
        if (!string.Equals(await command.ExecuteScalarAsync(token) as string, mode, StringComparison.Ordinal))
            throw new InvalidOperationException("index-recovery-reference-session-lost");
    }
    private static async Task<Guid> DatabaseIdentityAsync(FluxKnowledgeDbContext db, CancellationToken token)
    {
        await using var command = new SqlCommand("SELECT database_guid FROM sys.database_recovery_status WHERE database_id=DB_ID();",
            (SqlConnection)db.Database.GetDbConnection(), (SqlTransaction?)db.Database.CurrentTransaction?.GetDbTransaction());
        return await command.ExecuteScalarAsync(token) is Guid id && id != Guid.Empty ? id :
            throw new InvalidOperationException("index-recovery-database-identity-unavailable");
    }

    public sealed class RecoveryReferenceWriter : IAsyncDisposable
    {
        private readonly SqlConnection _connection;
        private readonly Guid _id;
        private readonly SemaphoreSlim _gate = new(1);
        private bool _completed, _disposed;
        internal RecoveryReferenceWriter(SqlConnection connection, Guid id) { _connection = connection; _id = id; }
        public async ValueTask CompleteAsync(CancellationToken token)
        {
            await _gate.WaitAsync(token);
            try
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_completed) return;
                await RequireSessionAsync(_connection, "Shared", token);
                await using var command = _connection.CreateCommand(); command.CommandTimeout = 5;
                command.CommandText = "DELETE FROM [dbo].[IndexRecoveryWriters] WHERE [Id]=@id;";
                command.Parameters.Add("@id", SqlDbType.UniqueIdentifier).Value = _id;
                if (await command.ExecuteNonQueryAsync(token) != 1) throw new InvalidOperationException("index-recovery-writer-binding-changed");
                _completed = true;
            }
            finally { _gate.Release(); }
        }
        public async ValueTask DisposeAsync()
        {
            await _gate.WaitAsync();
            try { if (_disposed) return; _disposed = true; await _connection.DisposeAsync(); }
            finally { _gate.Release(); }
        }
    }
}
