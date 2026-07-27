using FluxKnowledge.Application.Mcp;
using Microsoft.AspNetCore.Connections;
using Xunit;

namespace FluxKnowledge.Domain.Tests.Mcp;

public sealed class ReadonlyMcpRetryExecutorTests
{
    [Fact]
    public async Task Read_only_search_recreates_its_operation_three_times_after_transient_failures()
    {
        var attempts = 0;
        var executor = new ReadonlyMcpRetryExecutor(TimeSpan.Zero, TimeSpan.Zero);

        var result = await executor.ExecuteAsync(
            "kb.search",
            _ =>
            {
                attempts++;
                if (attempts < 3) throw new ConnectionResetException("backend connection reset");
                return Task.FromResult("recovered");
            },
            CancellationToken.None);

        Assert.Equal(3, attempts);
        Assert.Equal("recovered", result.Value);
    }

    [Fact]
    public async Task Permanent_io_failure_is_attempted_once()
    {
        var attempts = 0;
        var executor = new ReadonlyMcpRetryExecutor(TimeSpan.Zero, TimeSpan.Zero);

        var result = await executor.ExecuteAsync<string>(
            "kb.search",
            _ =>
            {
                attempts++;
                throw new FileNotFoundException("index file is permanently missing");
            },
            CancellationToken.None);

        Assert.Equal(1, attempts);
        Assert.IsType<FileNotFoundException>(result.Failure);
    }
}
