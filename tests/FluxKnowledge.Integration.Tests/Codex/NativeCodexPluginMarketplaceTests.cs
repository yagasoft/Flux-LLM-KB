using System.Text.Json;
using FluxKnowledge.Integrations.Codex;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Codex;

public sealed class NativeCodexPluginMarketplaceTests
{
    [Fact]
    public async Task Writer_creates_the_normalised_plugin_beneath_the_app_owned_marketplace_root()
    {
        var root = Path.Combine(Path.GetTempPath(), "FluxKnowledgeNativeMarketplaceTests", Guid.NewGuid().ToString("N"));
        try
        {
            var writer = new NativeCodexPluginManifestWriter();
            await writer.WriteAsync(root);

            var pluginRoot = Path.Combine(root, "plugins", "fluxknowledge");
            using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(pluginRoot, ".codex-plugin", "plugin.json")));
            using var marketplace = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(root, ".agents", "plugins", "marketplace.json")));

            Assert.Equal(@"I:\FluxKnowledge\CodexPlugin", CodexRegistrationPaths.Production.MarketplaceRoot);
            Assert.Equal("fluxknowledge", manifest.RootElement.GetProperty("name").GetString());
            Assert.Equal(JsonValueKind.Array, manifest.RootElement.GetProperty("interface").GetProperty("capabilities").ValueKind);
            Assert.Equal(JsonValueKind.Array, manifest.RootElement.GetProperty("interface").GetProperty("defaultPrompt").ValueKind);
            Assert.Equal("../../plugins/fluxknowledge", marketplace.RootElement.GetProperty("plugins")[0].GetProperty("source").GetProperty("path").GetString());
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
