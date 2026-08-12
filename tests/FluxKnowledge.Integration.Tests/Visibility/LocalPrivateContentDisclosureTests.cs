using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Visibility;

public sealed class LocalPrivateContentDisclosureTests
{
    [Fact]
    public void Local_disclosure_withholds_a_synthetic_secret_sentinel_with_a_fixed_reason()
    {
        var implementationType = typeof(FluxKnowledgeDbContext).Assembly.GetType(
            "FluxKnowledge.Infrastructure.SqlServer.Visibility.LocalPrivateContentDisclosure");
        Assert.NotNull(implementationType);

        var disclosure = Activator.CreateInstance(implementationType!);
        var evaluate = implementationType!.GetMethod("Evaluate");
        Assert.NotNull(evaluate);

        var result = evaluate!.Invoke(disclosure, ["secret-content-sentinel", 0]);
        Assert.NotNull(result);
        var resultType = result!.GetType();

        Assert.Null(resultType.GetProperty("Value")!.GetValue(result));
        Assert.True((bool)resultType.GetProperty("Withheld")!.GetValue(result)!);
        Assert.Equal("secret-content-withheld", resultType.GetProperty("ReasonCode")!.GetValue(result));
    }

    [Theory]
    [InlineData("-----BEGIN RSA PRIVATE KEY-----\nsynthetic-key-material\n-----END RSA PRIVATE KEY-----")]
    [InlineData("-----BEGIN EC PRIVATE KEY-----\nsynthetic-key-material\n-----END EC PRIVATE KEY-----")]
    [InlineData("-----BEGIN OPENSSH PRIVATE KEY-----\nsynthetic-key-material\n-----END OPENSSH PRIVATE KEY-----")]
    [InlineData("-----BEGIN ENCRYPTED PRIVATE KEY-----\nsynthetic-key-material\n-----END ENCRYPTED PRIVATE KEY-----")]
    [InlineData("-----BEGIN PGP PRIVATE KEY BLOCK-----\nsynthetic-key-material\n-----END PGP PRIVATE KEY BLOCK-----")]
    [InlineData("postgresql://synthetic-user:synthetic-password@127.0.0.1/synthetic")]
    [InlineData("https://synthetic-user:synthetic-password@localhost/synthetic")]
    public void Local_disclosure_withholds_standard_private_key_envelopes_and_credential_uris(string value)
    {
        var result = Evaluate(value);

        Assert.Null(result.Value);
        Assert.True(result.Withheld);
        Assert.Equal("secret-content-withheld", result.ReasonCode);
    }

    [Fact]
    public void Local_disclosure_returns_clean_retained_derived_detail_unchanged()
    {
        var implementationType = typeof(FluxKnowledgeDbContext).Assembly.GetType(
            "FluxKnowledge.Infrastructure.SqlServer.Visibility.LocalPrivateContentDisclosure");
        Assert.NotNull(implementationType);

        var result = implementationType!.GetMethod("Evaluate")!.Invoke(
            Activator.CreateInstance(implementationType), ["namespace Flux;", 0]);

        Assert.NotNull(result);
        var resultType = result!.GetType();
        Assert.Equal("namespace Flux;", resultType.GetProperty("Value")!.GetValue(result));
        Assert.False((bool)resultType.GetProperty("Withheld")!.GetValue(result)!);
        Assert.Null(resultType.GetProperty("ReasonCode")!.GetValue(result));
    }

    [Theory]
    [InlineData("{\"diagnostic\":{\"nested\":{\"nested\":{\"nested\":{\"nested\":{\"nested\":{\"nested\":{\"nested\":{\"nested\":{\"nested\":{\"nested\":{\"nested\":{\"nested\":{\"nested\":{\"nested\":{\"nested\":{\"nested\":{\"nested\":{\"nested\":{\"nested\":{\"nested\":{\"nested\":{\"nested\":{\"nested\":{\"nested\":{\"nested\":{\"nested\":{\"nested\":{\"nested\":{\"nested\":{\"nested\":{\"nested\":{\"password\":\"synthetic-password\"}}}}}}}}}}}}}}}}}}}}}}}}}}}}}}}}")]
    [InlineData("{\"diagnostic\":\"{\\\"password\\\":\\\"synthetic-password\\\"}\"}")]
    [InlineData("{\"connection string\":\"synthetic-local-label\"}")]
    [InlineData("{\"connection.string\":\"synthetic-local-label\"}")]
    public void Local_disclosure_withholds_JSON_credential_evidence_or_unparseable_bounded_JSON(string value)
    {
        var result = Evaluate(value);

        Assert.Null(result.Value);
        Assert.True(result.Withheld);
        Assert.Equal("secret-content-withheld", result.ReasonCode);
    }

