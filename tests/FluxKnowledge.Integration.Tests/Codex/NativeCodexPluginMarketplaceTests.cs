using System.Text.Json;
using FluxKnowledge.Application.Operations;
using FluxKnowledge.Integrations.Codex;
using FluxKnowledge.Integrations.Windows.NativeGoLive;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Codex;

public sealed class NativeCodexPluginMarketplaceTests
{
    [Fact]
    public async Task Confirmed_clean_slate_removes_a_stale_exact_marketplace_before_reobserving_missing()
    {
        var root = Path.Combine(Path.GetTempPath(), "FluxKnowledgeNativeMarketplaceTests", Guid.NewGuid().ToString("N"));
        try
        {
            var payloadRoot = Path.Combine(root, "payload");
            Directory.CreateDirectory(payloadRoot);
            await File.WriteAllTextAsync(Path.Combine(payloadRoot, "payload.dll"), "one-shot-payload");
            var plan = NativeGoLivePlan.CreateForIsolatedTests(
                LiveRootLayout.CreateForIsolatedTests(Path.Combine(root, "live")), new string('a', 40));
            var manifest = NativeGoLivePayloadHasher.Compute(payloadRoot);
            var capability = new NativeGoLiveCloseoutCapabilityIssuer().Issue(plan, payloadRoot, manifest.Sha256);
            Assert.True(capability.TryBeginExecution());
            var runner = new StaleMarketplaceRunner();
            var port = new NativeGoLiveWindowsMarketplacePort(capability, plan.Codex, runner);

            await port.ResetForConfirmedCleanSlateAsync(plan.Codex, CancellationToken.None);

            Assert.Equal(["remove", "list"], runner.Commands);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

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

    private sealed class StaleMarketplaceRunner : INativeCodexMarketplaceCommandRunner
    {
        public List<string> Commands { get; } = [];

        public ValueTask<NativeCodexMarketplaceCommandResult> AddFluxKnowledgeMarketplaceAsync(
            string marketplaceRoot,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<NativeCodexMarketplaceCommandResult> RemoveFluxKnowledgeMarketplaceAsync(
            CancellationToken cancellationToken)
        {
            Commands.Add("remove");
            return ValueTask.FromResult(new NativeCodexMarketplaceCommandResult(0, string.Empty, string.Empty));
        }

        public ValueTask<NativeCodexMarketplaceCommandResult> ListMarketplacesJsonAsync(
            CancellationToken cancellationToken)
        {
            Commands.Add("list");
            return ValueTask.FromResult(new NativeCodexMarketplaceCommandResult(
                0,
                "{\"marketplaces\":[]}",
                new string('0', 64)));
        }
    }
}
