using System.Text.Json;
using FluxKnowledge.Application.Mcp;
using ModelContextProtocol.Protocol;

namespace FluxKnowledge.Web.Mcp;

internal static class McpResultFactory
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public static CallToolResult Json(object value) => Text(JsonSerializer.Serialize(value, SerializerOptions));

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
}