    [Theory]
    [InlineData("parser failed; payload={\"password\":\"synthetic-password\"}")]
    [InlineData("parser failed; payload={\\\"oauth_client_secret\\\":\\\"synthetic-secret\\\"}")]
    [InlineData("parser failed; payload={\"diagnostic\":{\"access_token_value\":\"synthetic-token\"}}")]
    public void Local_disclosure_withholds_credential_JSON_fragments_embedded_in_diagnostic_prose(string value)
    {
        var result = Evaluate(value);

        Assert.Null(result.Value);
        Assert.True(result.Withheld);
        Assert.Equal("secret-content-withheld", result.ReasonCode);
    }

    [Theory]
    [InlineData("parser failed; payload={\"password\":\"synthetic-password\"")]
    [InlineData("parser failed; payload={\"oauth_access_tok")]
    [InlineData("parser failed; payload={\\\"client_secr")]
    [InlineData("parser failed; outer={broken:{\"password\":\"synthetic-password\"}}")]
    [InlineData("parser failed; payload={\\\"note\\\":\\\"brace } is data\\\",\\\"client_secret_value\\\":\\\"synthetic-secret\\\"}")]
    [InlineData("parser failed; payload={\"oauth_access_token_value\":\"synthetic-token\"}")]
    [InlineData("parser failed; payload={\"client_secret_value\":\"synthetic-secret\"}")]
    [InlineData("parser failed; payload={\"note\":] \"password\":\"synthetic-password\"}")]
    [InlineData("parser failed; payload={\"note\":} \"oauth_access_token_value\":\"synthetic-token\"}")]
    [InlineData("parser failed; payload=[\"note\"} \"client_secret_value\":\"synthetic-secret\"}")]
    [InlineData("parser failed; payload={\\\"note\\\":] \\\"client_secret\\\":\\\"synthetic-secret\\\"}")]
    [InlineData("parser failed; payload={\"note\":] \"pass\\u0077ord\":\"synthetic-secret\"}")]
    [InlineData("parser failed; payload={\\\"note\\\":] \\\"pass\\u0077ord\\\":\\\"synthetic-secret\\\"}")]
    [InlineData("parser failed; payload={\"note\":\"clean\"} \"password\":\"synthetic-password\"}")]
    [InlineData("parser failed; payload={\\\"note\\\":\\\"clean\\\"} \\\"client_secret\\\":\\\"synthetic-secret\\\"}")]
    [InlineData("parser failed; payload={\"password\" \"synthetic-secret\"}")]
    [InlineData("parser failed; payload={\"oauth_access_tok\"}")]
    public void Local_disclosure_fails_closed_for_malformed_or_composite_credential_JSON_evidence(string value)
    {
        var result = Evaluate(value);

        Assert.Null(result.Value);
        Assert.True(result.Withheld);
        Assert.Equal("secret-content-withheld", result.ReasonCode);
    }

    [Fact]
    public void Local_disclosure_returns_a_clean_non_JSON_diagnostic_unchanged()
    {
        var result = Evaluate("worker diagnostic: parser returned no supported members");

        Assert.Equal("worker diagnostic: parser returned no supported members", result.Value);
        Assert.False(result.Withheld);
        Assert.Null(result.ReasonCode);
    }

    [Fact]
    public void Local_disclosure_preserves_a_non_JSON_diagnostic_with_braces()
    {
        const string value = "worker diagnostic: expected { identifier }";

        var result = Evaluate(value);

        Assert.Equal(value, result.Value);
        Assert.False(result.Withheld);
        Assert.Null(result.ReasonCode);
    }

