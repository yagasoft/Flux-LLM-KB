using FluxKnowledge.Application.Ports;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence;

/// <summary>Shares a cancellable read-only startup probe; never retries a worker or transaction.</summary>
public sealed class SqlDatabaseStartupReadiness : IDatabaseStartupReadiness
{
    private readonly object _sync = new();
    private readonly Func<CancellationToken, Task> _probe;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly CancellationToken _stoppingToken;
    private readonly ILogger<SqlDatabaseStartupReadiness> _logger;
    private Task? _ready;

    public SqlDatabaseStartupReadiness(IDbContextFactory<FluxKnowledgeDbContext> factory,
        TimeProvider clock, ILogger<SqlDatabaseStartupReadiness> logger, IHostApplicationLifetime? lifetime = null,
        bool? segmentedPublicationEnabled = null)
        : this(ct => ProbeOnceAsync(factory, ct, segmentedPublicationEnabled), (delay, ct) => Task.Delay(delay, clock, ct),
            lifetime?.ApplicationStopping ?? CancellationToken.None, logger) { }

    internal SqlDatabaseStartupReadiness(Func<CancellationToken, Task> probe,
        Func<TimeSpan, CancellationToken, Task> delay, CancellationToken stoppingToken,
        ILogger<SqlDatabaseStartupReadiness> logger)
    {
        _probe = probe;
        _delay = delay;
        _stoppingToken = stoppingToken;
        _logger = logger;
    }

    public bool IsReady { get { lock (_sync) return _ready?.IsCompletedSuccessfully == true; } }

    public ValueTask WaitAsync(CancellationToken cancellationToken)
    {
        Task ready;
        lock (_sync) ready = _ready ??= Task.Run(WaitForDatabaseAsync, CancellationToken.None);
        return new(ready.WaitAsync(cancellationToken));
    }

    private async Task WaitForDatabaseAsync()
    {
        var delay = TimeSpan.FromSeconds(1);
        int? lastFailure = null;
        while (true)
        {
            _stoppingToken.ThrowIfCancellationRequested();
            try
            {
                await _probe(_stoppingToken).ConfigureAwait(false);
                _logger.LogInformation("SQL startup database probe succeeded. Process {ProcessId}.", Environment.ProcessId);
                return;
            }
            catch (DatabaseStartupUnavailableException failure)
            {
                if (lastFailure != failure.SqlErrorNumber)
                    _logger.LogWarning("SQL startup is waiting for the configured database after availability error {SqlErrorNumber}. Process {ProcessId}; durable work has not started.",
                        failure.SqlErrorNumber, Environment.ProcessId);
                lastFailure = failure.SqlErrorNumber;
                await _delay(delay, _stoppingToken).ConfigureAwait(false);
                delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, 15));
            }
            catch (InvalidOperationException failure) when (failure.Message is
                "index-startup-required-format-unknown" or "index-startup-segmented-publication-disabled")
            {
                _logger.LogError("SQL startup refused index compatibility: {ReasonCode}. Process {ProcessId}; durable work has not started.",
                    failure.Message, Environment.ProcessId);
                throw;
            }
            catch (Exception failure) when (failure is not OperationCanceledException)
            {
                _logger.LogError("SQL startup failed closed after {FailureType}. Process {ProcessId}.",
                    failure.GetType().Name, Environment.ProcessId);
                throw;
            }
        }
    }

    internal static bool IsAvailabilityFailure(SqlException failure) => failure.Errors.Count > 0 &&
        failure.Errors.Cast<SqlError>().All(error => error.Number is
            -2 or 2 or 53 or 64 or 233 or 258 or 4060 or 10053 or 10054 or 10060 or 10061 or 17142 or 18401);

    public static async Task ProbeOnceAsync(IDbContextFactory<FluxKnowledgeDbContext> factory, CancellationToken ct,
        bool? segmentedPublicationEnabled = null)
    {
        await using var context = await factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var connection = new SqlConnectionStringBuilder(context.Database.GetConnectionString())
        {
            ConnectTimeout = 2, ConnectRetryCount = 0, Pooling = false, Enlist = false
        };
        try
        {
            await using var sql = new SqlConnection(connection.ConnectionString);
            await sql.OpenAsync(ct).ConfigureAwait(false);
            await using var command = new SqlCommand("""
                IF COL_LENGTH(N'dbo.IndexState', N'RequiredIndexFormat') IS NULL
                    SELECT CONVERT(int,1) FROM [dbo].[IndexState] WHERE [Id]=1;
                ELSE
                    EXEC sys.sp_executesql N'SELECT [RequiredIndexFormat] FROM [dbo].[IndexState] WHERE [Id]=1;';
                """, sql) { CommandTimeout = 2 };
            if (await command.ExecuteScalarAsync(ct).ConfigureAwait(false) is not int value || value is not (1 or 2))
                throw new InvalidOperationException("index-startup-required-format-unknown");
            if (value == 2 && segmentedPublicationEnabled == false)
                throw new InvalidOperationException("index-startup-segmented-publication-disabled");
        }
        catch (SqlException) when (ct.IsCancellationRequested)
        {
            throw new OperationCanceledException(ct);
        }
        catch (SqlException failure) when (IsAvailabilityFailure(failure))
        {
            throw new DatabaseStartupUnavailableException(failure.Number, failure);
        }
    }
}
