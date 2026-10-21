using FluxKnowledge.Application.Operations;
using FluxKnowledge.Cli.Commands;
using FluxKnowledge.Infrastructure.Usearch;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Cli;

public sealed class IndexProjectionCommandBoundaryTests
{
    [Theory]
    [InlineData("run")]
    [InlineData("abandon")]
    public async Task Standalone_production_mutation_refuses_before_any_database_or_path_access(string action)
    {
        using var output = new StringWriter(); using var error = new StringWriter();
        var exit = await IndexProjectionCommand.ExecuteAsync([action, "--plan", "must-not-read.json", "--sha256", new string('a', 64)],
            null!, new UsearchIndexOptions(LiveRootLayout.Production.IndexRoot), output, error);
        Assert.Equal(1, exit);
        Assert.Empty(output.ToString());
        Assert.Equal("index-projection-production-requires-coordinated-updater", error.ToString().Trim());
    }
}
