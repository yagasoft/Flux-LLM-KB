using System.Text;
using System.Text.Json;
using FluxKnowledge.Application.Mcp;
using ModelContextProtocol.Protocol;
using FluxKnowledge.Application.IntegrationV1;

namespace FluxKnowledge.Web.Mcp;

internal static class McpResultFactory
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public static CallToolResult Json(object value) => Text(JsonSerializer.Serialize(value, SerializerOptions));

    public static CallToolResult NativeJson(NativeV1Envelope envelope)
    {
        var bounded = NativeBytes(envelope);
        return Text(Encoding.UTF8.GetString(bounded.Utf8));
    }

    public static (NativeV1Envelope Envelope, byte[] Utf8) NativeBytes(NativeV1Envelope envelope)
    {
        var utf8 = JsonSerializer.SerializeToUtf8Bytes(envelope, SerializerOptions);
        if (utf8.Length <= NativeV1ContractLimits.MaximumResponseBytes) return (envelope, utf8);

        var failure = NativeFailure("response-too-large");
        return (failure, JsonSerializer.SerializeToUtf8Bytes(failure, SerializerOptions));
    }

    public static CallToolResult Text(string value) =>
        new()
        {
            Content = [new TextContentBlock { Text = value }],
            IsError = false
        };

    public static CallToolResult Failure(string toolName, Exception exception) =>
        Json(McpTransientFailureClassifier.IsTransient(exception)
            ? McpErrorEnvelope.TemporaryUnavailable(toolName)
            : McpErrorEnvelope.ToolError(toolName));

    public static NativeV1Envelope NativeSuccess(object result) => new(true, result, null, null, false);

    public static NativeV1Envelope NativeFailure(Exception exception) => exception switch
    {
        NativeOperationException native => new(false, null, native.ReasonCode, "The request could not be completed.", false),
        _ when McpTransientFailureClassifier.IsTransient(exception) => new(false, null, "temporary-unavailable", "The request could not be completed.", true),
        _ => new(false, null, "operation-failed", "The request could not be completed.", false)
    };

    public static NativeV1Envelope NativeFailure(string reasonCode, bool retryable = false) =>
        new(false, null, reasonCode, "The request could not be completed.", retryable);
}

public sealed record NativeV1Envelope(bool Ok, object? Result, string? ReasonCode, string? Message, bool Retryable);
