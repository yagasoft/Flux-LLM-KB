using FluxKnowledge.Application.IntegrationV1;
using Xunit;

namespace FluxKnowledge.Integration.Tests.IntegrationV1;

public sealed class NativeV1EnvelopeProtectorTests
{
    [Theory]
    [InlineData("secret")]
    [InlineData("secretValue")]
    [InlineData("password")]
    [InlineData("pwd")]
    [InlineData("passphrase")]
    [InlineData("accessToken")]
    [InlineData("refreshToken")]
    [InlineData("idToken")]
    [InlineData("token")]
    [InlineData("apiKey")]
    [InlineData("clientSecret")]
    [InlineData("connectionString")]
    [InlineData("authorization")]
    [InlineData("cookie")]
    [InlineData("setCookie")]
    [InlineData("privateKey")]
    public void Credential_property_matrix_is_rejected_at_any_depth(string property)
    {
        var envelope = $"{{\"ok\":true,\"result\":{{\"nested\":{{\"{property}\":\"value\"}}}},\"reasonCode\":null,\"message\":null,\"retryable\":false}}";

        var accepted = NativeV1EnvelopeProtector.TryRead(envelope, out var protectedEnvelope);

        Assert.False(accepted);
        Assert.Empty(protectedEnvelope);
    }

    [Theory]
    [InlineData("Bearer opaque-value")]
    [InlineData("{\"authorization\":\"Bearer opaque-value\"}")]
    [InlineData("eyJjbGllbnRTZWNyZXQiOiJ2YWx1ZSJ9")]
    [InlineData("eyJhcGlLZXkiOiJ2YWx1ZSJ9")]
    public void Bare_and_encoded_credential_representations_are_rejected(string result)
    {
        var envelope = $"{{\"ok\":true,\"result\":{System.Text.Json.JsonSerializer.Serialize(result)},\"reasonCode\":null,\"message\":null,\"retryable\":false}}";

        var accepted = NativeV1EnvelopeProtector.TryRead(envelope, out var protectedEnvelope);

        Assert.False(accepted);
        Assert.Empty(protectedEnvelope);
    }

    [Fact]
    public void Three_nested_base64_layers_fail_closed_at_the_bounded_inspection_depth()
    {
        var result = "{\"accessToken\":\"value\"}";
        for (var layer = 0; layer < 3; layer++) result = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(result));
        var envelope = $"{{\"ok\":true,\"result\":{System.Text.Json.JsonSerializer.Serialize(result)},\"reasonCode\":null,\"message\":null,\"retryable\":false}}";

        var accepted = NativeV1EnvelopeProtector.TryRead(envelope, out var protectedEnvelope);

        Assert.False(accepted);
        Assert.Empty(protectedEnvelope);
    }

    [Fact]
    public void Whitespace_bearing_base64_credential_fails_closed()
    {
        var encoded = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("{\"accessToken\":\"value\"}"));
        var result = encoded[..8] + " \r\n\t" + encoded[8..];
        var envelope = $"{{\"ok\":true,\"result\":{System.Text.Json.JsonSerializer.Serialize(result)},\"reasonCode\":null,\"message\":null,\"retryable\":false}}";

        var accepted = NativeV1EnvelopeProtector.TryRead(envelope, out var protectedEnvelope);

        Assert.False(accepted);
        Assert.Empty(protectedEnvelope);
    }
}
