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
}
