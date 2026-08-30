using System.Text.Json;
using FluxKnowledge.Integrations.Windows.NativeGoLive;

namespace FluxKnowledge.Integrations.Codex;

/// <summary>Generates the app-owned native local marketplace through the shared no-follow writer.</summary>
public sealed class NativeCodexPluginManifestWriter
{
    public const string PluginName = CodexRegistrationPaths.PluginName;
    public const string McpEndpoint = "http://127.0.0.1:5137/mcp";
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly HandleRelativeNativeFileSystem _fileSystem;

    public NativeCodexPluginManifestWriter()
        : this(new HandleRelativeNativeFileSystem())
    {
    }

    internal NativeCodexPluginManifestWriter(HandleRelativeNativeFileSystem fileSystem)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        _fileSystem = fileSystem;
    }

    public async Task WriteAsync(string marketplaceRoot, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(marketplaceRoot);
        var paths = CodexRegistrationPaths.CreateForIsolatedTests(marketplaceRoot);
        using var marketplace = _fileSystem.OpenOrCreateDirectory(paths.MarketplaceRoot);
        using var plugins = await OpenOrCreateChildAsync(marketplace, "plugins", cancellationToken).ConfigureAwait(false);
        using var plugin = await OpenOrCreateChildAsync(plugins, PluginName, cancellationToken).ConfigureAwait(false);
        using var metadata = await OpenOrCreateChildAsync(plugin, ".codex-plugin", cancellationToken).ConfigureAwait(false);

        var manifest = new
        {
            name = PluginName,
            version = "1.0.0",
            description = "Local FluxKnowledge MCP tools.",
            author = new { name = "FluxKnowledge" },
            mcpServers = "./.mcp.json",
            @interface = new
            {
                displayName = "FluxKnowledge",
                shortDescription = "Local knowledge tools",
                longDescription = "Access local FluxKnowledge tools through MCP.",
                developerName = "FluxKnowledge",
                category = "Productivity",
                capabilities = new[] { "Read" },
                defaultPrompt = new[] { "Search my local knowledge base." }
            }
        };
        var companion = new
        {
            mcpServers = new Dictionary<string, object>
            {
                [PluginName] = new { type = "http", url = McpEndpoint }
            }
        };
        var marketplaceDocument = new
        {
            name = CodexRegistrationPaths.MarketplaceName,
            @interface = new { displayName = "FluxKnowledge" },
            plugins = new[]
            {
                new
                {
                    name = PluginName,
                    source = new { source = "local", path = "./plugins/fluxknowledge" },
                    policy = new { installation = "AVAILABLE", authentication = "ON_INSTALL" },
                    category = "Productivity"
                }
            }
        };

        await WriteJsonAsync(metadata, "plugin.json", manifest, cancellationToken).ConfigureAwait(false);
        await WriteJsonAsync(plugin, ".mcp.json", companion, cancellationToken).ConfigureAwait(false);
        await WriteJsonAsync(marketplace, "marketplace.json", marketplaceDocument, cancellationToken).ConfigureAwait(false);
    }

    public async Task<NativeCodexPluginValidation> ValidateAsync(
        string marketplaceRoot,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var paths = CodexRegistrationPaths.CreateForIsolatedTests(marketplaceRoot);
            using var marketplace = _fileSystem.OpenDirectory(paths.MarketplaceRoot);
            using var plugins = _fileSystem.OpenDirectory(marketplace, "plugins");
            using var plugin = _fileSystem.OpenDirectory(plugins, PluginName);
            using var metadata = _fileSystem.OpenDirectory(plugin, ".codex-plugin");
            var manifestBytes = await _fileSystem.ReadLiteralFileAsync(metadata, "plugin.json", cancellationToken).ConfigureAwait(false);
            var companionBytes = await _fileSystem.ReadLiteralFileAsync(plugin, ".mcp.json", cancellationToken).ConfigureAwait(false);
            var marketplaceBytes = await _fileSystem.ReadLiteralFileAsync(marketplace, "marketplace.json", cancellationToken).ConfigureAwait(false);
            if (manifestBytes is null || companionBytes is null || marketplaceBytes is null)
            {
                return new(false, "plugin-material-missing");
            }

            using var manifest = JsonDocument.Parse(manifestBytes.Content);
            using var companion = JsonDocument.Parse(companionBytes.Content);
            using var marketplaceJson = JsonDocument.Parse(marketplaceBytes.Content);
            var root = manifest.RootElement;
            var valid = Path.GetFileName(paths.PluginRoot) == PluginName
                && StringProperty(root, "name") == PluginName
                && IsSemVer(StringProperty(root, "version"))
                && !string.IsNullOrWhiteSpace(StringProperty(root, "description"))
                && root.TryGetProperty("author", out var author) && !string.IsNullOrWhiteSpace(StringProperty(author, "name"))
                && StringProperty(root, "mcpServers") == "./.mcp.json"
                && ValidInterface(root)
                && ValidSingleServer(companion.RootElement)
                && ValidMarketplace(marketplaceJson.RootElement);
            return valid ? new(true, null) : new(false, "plugin-material-invalid");
        }
        catch (Exception exception) when (
            exception is IOException or JsonException or UnauthorizedAccessException or InvalidDataException)
        {
            return new(false, "plugin-material-missing");
        }
    }

    private async ValueTask<VerifiedNativeDirectory> OpenOrCreateChildAsync(
        VerifiedNativeDirectory parent,
        string literalChild,
        CancellationToken cancellationToken)
    {
        try
        {
            return _fileSystem.OpenDirectory(parent, literalChild);
        }
        catch (FileNotFoundException)
        {
            var created = await _fileSystem.CreateDirectoryAsync(parent, literalChild, cancellationToken).ConfigureAwait(false);
            if (!created.Changed)
            {
                throw new IOException(created.Reason ?? "directory-create-failed");
            }

            return _fileSystem.OpenDirectory(parent, literalChild);
        }
    }

    private async ValueTask WriteJsonAsync(
        VerifiedNativeDirectory parent,
        string destinationLiteralChild,
        object value,
        CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(value, SerializerOptions);
        var terminatedPayload = new byte[payload.Length + 1];
        payload.CopyTo(terminatedPayload, 0);
        terminatedPayload[^1] = (byte)'\n';
        var existing = await _fileSystem.ReadLiteralFileAsync(parent, destinationLiteralChild, cancellationToken).ConfigureAwait(false);
        if (existing is not null && existing.Content.AsSpan().SequenceEqual(terminatedPayload)) return;

        var result = await _fileSystem.ReplaceFileAsync(
            parent,
            destinationLiteralChild + ".tmp",
            destinationLiteralChild,
            terminatedPayload,
            existing?.Identity,
            cancellationToken).ConfigureAwait(false);
        if (!result.Changed)
        {
            throw new IOException(result.Reason ?? "plugin-material-write-failed");
        }
    }

    private static bool ValidInterface(JsonElement root) => root.TryGetProperty("interface", out var ui)
        && !string.IsNullOrWhiteSpace(StringProperty(ui, "displayName"))
        && !string.IsNullOrWhiteSpace(StringProperty(ui, "shortDescription"))
        && !string.IsNullOrWhiteSpace(StringProperty(ui, "longDescription"))
        && !string.IsNullOrWhiteSpace(StringProperty(ui, "developerName"))
        && !string.IsNullOrWhiteSpace(StringProperty(ui, "category"))
        && ui.TryGetProperty("capabilities", out var capabilities) && capabilities.ValueKind == JsonValueKind.Array && capabilities.GetArrayLength() > 0 && capabilities.EnumerateArray().All(item => item.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(item.GetString()))
        && ui.TryGetProperty("defaultPrompt", out var prompts) && prompts.ValueKind == JsonValueKind.Array && prompts.GetArrayLength() is > 0 and <= 3 && prompts.EnumerateArray().All(item => item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 and <= 128 });

    private static bool ValidSingleServer(JsonElement companion) => companion.ValueKind == JsonValueKind.Object
        && companion.TryGetProperty("mcpServers", out var servers)
        && servers.ValueKind == JsonValueKind.Object && servers.EnumerateObject().Count() == 1
        && servers.TryGetProperty(PluginName, out var server)
        && StringProperty(server, "type") == "http" && StringProperty(server, "url") == McpEndpoint;

    private static bool ValidMarketplace(JsonElement marketplace) => marketplace.ValueKind == JsonValueKind.Object
        && StringProperty(marketplace, "name") == CodexRegistrationPaths.MarketplaceName
        && marketplace.TryGetProperty("plugins", out var plugins) && plugins.ValueKind == JsonValueKind.Array && plugins.GetArrayLength() == 1
        && StringProperty(plugins[0], "name") == PluginName
        && plugins[0].TryGetProperty("source", out var source) && StringProperty(source, "source") == "local" && StringProperty(source, "path") == "./plugins/fluxknowledge";

    private static string? StringProperty(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(name, out var property) &&
        property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static bool IsSemVer(string? value) =>
        value is not null && System.Text.RegularExpressions.Regex.IsMatch(
            value,
            "^\\d+\\.\\d+\\.\\d+(-[0-9A-Za-z.-]+)?(\\+[0-9A-Za-z.-]+)?$");
}

public sealed record NativeCodexPluginValidation(bool IsValid, string? Reason);
