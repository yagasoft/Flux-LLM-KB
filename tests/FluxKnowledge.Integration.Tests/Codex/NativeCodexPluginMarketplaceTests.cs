using System.Diagnostics;
using System.Text.Json;
using FluxKnowledge.Application.Operations;
using FluxKnowledge.Integrations.Codex;
using FluxKnowledge.Integrations.Windows.NativeGoLive;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Codex;

public sealed class NativeCodexPluginMarketplaceTests
{
    [Theory]
    [InlineData(
        "UserPromptSubmit",
        "{\"continue\":true,\"hookSpecificOutput\":{\"hookEventName\":\"UserPromptSubmit\",\"additionalContext\":\"Prior local context\"},\"systemMessage\":null}",
        "{\"continue\":true,\"hookSpecificOutput\":{\"hookEventName\":\"UserPromptSubmit\",\"additionalContext\":\"Prior local context\"}}")]
    [InlineData(
        "UserPromptSubmit",
        "{\"continue\":true,\"hookSpecificOutput\":null,\"systemMessage\":null}",
        "{\"continue\":true}")]
    [InlineData(
        "PreCompact",
        "{\"continue\":true,\"hookSpecificOutput\":null,\"systemMessage\":null}",
        "{\"continue\":true}")]
    [InlineData(
        "Stop",
        "{\"continue\":true,\"hookSpecificOutput\":null,\"systemMessage\":null}",
        "{\"continue\":true}")]
    [InlineData(
        "Stop",
        "{\"continue\":true,\"hookSpecificOutput\":null,\"systemMessage\":\"Native Codex hook ignored invalid input.\"}",
        "{\"continue\":true,\"systemMessage\":\"Native Codex hook ignored invalid input.\"}")]
    public async Task Generated_native_hook_adapter_emits_only_Codex_supported_fields(
        string eventName,
        string loopbackResponse,
        string expectedOutput)
    {
        var root = Path.Combine(Path.GetTempPath(), "FluxKnowledgeNativeHookAdapterTests", Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            var adapterPath = Path.Combine(root, "invoke-native-hook.ps1");
            var wrapperPath = Path.Combine(root, "invoke-with-stubbed-loopback.ps1");
            await File.WriteAllBytesAsync(adapterPath, NativeCodexPluginManifestWriter.RenderNativeHookAdapterUtf8());
            await File.WriteAllTextAsync(wrapperPath, """
param(
    [Parameter(Mandatory = $true)][string]$AdapterPath,
    [Parameter(Mandatory = $true)][string]$EventName
)
function Invoke-RestMethod {
    param(
        [string]$Method,
        [string]$Uri,
        [string]$ContentType,
        [string]$Body,
        [int]$TimeoutSec
    )
    return $env:FLUX_TEST_NATIVE_HOOK_RESPONSE | ConvertFrom-Json
}
& $AdapterPath $EventName
""");

            var result = await RunPowerShellAdapterAsync(wrapperPath, adapterPath, eventName, loopbackResponse);

            Assert.Equal(0, result.ExitCode);
            Assert.Equal(string.Empty, result.Error.Trim());
            Assert.Equal(expectedOutput, result.Output.Trim());
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Generated_native_hook_adapter_preserves_the_fail_open_transport_response()
    {
        var root = Path.Combine(Path.GetTempPath(), "FluxKnowledgeNativeHookAdapterTests", Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            var adapterPath = Path.Combine(root, "invoke-native-hook.ps1");
            var wrapperPath = Path.Combine(root, "invoke-with-stubbed-loopback.ps1");
            await File.WriteAllBytesAsync(adapterPath, NativeCodexPluginManifestWriter.RenderNativeHookAdapterUtf8());
            await File.WriteAllTextAsync(wrapperPath, """
param(
    [Parameter(Mandatory = $true)][string]$AdapterPath,
    [Parameter(Mandatory = $true)][string]$EventName
)
function Invoke-RestMethod {
    param(
        [string]$Method,
        [string]$Uri,
        [string]$ContentType,
        [string]$Body,
        [int]$TimeoutSec
    )
    throw 'test loopback transport failure'
}
& $AdapterPath $EventName
""");

            var result = await RunPowerShellAdapterAsync(wrapperPath, adapterPath, "Stop", "{}");

            Assert.Equal(0, result.ExitCode);
            Assert.Equal(string.Empty, result.Error.Trim());
            Assert.Equal("{\"continue\":true,\"systemMessage\":\"Native Codex hook transport unavailable; continuing.\"}", result.Output.Trim());
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

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

    private static async Task<PowerShellResult> RunPowerShellAdapterAsync(
        string wrapperPath,
        string adapterPath,
        string eventName,
        string loopbackResponse)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "pwsh",
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }
        };
        process.StartInfo.ArgumentList.Add("-NoProfile");
        process.StartInfo.ArgumentList.Add("-File");
        process.StartInfo.ArgumentList.Add(wrapperPath);
        process.StartInfo.ArgumentList.Add(adapterPath);
        process.StartInfo.ArgumentList.Add(eventName);
        process.StartInfo.Environment["FLUX_TEST_NATIVE_HOOK_RESPONSE"] = loopbackResponse;

        process.Start();
        await process.StandardInput.WriteAsync("{\"test\":true}");
        process.StandardInput.Close();
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        return new PowerShellResult(process.ExitCode, await output, await error);
    }

    private sealed record PowerShellResult(int ExitCode, string Output, string Error);

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
            var hooks = await File.ReadAllTextAsync(Path.Combine(pluginRoot, "hooks", "hooks.json"));
            var adapter = await File.ReadAllTextAsync(Path.Combine(pluginRoot, "hooks", "invoke-native-hook.ps1"));

            Assert.Equal(@"I:\FluxKnowledge\CodexPlugin", CodexRegistrationPaths.Production.MarketplaceRoot);
            Assert.Equal("fluxknowledge", manifest.RootElement.GetProperty("name").GetString());
            Assert.False(manifest.RootElement.TryGetProperty("hooks", out _));
            Assert.Equal(JsonValueKind.Array, manifest.RootElement.GetProperty("interface").GetProperty("capabilities").ValueKind);
            Assert.Equal(JsonValueKind.Array, manifest.RootElement.GetProperty("interface").GetProperty("defaultPrompt").ValueKind);
            Assert.Equal("./plugins/fluxknowledge", marketplace.RootElement.GetProperty("plugins")[0].GetProperty("source").GetProperty("path").GetString());
            Assert.False(File.Exists(Path.Combine(pluginRoot, "hooks", "registration.json")));
            Assert.Contains("UserPromptSubmit", hooks, StringComparison.Ordinal);
            Assert.Contains("/native/v1/codex/hooks/", adapter, StringComparison.Ordinal);
            Assert.True(adapter.Contains("continue", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain("FLUXKNOWLEDGE_NATIVE_HOOK_CHALLENGE", adapter, StringComparison.Ordinal);
            Assert.DoesNotContain("FLUXKNOWLEDGE_NATIVE_HOOK_SECRET", adapter, StringComparison.Ordinal);
            Assert.DoesNotContain("X-FluxKnowledge-Hook-", adapter, StringComparison.Ordinal);
            Assert.DoesNotContain("python", adapter, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("flux_llm_kb", adapter, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("postgresql", adapter, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("docker", adapter, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Writer_removes_an_obsolete_hook_registration_during_an_in_place_rewrite()
    {
        var root = Path.Combine(Path.GetTempPath(), "FluxKnowledgeNativeMarketplaceTests", Guid.NewGuid().ToString("N"));
        try
        {
            var writer = new NativeCodexPluginManifestWriter();
            await writer.WriteAsync(root);
            var registrationPath = Path.Combine(root, "plugins", "fluxknowledge", "hooks", "registration.json");
            await File.WriteAllTextAsync(registrationPath, "{\"obsolete\":true}");

            await writer.WriteAsync(root);

            Assert.False(File.Exists(registrationPath));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task Native_plugin_registration_requires_installed_and_enabled_state(bool installed, bool enabled)
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
            var runner = new NativePluginStateRunner(plan.Codex, installed, enabled, legacyPresentAfterRemoval: false);
            var port = new NativeGoLiveWindowsMarketplacePort(capability, plan.Codex, runner);
            _ = await port.ObserveAsync(plan.Codex, CancellationToken.None);

            var exception = await Assert.ThrowsAsync<NativeGoLiveContractException>(
                () => port.RegisterAndObserveAsync(plan.Codex, CancellationToken.None).AsTask());

            Assert.Equal("native-plugin-install-not-proved", exception.ReasonCode);
            Assert.Equal(["marketplace-list", "marketplace-list", "plugin-add", "plugin-list"], runner.Commands);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Native_plugin_registration_rejects_malformed_or_duplicate_marketplace_evidence_after_a_valid_entry(bool duplicate)
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
            var valid = new { name = plan.Codex.MarketplaceName, root = plan.Codex.MarketplaceRoot };
            var marketplaceListJson = JsonSerializer.Serialize(new
            {
                marketplaces = duplicate ? new object[] { valid, valid } : new object[] { valid, 17 }
            });
            var runner = new NativePluginStateRunner(
                plan.Codex, installed: true, enabled: true, legacyPresentAfterRemoval: false,
                marketplaceListJson: marketplaceListJson);
            var port = new NativeGoLiveWindowsMarketplacePort(capability, plan.Codex, runner);
            _ = await port.ObserveAsync(plan.Codex, CancellationToken.None);

            var exception = await Assert.ThrowsAsync<NativeGoLiveContractException>(
                () => port.RegisterAndObserveAsync(plan.Codex, CancellationToken.None).AsTask());

            Assert.Equal(duplicate ? "foreign-registration" : "lifecycle-unavailable", exception.ReasonCode);
            Assert.Equal(["marketplace-list"], runner.Commands);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Native_plugin_registration_rejects_malformed_or_duplicate_plugin_evidence_after_a_valid_entry(bool duplicate)
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
            var valid = new
            {
                pluginId = plan.Codex.PluginName + "@" + plan.Codex.MarketplaceName,
                installed = true,
                enabled = true
            };
            var pluginListJson = JsonSerializer.Serialize(new
            {
                installed = duplicate ? new object[] { valid, valid } : new object[] { valid, 17 }
            });
            var runner = new NativePluginStateRunner(
                plan.Codex, installed: true, enabled: true, legacyPresentAfterRemoval: false,
                pluginListJson: pluginListJson);
            var port = new NativeGoLiveWindowsMarketplacePort(capability, plan.Codex, runner);
            _ = await port.ObserveAsync(plan.Codex, CancellationToken.None);

            var exception = await Assert.ThrowsAsync<NativeGoLiveContractException>(
                () => port.RegisterAndObserveAsync(plan.Codex, CancellationToken.None).AsTask());

            Assert.Equal("native-plugin-install-not-proved", exception.ReasonCode);
            Assert.Equal(["marketplace-list", "marketplace-list", "plugin-add", "plugin-list"], runner.Commands);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Legacy_plugin_removal_requires_the_exact_plugin_to_be_absent_after_remove()
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
            var runner = new NativePluginStateRunner(
                plan.Codex, installed: true, enabled: true,
                legacyPresentAfterRemoval: true, legacyPresentInitially: true);
            var port = new NativeGoLiveWindowsMarketplacePort(capability, plan.Codex, runner);

            var exception = await Assert.ThrowsAsync<NativeGoLiveContractException>(
                () => port.RemoveExactLegacyPluginAsync(CancellationToken.None).AsTask());

            Assert.Equal("legacy-plugin-removal-not-proved", exception.ReasonCode);
            Assert.Equal(["plugin-list", "legacy-remove", "plugin-list"], runner.Commands);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Legacy_plugin_removal_accepts_a_well_formed_already_absent_plugin_list_without_mutation()
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
            var runner = new NativePluginStateRunner(plan.Codex, installed: true, enabled: true,
                legacyPresentAfterRemoval: false);
            var port = new NativeGoLiveWindowsMarketplacePort(capability, plan.Codex, runner);

            await port.RemoveExactLegacyPluginAsync(CancellationToken.None);

            Assert.Equal(["plugin-list"], runner.Commands);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Legacy_plugin_removal_removes_the_present_exact_plugin_then_rechecks_absence()
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
            var runner = new NativePluginStateRunner(
                plan.Codex, installed: true, enabled: true,
                legacyPresentAfterRemoval: false, legacyPresentInitially: true);
            var port = new NativeGoLiveWindowsMarketplacePort(capability, plan.Codex, runner);

            await port.RemoveExactLegacyPluginAsync(CancellationToken.None);

            Assert.Equal(["plugin-list", "legacy-remove", "plugin-list"], runner.Commands);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Native_registration_rejects_residual_legacy_plugin()
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
            var runner = new NativePluginStateRunner(
                plan.Codex, installed: true, enabled: true,
                legacyPresentAfterRemoval: false, legacyPresentInitially: true);
            var port = new NativeGoLiveWindowsMarketplacePort(capability, plan.Codex, runner);
            _ = await port.ObserveAsync(plan.Codex, CancellationToken.None);

            var exception = await Assert.ThrowsAsync<NativeGoLiveContractException>(
                () => port.RegisterAndObserveAsync(plan.Codex, CancellationToken.None).AsTask());

            Assert.Equal("native-plugin-install-not-proved", exception.ReasonCode);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Native_registration_allows_well_formed_unrelated_plugins()
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
            var pluginListJson = JsonSerializer.Serialize(new
            {
                installed = new object[]
                {
                    new { pluginId = "fluxknowledge@fluxknowledge", installed = true, enabled = true },
                    new { pluginId = "user-plugin@user-marketplace", installed = true, enabled = false }
                }
            });
            var runner = new NativePluginStateRunner(
                plan.Codex, installed: true, enabled: true,
                legacyPresentAfterRemoval: false, pluginListJson: pluginListJson);
            var port = new NativeGoLiveWindowsMarketplacePort(capability, plan.Codex, runner);
            _ = await port.ObserveAsync(plan.Codex, CancellationToken.None);

            await port.RegisterAndObserveAsync(plan.Codex, CancellationToken.None);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("{\"available\":[]}")]
    [InlineData("{\"installed\":{}}")]
    [InlineData("{\"installed\":[{}]}")]
    [InlineData("{\"installed\":[{\"pluginId\":17}]}")]
    [InlineData("{\"installed\":[{\"pluginId\":\"\"}]}")]
    [InlineData("[]")]
    [InlineData("{")]
    public async Task Legacy_plugin_removal_rejects_a_plugin_list_without_an_exact_installed_array(string pluginListJson)
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
            var runner = new NativePluginStateRunner(plan.Codex, installed: true, enabled: true,
                legacyPresentAfterRemoval: false, pluginListJson: pluginListJson);
            var port = new NativeGoLiveWindowsMarketplacePort(capability, plan.Codex, runner);

            var exception = await Assert.ThrowsAsync<NativeGoLiveContractException>(
                () => port.RemoveExactLegacyPluginAsync(CancellationToken.None).AsTask());

            Assert.Equal("legacy-plugin-removal-not-proved", exception.ReasonCode);
            Assert.Equal(["plugin-list"], runner.Commands);
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

    private sealed class NativePluginStateRunner(
        NativeGoLiveCodexIdentity identity,
        bool installed,
        bool enabled,
        bool legacyPresentAfterRemoval,
        bool legacyPresentInitially = false,
        string? pluginListJson = null,
        string? marketplaceListJson = null) : INativeCodexMarketplaceCommandRunner
    {
        public List<string> Commands { get; } = [];

        public ValueTask<NativeCodexMarketplaceCommandResult> AddFluxKnowledgeMarketplaceAsync(
            string marketplaceRoot,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<NativeCodexMarketplaceCommandResult> RemoveFluxKnowledgeMarketplaceAsync(
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<NativeCodexMarketplaceCommandResult> ListMarketplacesJsonAsync(
            CancellationToken cancellationToken)
        {
            Commands.Add("marketplace-list");
            return ValueTask.FromResult(new NativeCodexMarketplaceCommandResult(
                0,
                marketplaceListJson ?? JsonSerializer.Serialize(new { marketplaces = new[] { new { name = identity.MarketplaceName, root = identity.MarketplaceRoot } } }),
                new string('0', 64)));
        }

        public ValueTask<NativeCodexMarketplaceCommandResult> AddFluxKnowledgePluginAsync(
            CancellationToken cancellationToken)
        {
            Commands.Add("plugin-add");
            return ValueTask.FromResult(new NativeCodexMarketplaceCommandResult(0, string.Empty, string.Empty));
        }

        public ValueTask<NativeCodexMarketplaceCommandResult> RemoveLegacyFluxLlmKbPluginAsync(
            CancellationToken cancellationToken)
        {
            Commands.Add("legacy-remove");
            return ValueTask.FromResult(new NativeCodexMarketplaceCommandResult(0, string.Empty, string.Empty));
        }

        public ValueTask<NativeCodexMarketplaceCommandResult> ListPluginsJsonAsync(
            CancellationToken cancellationToken)
        {
            Commands.Add("plugin-list");
            if (pluginListJson is not null)
            {
                return ValueTask.FromResult(new NativeCodexMarketplaceCommandResult(
                    0, pluginListJson, string.Empty));
            }
            var plugins = new List<object>();
            if (installed)
            {
                plugins.Add(new
                {
                    pluginId = identity.PluginName + "@" + identity.MarketplaceName,
                    installed,
                    enabled
                });
            }
            var legacyPresent = Commands.Contains("legacy-remove")
                ? legacyPresentAfterRemoval
                : legacyPresentInitially;
            if (legacyPresent)
            {
                plugins.Add(new
                {
                    pluginId = "flux-llm-kb@flux-llm-kb-local",
                    installed = true,
                    enabled = true
                });
            }
            return ValueTask.FromResult(new NativeCodexMarketplaceCommandResult(
                0,
                JsonSerializer.Serialize(new { installed = plugins }),
                string.Empty));
        }
    }
}
