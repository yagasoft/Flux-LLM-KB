using FluxKnowledge.Web.Components.Sources;
using Xunit;

namespace FluxKnowledge.Web.Tests.Components;

public sealed class SourceJournalAllowlistHandoffTests
{
    [Theory]
    [InlineData("http://localhost:5158", "http://localhost:5158/review")]
    [InlineData("http://localhost:5158/", "http://localhost:5158/review")]
    public void Operator_review_uses_the_configured_literal_loopback_authority(string authority, string expected)
    {
        Assert.Equal(expected, new SourceJournalAllowlistHandoff(authority).ReviewUrl);
    }

    [Theory]
    [InlineData("https://127.0.0.1:5158")]
    [InlineData("http://127.0.0.1:5137")]
    [InlineData("http://127.0.0.1:80")]
    [InlineData("http://127.0.0.1:5158")]
    [InlineData("http://127.0.0.1:5158/elsewhere")]
    [InlineData("http://127.0.0.1:5158?query")]
    [InlineData("http://user@127.0.0.1:5158")]
    [InlineData("http://127.0.0.1:5158/#fragment")]
    [InlineData("https://example.org:5158")]
    [InlineData("file:///I:/operator")]
    public void Invalid_operator_binding_cannot_receive_a_proposal(string authority)
    {
        Assert.Throws<InvalidOperationException>(() => new SourceJournalAllowlistHandoff(authority));
    }

    [Fact]
    public void Missing_operator_binding_keeps_the_existing_download_available()
    {
        Assert.Null(new SourceJournalAllowlistHandoff(null).ReviewUrl);
    }

    [Fact]
    public void Impact_bytes_bind_the_display_to_the_source_state()
    {
        const string volume = @"\\?\VOLUME{11111111-1111-1111-1111-111111111111}\";
        var source = new JournalSourceRoot(Guid.NewGuid(), "Workspace", @"I:\Lab", "Enabled");
        JournalAllowlistProposal proposal = new([volume], [], [new(source, volume, true)], [], "ignored", "ignored");
        var first = SourceJournalAllowlistHandoff.BuildImpact(proposal);
        var bytes = Convert.FromBase64String(first.Base64);
        Assert.Equal(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)), first.Sha256);
        var rows = System.Text.Json.JsonSerializer.Deserialize<string[]>(bytes)!;
        var display = Assert.Single(rows, row => !row.StartsWith(SourceJournalAllowlistHandoff.ImpactDigestPrefix, StringComparison.Ordinal));
        Assert.Contains(@"I:\Lab", display);
        Assert.Contains("Enabled", display);
        var changed = proposal with { Impacts = [new(source with { State = "Paused" }, volume, true)] };
        Assert.NotEqual(first.Sha256, SourceJournalAllowlistHandoff.BuildImpact(changed).Sha256);
        var replacement = proposal with { Impacts = [new(source with { Id = Guid.NewGuid() }, volume, true)] };
        Assert.NotEqual(first.Sha256, SourceJournalAllowlistHandoff.BuildImpact(replacement).Sha256);
    }

    [Fact]
    public void Large_impact_remains_submittable_and_binds_every_source()
    {
        const string volume = @"\\?\VOLUME{11111111-1111-1111-1111-111111111111}\";
        var sources = Enumerable.Range(0, 200).Select(i => new JournalRootImpact(
            new JournalSourceRoot(Guid.Parse($"10000000-0000-0000-0000-{i:X12}"), $"Workspace {i} {new string('x', 120)}", $@"I:\Lab\{i}", "Enabled"), volume, true)).ToArray();
        JournalAllowlistProposal proposal = new([volume], [], sources, [], "ignored", "ignored");
        var first = SourceJournalAllowlistHandoff.BuildImpact(proposal);
        var bytes = Convert.FromBase64String(first.Base64);
        Assert.InRange(bytes.Length, 2, 12 * 1024);
        var rows = System.Text.Json.JsonSerializer.Deserialize<string[]>(bytes)!;
        Assert.InRange(rows.Length, 1, 128);
        Assert.Contains(rows, row => row.Contains("more sources", StringComparison.Ordinal));

        Assert.DoesNotContain(rows, row => row.Contains(sources[^1].Root.Id.ToString("D"), StringComparison.Ordinal));
        sources[^1] = sources[^1] with { Root = sources[^1].Root with { StoredVolume = volume } };
        Assert.NotEqual(first.Sha256, SourceJournalAllowlistHandoff.BuildImpact(proposal).Sha256);
    }
}
