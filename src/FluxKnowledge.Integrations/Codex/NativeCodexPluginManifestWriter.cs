using System.Text.Json;
using System.Text;
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
        using var pluginDirectory = await OpenOrCreateChildAsync(marketplace, "plugins", cancellationToken).ConfigureAwait(false);
        using var plugin = await OpenOrCreateChildAsync(pluginDirectory, PluginName, cancellationToken).ConfigureAwait(false);
        using var metadata = await OpenOrCreateChildAsync(plugin, ".codex-plugin", cancellationToken).ConfigureAwait(false);
        using var hooksDirectory = await OpenOrCreateChildAsync(plugin, "hooks", cancellationToken).ConfigureAwait(false);
        using var agentDirectory = await OpenOrCreateChildAsync(marketplace, ".agents", cancellationToken).ConfigureAwait(false);
        using var marketplaceDirectory = await OpenOrCreateChildAsync(agentDirectory, "plugins", cancellationToken).ConfigureAwait(false);

        await RemoveObsoleteHookRegistrationAsync(hooksDirectory, cancellationToken).ConfigureAwait(false);

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
        await WriteBytesAsync(hooksDirectory, "hooks.json", RenderHooksUtf8(), cancellationToken).ConfigureAwait(false);
        await WriteBytesAsync(hooksDirectory, "invoke-native-hook.ps1", RenderNativeHookAdapterUtf8(), cancellationToken).ConfigureAwait(false);
        await WriteJsonAsync(marketplaceDirectory, "marketplace.json", marketplaceDocument, cancellationToken).ConfigureAwait(false);
    }

    public async Task<NativeCodexPluginValidation> ValidateAsync(
        string marketplaceRoot,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var paths = CodexRegistrationPaths.CreateForIsolatedTests(marketplaceRoot);
            using var marketplace = _fileSystem.OpenDirectory(paths.MarketplaceRoot);
            using var pluginDirectory = _fileSystem.OpenDirectory(marketplace, "plugins");
            using var plugin = _fileSystem.OpenDirectory(pluginDirectory, PluginName);
            using var metadata = _fileSystem.OpenDirectory(plugin, ".codex-plugin");
            using var hooksDirectory = _fileSystem.OpenDirectory(plugin, "hooks");
            using var agentDirectory = _fileSystem.OpenDirectory(marketplace, ".agents");
            using var marketplaceDirectory = _fileSystem.OpenDirectory(agentDirectory, "plugins");
            var manifestBytes = await _fileSystem.ReadLiteralFileAsync(metadata, "plugin.json", cancellationToken).ConfigureAwait(false);
            var companionBytes = await _fileSystem.ReadLiteralFileAsync(plugin, ".mcp.json", cancellationToken).ConfigureAwait(false);
            var hooksBytes = await _fileSystem.ReadLiteralFileAsync(hooksDirectory, "hooks.json", cancellationToken).ConfigureAwait(false);
            var adapterBytes = await _fileSystem.ReadLiteralFileAsync(hooksDirectory, "invoke-native-hook.ps1", cancellationToken).ConfigureAwait(false);
            var marketplaceBytes = await _fileSystem.ReadLiteralFileAsync(marketplaceDirectory, "marketplace.json", cancellationToken).ConfigureAwait(false);
            if (manifestBytes is null || companionBytes is null || marketplaceBytes is null || hooksBytes is null || adapterBytes is null)
            {
                return new(false, "plugin-material-missing");
            }

            using var manifest = JsonDocument.Parse(manifestBytes.Content);
            using var companion = JsonDocument.Parse(companionBytes.Content);
            using var hooks = JsonDocument.Parse(hooksBytes.Content);
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
                && ValidHooks(hooks.RootElement)
                && ValidNativeHookAdapter(adapterBytes.Content)
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
        var terminatedPayload = RenderJsonUtf8(value);
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

    private async ValueTask WriteBytesAsync(
        VerifiedNativeDirectory parent,
        string destinationLiteralChild,
        byte[] payload,
        CancellationToken cancellationToken)
    {
        var existing = await _fileSystem.ReadLiteralFileAsync(parent, destinationLiteralChild, cancellationToken).ConfigureAwait(false);
        if (existing is not null && existing.Content.AsSpan().SequenceEqual(payload)) return;
        var result = await _fileSystem.ReplaceFileAsync(parent, destinationLiteralChild + ".tmp", destinationLiteralChild, payload, existing?.Identity, cancellationToken).ConfigureAwait(false);
        if (!result.Changed) throw new IOException(result.Reason ?? "plugin-material-write-failed");
    }

    private async ValueTask RemoveObsoleteHookRegistrationAsync(
        VerifiedNativeDirectory hooksDirectory,
        CancellationToken cancellationToken)
    {
        var obsoleteRegistration = await _fileSystem.ReadLiteralFileAsync(
            hooksDirectory,
            "registration.json",
            cancellationToken).ConfigureAwait(false);
        if (obsoleteRegistration is null) return;

        var result = await _fileSystem.DeleteLiteralChildAsync(
            hooksDirectory,
            "registration.json",
            obsoleteRegistration.Identity,
            cancellationToken).ConfigureAwait(false);
        if (!result.Changed) throw new IOException(result.Reason ?? "obsolete-hook-registration-delete-failed");
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

    private static object Hook(string eventName, string statusMessage) => new { hooks = new[] { Command(eventName, statusMessage) } };

    /// <summary>Renders the exact UTF-8 bytes accepted for the generated hook definition.</summary>
    public static byte[] RenderHooksUtf8() => RenderJsonUtf8(new
    {
        hooks = new Dictionary<string, object>
        {
            ["UserPromptSubmit"] = new[] { Hook("UserPromptSubmit", "Retrieving local FluxKnowledge context") },
            ["PreCompact"] = new[] { new { matcher = "manual|auto", hooks = new[] { Command("PreCompact", "Preparing local turn capture") } } },
            ["Stop"] = new[] { Hook("Stop", "Finalising local Codex turn") }
        }
    });

    /// <summary>Renders the exact UTF-8 bytes accepted for the generated hook adapter.</summary>
    public static byte[] RenderNativeHookAdapterUtf8() => Encoding.UTF8.GetBytes(NativeHookAdapter);

    private static object Command(string eventName, string statusMessage) => new
    {
        type = "command",
        command = $"pwsh -NoProfile -File \"$env:PLUGIN_ROOT/hooks/invoke-native-hook.ps1\" {eventName}",
        commandWindows = $"powershell -NoProfile -ExecutionPolicy Bypass -File \"$env:PLUGIN_ROOT\\hooks\\invoke-native-hook.ps1\" {eventName}",
        statusMessage
    };

    private static byte[] RenderJsonUtf8(object value)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(value, SerializerOptions);
        var terminatedPayload = new byte[payload.Length + 1];
        payload.CopyTo(terminatedPayload, 0);
        terminatedPayload[^1] = (byte)'\n';
        return terminatedPayload;
    }

    private static bool ValidHooks(JsonElement hooks) => hooks.ValueKind == JsonValueKind.Object
        && hooks.TryGetProperty("hooks", out var events) && events.ValueKind == JsonValueKind.Object
        && new[] { "UserPromptSubmit", "PreCompact", "Stop" }.All(eventName => events.TryGetProperty(eventName, out var entries) && entries.ValueKind == JsonValueKind.Array && entries.GetArrayLength() == 1);

    private static bool ValidNativeHookAdapter(byte[] adapterBytes)
    {
        var adapter = Encoding.UTF8.GetString(adapterBytes);
        return adapter.Contains("http://127.0.0.1:5137/native/v1/codex/hooks/", StringComparison.Ordinal)
            && adapter.Contains("continue", StringComparison.OrdinalIgnoreCase)
            && !adapter.Contains("FLUXKNOWLEDGE_NATIVE_HOOK_", StringComparison.Ordinal)
            && !adapter.Contains("X-FluxKnowledge-Hook-", StringComparison.Ordinal)
            && !adapter.Contains("X-FluxKnowledge-Plugin-Root", StringComparison.Ordinal)
            && !new[] { "python", "flux_llm_kb", "postgresql", "docker" }.Any(forbidden => adapter.Contains(forbidden, StringComparison.OrdinalIgnoreCase));
    }

private const string NativeHookAdapter = """
param([Parameter(Mandatory = $true)][ValidateSet('UserPromptSubmit', 'PreCompact', 'Stop')][string]$EventName)
$payload = [Console]::In.ReadToEnd()
try {
    $response = Invoke-RestMethod -Method Post -Uri "http://127.0.0.1:5137/native/v1/codex/hooks/$EventName" -ContentType 'application/json' -Body $payload -TimeoutSec 10
    $output = [ordered]@{ continue = if ($null -eq $response.continue) { $true } else { [bool]$response.continue } }
    if ($EventName -eq 'UserPromptSubmit' -and $null -ne $response.hookSpecificOutput) {
        $context = [string]$response.hookSpecificOutput.additionalContext
        if (-not [string]::IsNullOrWhiteSpace($context)) {
            $output.hookSpecificOutput = [ordered]@{ hookEventName = 'UserPromptSubmit'; additionalContext = $context }
        }
    }
    $systemMessage = [string]$response.systemMessage
    if (-not [string]::IsNullOrWhiteSpace($systemMessage)) {
        $output.systemMessage = $systemMessage
    }
    $output | ConvertTo-Json -Compress -Depth 8
}
catch {
    [ordered]@{ continue = $true; systemMessage = 'Native Codex hook transport unavailable; continuing.' } | ConvertTo-Json -Compress
}
""";

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