    [Theory]
    [InlineData("worker diagnostic: payload={\\\"note\\\":\\\"brace } is data\\\"}")]
    [InlineData("worker diagnostic: outer={broken:{\"note\":\"clean\"}}")]
    [InlineData("worker diagnostic: payload={\"note\":\"truncated clean detail")]
    [InlineData("parser failed; payload={\"note\":] \"diagnostic\":\"clean\"}")]
    [InlineData("parser failed; payload={\\\"note\\\":] \\\"diagnostic\\\":\\\"clean\\\"}")]
    [InlineData("parser failed; payload={\"note\":] \"diag\\u006eostic\":\"clean\"}")]
    [InlineData("parser failed; payload={\\\"note\\\":] \\\"diag\\u006eostic\\\":\\\"clean\\\"}")]
    [InlineData("parser failed; payload={\"note\":\"clean\"} \"diagnostic\":\"clean\"}")]
    [InlineData("parser failed; payload={\\\"note\\\":\\\"clean\\\"} \\\"diagnostic\\\":\\\"clean\\\"}")]
    [InlineData("parser failed; payload={\"diagnostic\" \"clean\"}")]
    public void Local_disclosure_preserves_malformed_brace_diagnostics_without_credential_evidence(string value)
    {
        var result = Evaluate(value);

        Assert.Equal(value, result.Value);
        Assert.False(result.Withheld);
        Assert.Null(result.ReasonCode);
    }

    [Fact]
    public void Local_disclosure_fails_closed_after_too_many_embedded_JSON_candidates()
    {
        var result = Evaluate("parser diagnostic: " + string.Concat(Enumerable.Repeat("{", 65)));

        Assert.Null(result.Value);
        Assert.True(result.Withheld);
        Assert.Equal("secret-content-withheld", result.ReasonCode);
    }

    [Fact]
    public void Local_disclosure_fails_closed_when_an_embedded_JSON_candidate_exceeds_the_depth_budget()
    {
        var value = "parser diagnostic: payload=" +
            string.Concat(Enumerable.Repeat("{\"diagnostic\":", 33)) +
            "\"clean\"" +
            new string('}', 33);

        var result = Evaluate(value);

        Assert.Null(result.Value);
        Assert.True(result.Withheld);
        Assert.Equal("secret-content-withheld", result.ReasonCode);
    }

    [Fact]
    public void Local_disclosure_fails_closed_when_an_embedded_JSON_candidate_exceeds_the_length_budget()
    {
        var value = "parser diagnostic: payload={\"note\":\"" + new string('x', 5_000) + "\"}";

        var result = Evaluate(value);

        Assert.Null(result.Value);
        Assert.True(result.Withheld);
        Assert.Equal("secret-content-withheld", result.ReasonCode);
    }

    [Fact]
    public void Local_disclosure_preserves_a_clean_embedded_JSON_candidate_within_the_length_budget()
    {
        var value = "parser diagnostic: payload={\"note\":\"" + new string('x', 3_900) + "\"}";

        var result = Evaluate(value);

        Assert.Equal(value, result.Value);
        Assert.False(result.Withheld);
        Assert.Null(result.ReasonCode);
    }

    [Fact]
    public void Local_disclosure_withholds_an_encoded_JSON_chain_beyond_the_bounded_reader()
    {
        var result = Evaluate(BuildEncodedJsonChain("clean retained diagnostic", layers: 9));

        Assert.Null(result.Value);
        Assert.True(result.Withheld);
        Assert.Equal("secret-content-withheld", result.ReasonCode);
    }

    private static (string? Value, bool Withheld, string? ReasonCode) Evaluate(string value)
    {
        var implementationType = typeof(FluxKnowledgeDbContext).Assembly.GetType(
            "FluxKnowledge.Infrastructure.SqlServer.Visibility.LocalPrivateContentDisclosure");
        Assert.NotNull(implementationType);

        var result = implementationType!.GetMethod("Evaluate")!.Invoke(
            Activator.CreateInstance(implementationType), [value, 0]);
        Assert.NotNull(result);
        var resultType = result!.GetType();
        return (
            (string?)resultType.GetProperty("Value")!.GetValue(result),
            (bool)resultType.GetProperty("Withheld")!.GetValue(result)!,
            (string?)resultType.GetProperty("ReasonCode")!.GetValue(result));
    }

    private static string BuildEncodedJsonChain(string value, int layers)
    {
        for (var index = 0; index < layers; index++)
        {
            value = System.Text.Json.JsonSerializer.Serialize(value);
        }

        return value;
    }
}
