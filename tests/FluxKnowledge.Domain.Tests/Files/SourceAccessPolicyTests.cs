using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using FluxKnowledge.Integrations.Files;
using Xunit;

namespace FluxKnowledge.Domain.Tests.Files;

[SupportedOSPlatform("windows")]
public sealed class SourceAccessPolicyTests
{
    private static SourceAccessPolicy Valid() => new(1, "FluxKnowledge.SourceAccess.test", "S-1-5-21-1-2-3-1001",
        [new(@"I:\authorised-source", new string('a', 64))], [@"I:\private-storage"]);

    [Fact]
    public void Closed_policy_preserves_the_approved_root_identity_and_indexer()
    {
        var policy = Valid();
        var parsed = SourceAccessPolicy.Parse(JsonSerializer.SerializeToUtf8Bytes(policy));
        Assert.Equal(policy.IndexerSid, parsed.IndexerSid);
        Assert.Equal(Assert.Single(policy.Roots), Assert.Single(parsed.Roots));
    }

    [Fact]
    public void Duplicate_unknown_or_missing_fields_are_refused()
    {
        var json = JsonSerializer.Serialize(Valid());
        foreach (var invalid in new[] { json.Replace("\"Version\":1", "\"Version\":1,\"Version\":1", StringComparison.Ordinal),
                     json.Replace("\"Version\":1", "\"Version\":1,\"ResetAllAcls\":true", StringComparison.Ordinal),
                     json.Replace("\"Version\":1,", "", StringComparison.Ordinal) })
        {
            var failure = Record.Exception(() => SourceAccessPolicy.Parse(Encoding.UTF8.GetBytes(invalid)));
            Assert.True(failure is InvalidDataException or JsonException);
        }
    }

    [Theory]
    [InlineData("version")]
    [InlineData("sid")]
    [InlineData("fingerprint")]
    [InlineData("duplicate-root")]
    [InlineData("empty-roots")]
    [InlineData("relative-root")]
    [InlineData("protected-overlap")]
    public void Invalid_or_conflicting_authority_is_refused(string error)
    {
        var policy = Valid();
        policy = error switch
        {
            "version" => policy with { Version = 2 },
            "sid" => policy with { IndexerSid = "not-a-sid" },
            "fingerprint" => policy with { Roots = [policy.Roots[0] with { IdentityFingerprint = "not-an-identity" }] },
            "duplicate-root" => policy with { Roots = [policy.Roots[0], policy.Roots[0] with { Path = @"I:\AUTHORISED-SOURCE" }] },
            "empty-roots" => policy with { Roots = [] },
            "relative-root" => policy with { Roots = [policy.Roots[0] with { Path = "relative" }] },
            _ => policy with { ProtectedRoots = [@"I:\authorised-source\secrets"] }
        };
        var failure = Record.Exception(() => SourceAccessPolicy.Parse(JsonSerializer.SerializeToUtf8Bytes(policy)));
        Assert.True(failure is InvalidDataException or JsonException);
    }
}
