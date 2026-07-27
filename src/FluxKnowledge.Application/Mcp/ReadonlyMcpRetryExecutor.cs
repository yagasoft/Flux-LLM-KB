namespace FluxKnowledge.Application.Mcp;

public sealed class ReadonlyMcpRetryExecutor(
    TimeSpan? firstRetryDelay = null,
    TimeSpan? secondRetryDelay = null)
{
    private readonly TimeSpan _firstRetryDelay = firstRetryDelay ?? TimeSpan.FromMilliseconds(200);
    private readonly TimeSpan _secondRetryDelay = secondRetryDelay ?? TimeSpan.FromMilliseconds(800);

    public async Task<ReadonlyMcpExecutionResult<T>> ExecuteAsync<T>(
        string toolName,
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolName);
        ArgumentNullException.ThrowIfNull(operation);

        Exception? failure = null;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                return new ReadonlyMcpExecutionResult<T>(
                    await operation(cancellationToken).ConfigureAwait(false),
                    null);
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                failure = exception;
                if (!McpTransientFailureClassifier.IsTransient(exception) || attempt == 2)
                {
                    break;
                }

                await Task.Delay(
                    attempt == 0 ? _firstRetryDelay : _secondRetryDelay,
                    cancellationToken).ConfigureAwait(false);
            }
        }

        return new ReadonlyMcpExecutionResult<T>(default, failure!);
    }
}

public sealed record ReadonlyMcpExecutionResult<T>(T? Value, Exception? Failure)
{
    public bool Succeeded => Failure is null;
}
