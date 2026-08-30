using System.Text;
using FluxKnowledge.Web.Configuration;
using FluxKnowledge.Web;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace FluxKnowledge.Web.Tests.Configuration;

public sealed class NoFollowJsonConfigurationProviderTests
{
    private const string CanonicalProductionPath = @"I:\FluxKnowledge\Config\appsettings.Production.json";

    [Fact]
    public void Production_provider_rejects_reparse_config_before_opening_json()
    {
        var opener = new RecordingNoFollowPathOpener(reparseAt: @"I:\FluxKnowledge\Config");

        Assert.Throws<InvalidOperationException>(() =>
            NoFollowJsonConfigurationProvider.LoadCanonicalProduction(CanonicalProductionPath, opener));

        Assert.Equal(0, opener.FileOpenCount);
    }

    [Fact]
    public void Production_provider_reads_json_only_after_the_canonical_config_directory_is_validated()
    {
        var opener = new RecordingNoFollowPathOpener(json: "{\"Runtime\":{\"GpuEnabled\":false}}");

        var configuration = NoFollowJsonConfigurationProvider.LoadCanonicalProduction(
            CanonicalProductionPath,
            opener);

        Assert.Equal("False", configuration["Runtime:GpuEnabled"]);
        Assert.Equal(1, opener.FileOpenCount);
        Assert.Equal([@"I:\FluxKnowledge\Config"], opener.ValidatedDirectories);
    }

    [Fact]
    public void Production_composition_loads_its_configuration_through_the_no_follow_provider()
    {
        var opener = new RecordingNoFollowPathOpener(json: "{\"ConnectionStrings\":{\"FluxKnowledge\":\"canonical\"}}");

        var configuration = WebHostComposition.LoadCanonicalProductionConfiguration(opener);

        Assert.Equal("canonical", configuration["ConnectionStrings:FluxKnowledge"]);
        Assert.Equal(1, opener.FileOpenCount);
    }

    private sealed class RecordingNoFollowPathOpener(string? reparseAt = null, string? json = null)
        : INoFollowPathOpener
    {
        public int FileOpenCount { get; private set; }
        public List<string> ValidatedDirectories { get; } = [];

        public Stream OpenRead(string canonicalPath)
        {
            FileOpenCount++;
            return new MemoryStream(Encoding.UTF8.GetBytes(json ?? "{}"));
        }

        public string ValidateDirectory(string canonicalPath)
        {
            ValidatedDirectories.Add(canonicalPath);
            if (string.Equals(canonicalPath, reparseAt, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("A reparse point is not allowed.");
            }

            return canonicalPath;
        }
    }
}
