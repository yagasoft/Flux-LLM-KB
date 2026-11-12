using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace FluxKnowledge.Web.Mcp;

/// <summary>The real native facade, exposing only the two read tools in the closed staging trial.</summary>
public sealed class StagingSearchMcpTools(NativeV1McpTools native)
{
    [McpServerTool(Name = "corpus.search")]
    [Description("Search the isolated staging corpus.")]
    public Task<CallToolResult> Search(string query, int limit = 10, string scope = "workspace", string? root_id = null,
        string? cwd = null, string match_mode = "keyword", CancellationToken cancellationToken = default) =>
        native.CorpusSearch(query, limit, scope, root_id, cwd, cancellationToken, match_mode);
    [McpServerTool(Name = "corpus.read")]
    [Description("Read a current isolated staging citation.")]
    public Task<CallToolResult> Read(string evidence_ref, int context_characters = 1024, CancellationToken cancellationToken = default) =>
        native.CorpusRead(evidence_ref, context_characters, cancellationToken);
}
