using FluxKnowledge.Cli.Commands;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace FluxKnowledge.Web.Tests.Composition;

public sealed class WebProgramIdentityTests
{
    [Fact]
    public void Web_test_host_marker_resolves_to_the_Web_assembly_when_Cli_is_a_transitive_reference()
    {
        using var factory = new WebApplicationFactory<Program>();

        Assert.Equal("FluxKnowledge.Web", typeof(Program).Assembly.GetName().Name);
        Assert.Equal("FluxKnowledge.Cli", typeof(LocalRetainedCsharpCodeCommand).Assembly.GetName().Name);
    }
}
