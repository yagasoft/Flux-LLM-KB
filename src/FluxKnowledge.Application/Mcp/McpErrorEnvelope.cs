using System.Text.Json.Serialization;

namespace FluxKnowledge.Application.Mcp;

public sealed record McpErrorEnvelope(
    bool Ok,
    string Status,
    [property: JsonPropertyName("settings_mutated")]
    bool SettingsMutated,
    McpErrorDetail Error)
{
    public static McpErrorEnvelope TemporaryUnavailable(string toolName) =>
        new(
            false,
            "temporary_unavailable",
            false,
            new McpErrorDetail(
                "mcp.temporary_unavailable",
                $"Flux memory backend is temporarily unavailable while running {toolName}.",
                "mcp",
                toolName,
                true,
                "Retry after the Flux API, database, or search service finishes restarting.",
                503));

    public static McpErrorEnvelope ToolError(string toolName) =>
        new(
            false,
            "tool_error",
            false,
            new McpErrorDetail(
                "mcp.tool_error",
                $"Flux memory backend could not complete {toolName}.",
                "mcp",
                toolName,
                false,
                "Correct the request and retry.",
                500));
}

public sealed record McpErrorDetail(
    string Code,
    string Message,
    string Component,
    string Stage,
    bool Retryable,
    [property: JsonPropertyName("user_action")]
    string UserAction,
    [property: JsonPropertyName("status_code")]
    int StatusCode);
