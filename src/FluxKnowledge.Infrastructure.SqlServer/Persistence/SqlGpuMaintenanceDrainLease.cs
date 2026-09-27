using System.Data;
using FluxKnowledge.Application.Gpu;
using FluxKnowledge.Domain.Gpu;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence;

/// <summary>Operator-owned admission fence. Existing execution and release continue;
/// this lease never changes a reservation or infers release from elapsed time.</summary>
public sealed class SqlGpuMaintenanceDrainLease : IAsyncDisposable
{
    private const string AdmissionResource = "FluxKnowledge.GpuScheduler.Admission";
    private readonly SqlConnection _connection;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _disposed;
    private SqlGpuMaintenanceDrainLease(SqlConnection connection, int sessionId)
    { _connection = connection; SessionId = sessionId; }
    public int SessionId { get; }

    public static async ValueTask<SqlGpuMaintenanceDrainLease> AcquireAsync(
        IDbContextFactory<FluxKnowledgeDbContext> factory, CancellationToken ct)
    {
        await using var context = await factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var builder = new SqlConnectionStringBuilder(context.Database.GetDbConnection().ConnectionString)
        {
            Pooling = false, ConnectRetryCount = 0, ApplicationName = "FluxKnowledge.GpuMaintenanceDrain"
        };
        var connection = new SqlConnection(builder.ConnectionString);
        try
        {
            await connection.OpenAsync(ct).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                DECLARE @result int;
                EXEC @result = sp_getapplock @Resource = @resource, @LockMode = N'Exclusive',
                     @LockOwner = N'Session', @LockTimeout = 10000;
                IF @result < 0 THROW 51000, 'gpu-maintenance-admission-fence-unavailable', 1;
                SELECT @@SPID;
                """;
            command.Parameters.Add("@resource", SqlDbType.NVarChar, 255).Value = AdmissionResource;
            var sessionId = Convert.ToInt32(await command.ExecuteScalarAsync(ct).ConfigureAwait(false));
            return new(connection, sessionId);
        }
        catch { await connection.DisposeAsync().ConfigureAwait(false); throw; }
    }

    public async ValueTask<bool> IsDrainedAsync(string requiredSlotKey, CancellationToken ct)
    {
        GpuSchedulerOpaqueKeyValidator.RequireCanonical(requiredSlotKey, nameof(requiredSlotKey), 256);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_connection.State != ConnectionState.Open) throw new InvalidOperationException("gpu-maintenance-admission-fence-lost");
            await using var command = _connection.CreateCommand();
            command.CommandText = """
                IF ISNULL(APPLOCK_MODE(N'public', @resource, N'Session'), N'NoLock') <> N'Exclusive'
                    THROW 51000, 'gpu-maintenance-admission-fence-lost', 1;
                SELECT CONVERT(bit, CASE WHEN
                    EXISTS (SELECT 1 FROM GpuCapacitySlots WHERE SlotKey COLLATE Latin1_General_100_BIN2 = @slot)
                    AND NOT EXISTS (SELECT 1 FROM GpuCapacitySlots WHERE State <> @available OR ActiveBatchId IS NOT NULL)
                    AND NOT EXISTS (SELECT 1 FROM GpuMiniTasks WHERE State = @active)
                    THEN 1 ELSE 0 END);
                """;
            command.Parameters.Add("@resource", SqlDbType.NVarChar, 255).Value = AdmissionResource;
            command.Parameters.Add("@slot", SqlDbType.NVarChar, 256).Value = requiredSlotKey;
            command.Parameters.Add("@available", SqlDbType.Int).Value = (int)GpuCapacitySlotState.Available;
            command.Parameters.Add("@active", SqlDbType.Int).Value = (int)GpuMiniTaskExecutionState.Active;
            return Convert.ToBoolean(await command.ExecuteScalarAsync(ct).ConfigureAwait(false));
        }
        finally { _gate.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed) return;
            _disposed = true;
            // Unpooled disposal ends the owning session and its application lock.
            await _connection.DisposeAsync().ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    // Reset SQL uses the very session holding admission; it cannot continue on a
    // second/reconnected connection after the reservation fence has been lost.
    internal async ValueTask<T> WithHeldAdmissionAsync<T>(Func<SqlConnection, CancellationToken, Task<T>> operation, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_connection.State != ConnectionState.Open) throw new InvalidOperationException("gpu-maintenance-admission-fence-lost");
            await using var command = _connection.CreateCommand();
            command.CommandText = """
                IF ISNULL(APPLOCK_MODE(N'public', @resource, N'Session'), N'NoLock') <> N'Exclusive'
                    THROW 51000, 'gpu-maintenance-admission-fence-lost', 1;
                """;
            command.Parameters.Add("@resource", SqlDbType.NVarChar, 255).Value = AdmissionResource;
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            return await operation(_connection, ct).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }
}
