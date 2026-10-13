namespace FluxKnowledge.Application.Ports;

/// <summary>Waits for a read-only database probe before the first durable background operation.</summary>
public interface IDatabaseStartupReadiness
{
    bool IsReady { get; }
    ValueTask WaitAsync(CancellationToken cancellationToken);
}

/// <summary>Emitted only at a known read-only startup boundary, before durable work.</summary>
public sealed class DatabaseStartupUnavailableException(int sqlErrorNumber, Exception? innerException = null)
    : Exception("database-startup-unavailable", innerException)
{
    public int SqlErrorNumber { get; } = sqlErrorNumber;
}
