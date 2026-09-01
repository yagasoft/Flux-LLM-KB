using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using FluxKnowledge.Application.Operations;
using FluxKnowledge.Integrations.Codex;
using FluxKnowledge.Integrations.Windows.NativeGoLive;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Codex;

public sealed class NativeCodexPluginRegistrarTests
{
    [Fact]
    public void Normal_registrar_is_status_only_and_native_adapter_construction_is_not_public()
    {
        Assert.Empty(typeof(NativeCodexPluginRegistrar).GetConstructors(BindingFlags.Public | BindingFlags.Instance));
        Assert.False(typeof(ICodexMarketplaceLifecycle).IsPublic);
        Assert.False(typeof(NativeCodexMarketplaceLifecycleAdapter).IsPublic);
        var publicFactories = typeof(NativeCodexPluginRegistrar)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(method => method.ReturnType == typeof(NativeCodexPluginRegistrar))
            .ToArray();

        Assert.Equal("CreateStatusOnly", Assert.Single(publicFactories).Name);
        Assert.Empty(typeof(NativeCodexMarketplaceLifecycleAdapter).GetConstructors());
    }

    [Fact]
    public void Marketplace_has_no_separately_consumable_authority_type()
    {
        var integrationsAssembly = typeof(NativeCodexPluginRegistrar).Assembly;

        Assert.Null(integrationsAssembly.GetType("FluxKnowledge.Integrations.Codex.GoLiveAuthority"));
        Assert.Null(integrationsAssembly.GetType("FluxKnowledge.Integrations.Codex.GoLiveAuthorityIssuer"));
        Assert.Null(integrationsAssembly.GetType("FluxKnowledge.Integrations.Codex.IGoLiveAuthorityValidator"));
    }

    [Fact]
    public async Task Generated_marketplace_passes_the_current_bundled_canonical_plugin_validator()
    {
        await using var fixture = new WriterFixture();
        await fixture.Writer.WriteAsync(fixture.Paths.MarketplaceRoot);

        var validation = await fixture.Writer.ValidateAsync(fixture.Paths.MarketplaceRoot);
        var validator = await RunCanonicalValidatorAsync(fixture.Paths.PluginRoot);

        Assert.True(validation.IsValid, validation.Reason);
        Assert.True(validator.ExitCode == 0, validator.Output);
        Assert.Contains("Plugin validation passed", validator.Output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("capabilities")]
    [InlineData("defaultPrompt")]
    [InlineData("name")]
    public async Task Generated_material_validation_rejects_missing_required_interface_fields_and_name_mismatch(string field)
    {
        await using var fixture = new WriterFixture();
        await fixture.Writer.WriteAsync(fixture.Paths.MarketplaceRoot);
        var manifestPath = Path.Combine(fixture.Paths.PluginRoot, ".codex-plugin", "plugin.json");
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(manifestPath));
        var root = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(document.RootElement.GetRawText())!;
        if (field == "name") root[field] = JsonSerializer.SerializeToElement("not-fluxknowledge");
        else
        {
            var ui = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(root["interface"].GetRawText())!;
            ui.Remove(field);
            root["interface"] = JsonSerializer.SerializeToElement(ui);
        }
        await File.WriteAllTextAsync(manifestPath, JsonSerializer.Serialize(root));

        var validation = await fixture.Writer.ValidateAsync(fixture.Paths.MarketplaceRoot);

        Assert.False(validation.IsValid);
        Assert.Equal("plugin-material-invalid", validation.Reason);
    }

    [Fact]
    public async Task Generated_material_validation_rejects_a_second_or_remote_server()
    {
        await using var fixture = new WriterFixture();
        await fixture.Writer.WriteAsync(fixture.Paths.MarketplaceRoot);
        await File.WriteAllTextAsync(
            Path.Combine(fixture.Paths.PluginRoot, ".mcp.json"),
            "{\"mcpServers\":{\"fluxknowledge\":{\"type\":\"http\",\"url\":\"http://127.0.0.1:5137/mcp\"},\"remote\":{\"type\":\"http\",\"url\":\"https://example.test/mcp\"}}}");

        var validation = await fixture.Writer.ValidateAsync(fixture.Paths.MarketplaceRoot);

        Assert.False(validation.IsValid);
        Assert.Equal("plugin-material-invalid", validation.Reason);
    }

    [Fact]
    public async Task Manifest_writer_rejects_a_reparse_segment_without_writing_through_it()
    {
        await using var fixture = new WriterFixture();
        Directory.CreateDirectory(fixture.Paths.MarketplaceRoot);
        var external = Path.Combine(
            Path.GetTempPath(),
            "FluxKnowledgeNativeCodexExternalTarget",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(external);
        Directory.CreateSymbolicLink(Path.Combine(fixture.Paths.MarketplaceRoot, "plugins"), external);
        try
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
                fixture.Writer.WriteAsync(fixture.Paths.MarketplaceRoot));

            Assert.Empty(Directory.EnumerateFileSystemEntries(external));
        }
        finally
        {
            if (Directory.Exists(external)) Directory.Delete(external, recursive: true);
        }
    }

    [Fact]
    public async Task Status_only_repair_never_writes_or_runs_a_marketplace_process()
    {
        await using var fixture = new WriterFixture();
        var registrar = NativeCodexPluginRegistrar.CreateStatusOnly(fixture.Paths, fixture.Writer);

        var repair = await registrar.RepairAsync();

        Assert.False(repair.Changed);
        Assert.Equal("go-live-authority-required", repair.Reason);
        Assert.False(Directory.Exists(fixture.Paths.PluginRoot));
    }

    [Fact]
    public async Task Marketplace_uses_only_add_then_bounded_list_verification()
    {
        await using var fixture = new NativeMarketplaceFixture();
        var runner = new RecordingMarketplaceCommandRunner(
            MarketplaceListJson(fixture.Identity),
            fixture.BeforeStructuralHash);
        var adapter = fixture.CreateAdapter(
            runner,
            new CodexMarketplacePreflight(CodexMarketplaceLifecycleState.Missing, null, fixture.BeforeStructuralHash));

        var result = await adapter.RegisterAsync(fixture.Identity, CancellationToken.None);

        Assert.Equal(
            ["codex plugin marketplace add", "codex plugin marketplace list --json"],
            runner.Commands);
        Assert.Equal(fixture.Identity.MarketplaceRoot, runner.AddedMarketplaceRoot);
        Assert.True(runner.ListCancellationWasBounded);
        Assert.True(result.IsHealthy, result.Reason);
        Assert.True(result.Changed);
    }

    [Theory]
    [InlineData(true, "marketplace-add-timeout", 1)]
    [InlineData(false, "marketplace-verification-timeout", 2)]
    public async Task Marketplace_enforces_timeout_when_runner_never_completes(
        bool stallAdd,
        string expectedReason,
        int expectedCommandCount)
    {
        await using var fixture = new NativeMarketplaceFixture();
        var runner = new NonCompletingMarketplaceCommandRunner(
            stallAdd,
            MarketplaceListJson(fixture.Identity),
            fixture.BeforeStructuralHash);
        var adapter = fixture.CreateAdapter(
            runner,
            new CodexMarketplacePreflight(CodexMarketplaceLifecycleState.Missing, null, fixture.BeforeStructuralHash),
            TimeSpan.FromMilliseconds(40));

        var result = await adapter.RegisterAsync(fixture.Identity, CancellationToken.None)
            .AsTask()
            .WaitAsync(TimeSpan.FromSeconds(1));

        Assert.False(result.IsHealthy);
        Assert.True(result.Changed);
        Assert.Equal(expectedReason, result.Reason);
        Assert.Equal(expectedCommandCount, runner.Commands.Count);
    }

    [Fact]
    public async Task Foreign_marketplace_fails_before_writer_or_process_mutation()
    {
        await using var fixture = new NativeMarketplaceFixture();
        var runner = new RecordingMarketplaceCommandRunner(
            MarketplaceListJson(fixture.Identity),
            fixture.BeforeStructuralHash);
        var adapter = fixture.CreateAdapter(
            runner,
            new CodexMarketplacePreflight(
                CodexMarketplaceLifecycleState.Foreign,
                fixture.Identity.MarketplaceRoot + "-foreign",
                fixture.BeforeStructuralHash));

        var result = await adapter.RegisterAsync(fixture.Identity, CancellationToken.None);

        Assert.False(result.IsHealthy);
        Assert.False(result.Changed);
        Assert.Equal("foreign-registration", result.Reason);
        Assert.Empty(runner.Commands);
        Assert.False(Directory.Exists(Path.Combine(fixture.Identity.MarketplaceRoot, "plugins")));
    }

    [Fact]
    public async Task Exact_existing_marketplace_rehydrates_the_app_owned_plugin_material_without_readding()
    {
        await using var fixture = new NativeMarketplaceFixture();
        var runner = new RecordingMarketplaceCommandRunner(
            MarketplaceListJson(fixture.Identity),
            fixture.BeforeStructuralHash);
        var adapter = fixture.CreateAdapter(
            runner,
            new CodexMarketplacePreflight(
                CodexMarketplaceLifecycleState.Registered,
                fixture.Identity.MarketplaceRoot,
                fixture.BeforeStructuralHash));

        var result = await adapter.RegisterAsync(fixture.Identity, CancellationToken.None);

        Assert.True(result.IsHealthy, result.Reason);
        Assert.False(result.Changed);
        Assert.Empty(runner.Commands);
        Assert.True(Directory.Exists(Path.Combine(fixture.Identity.MarketplaceRoot, "plugins")));
    }

    [Fact]
    public async Task Marketplace_verification_rejects_unrelated_configuration_hash_change()
    {
        await using var fixture = new NativeMarketplaceFixture();
        var runner = new RecordingMarketplaceCommandRunner(
            MarketplaceListJson(fixture.Identity),
            new string('f', 64));
        var adapter = fixture.CreateAdapter(
            runner,
            new CodexMarketplacePreflight(CodexMarketplaceLifecycleState.Missing, null, fixture.BeforeStructuralHash));

        var result = await adapter.RegisterAsync(fixture.Identity, CancellationToken.None);

        Assert.False(result.IsHealthy);
        Assert.True(result.Changed);
        Assert.Equal("unrelated-configuration-changed", result.Reason);
        Assert.Equal(
            ["codex plugin marketplace add", "codex plugin marketplace list --json"],
            runner.Commands);
    }

    [Fact]
    public async Task Marketplace_adapter_rejects_an_identity_change_before_writer_or_process_mutation()
    {
        await using var fixture = new NativeMarketplaceFixture();
        var runner = new RecordingMarketplaceCommandRunner(
            MarketplaceListJson(fixture.Identity),
            fixture.BeforeStructuralHash);
        var adapter = fixture.CreateAdapter(
            runner,
            new CodexMarketplacePreflight(CodexMarketplaceLifecycleState.Missing, null, fixture.BeforeStructuralHash));
        var foreign = fixture.Identity with { MarketplaceRoot = fixture.Identity.MarketplaceRoot + "-foreign" };

        var result = await adapter.RegisterAsync(foreign, CancellationToken.None);

        Assert.False(result.IsHealthy);
        Assert.Equal("foreign-registration", result.Reason);
        Assert.Empty(runner.Commands);
        Assert.False(Directory.Exists(Path.Combine(fixture.Identity.MarketplaceRoot, "plugins")));
    }

    [Fact]
    public async Task Native_marketplace_adapter_requires_consumed_one_shot_capability()
    {
        await using var fixture = new NativeMarketplaceFixture(claimAuthority: false);
        var runner = new RecordingMarketplaceCommandRunner(
            MarketplaceListJson(fixture.Identity),
            fixture.BeforeStructuralHash);

        var exception = Assert.Throws<InvalidOperationException>(() => fixture.CreateAdapter(
            runner,
            new CodexMarketplacePreflight(CodexMarketplaceLifecycleState.Missing, null, fixture.BeforeStructuralHash)));

        Assert.Equal("go-live-closeout-capability-not-consumed", exception.Message);
        Assert.Empty(runner.Commands);
        Assert.False(Directory.Exists(Path.Combine(fixture.Identity.MarketplaceRoot, "plugins")));
    }

    private static string MarketplaceListJson(NativeGoLiveCodexIdentity identity) => JsonSerializer.Serialize(new
    {
        marketplaces = new[]
        {
            new
            {
                name = identity.MarketplaceName,
                root = identity.MarketplaceRoot
            }
        }
    });

    private static async Task<(int ExitCode, string Output)> RunCanonicalValidatorAsync(string pluginRoot)
    {
        var validator = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".codex",
            "skills",
            ".system",
            "plugin-creator",
            "scripts",
            "validate_plugin.py");
        var shimRoot = Path.Combine(Path.GetTempPath(), "FluxKnowledgeValidatorShim", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(shimRoot);
        await File.WriteAllTextAsync(Path.Combine(shimRoot, "yaml.py"), "def safe_load(value):\n    return {}\n");
        var start = new ProcessStartInfo("python")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        start.ArgumentList.Add(validator);
        start.ArgumentList.Add(pluginRoot);
        start.Environment["PYTHONPATH"] = shimRoot;
        try
        {
            using var process = Process.Start(start)
                ?? throw new InvalidOperationException("Unable to start the bundled plugin validator.");
            var output = await process.StandardOutput.ReadToEndAsync();
            output += await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            return (process.ExitCode, output);
        }
        finally
        {
            Directory.Delete(shimRoot, recursive: true);
        }
    }

    private sealed class WriterFixture : IAsyncDisposable
    {
        private readonly string _root = Path.Combine(
            Path.GetTempPath(),
            "FluxKnowledgeNativeCodexWriterTests",
            Guid.NewGuid().ToString("N"));

        public WriterFixture()
        {
            Directory.CreateDirectory(_root);
            Paths = CodexRegistrationPaths.CreateForIsolatedTests(Path.Combine(_root, "CodexPlugin"));
        }

        public CodexRegistrationPaths Paths { get; }
        public NativeCodexPluginManifestWriter Writer { get; } = new();

        public ValueTask DisposeAsync()
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class NativeMarketplaceFixture : IAsyncDisposable
    {
        private readonly string _root = Path.Combine(
            Path.GetTempPath(),
            "FluxKnowledgeNativeCodexGoLiveTests",
            Guid.NewGuid().ToString("N"));

        public NativeMarketplaceFixture(bool claimAuthority = true)
        {
            Directory.CreateDirectory(_root);
            Identity = new NativeGoLiveCodexIdentity(
                Path.Combine(_root, "CodexPlugin"),
                CodexRegistrationPaths.MarketplaceName,
                CodexRegistrationPaths.PluginName);
            Directory.CreateDirectory(Identity.MarketplaceRoot);
            Capability = NativeGoLiveProvisioningCapability.CreateForIsolatedTests(claimAuthority);
        }

        public NativeGoLiveCodexIdentity Identity { get; }
        public string BeforeStructuralHash { get; } = new('b', 64);
        private NativeGoLiveProvisioningCapability Capability { get; }

        public NativeCodexMarketplaceLifecycleAdapter CreateAdapter(
            INativeCodexMarketplaceCommandRunner runner,
            CodexMarketplacePreflight preflight,
            TimeSpan? commandTimeout = null) =>
            new(
                Capability,
                Identity,
                new NativeCodexPluginManifestWriter(),
                runner,
                preflight,
                commandTimeout ?? TimeSpan.FromSeconds(2));

        public ValueTask DisposeAsync()
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingMarketplaceCommandRunner(
        string listJson,
        string afterStructuralHash) : INativeCodexMarketplaceCommandRunner
    {
        public List<string> Commands { get; } = [];
        public string? AddedMarketplaceRoot { get; private set; }
        public bool ListCancellationWasBounded { get; private set; }

        public ValueTask<NativeCodexMarketplaceCommandResult> AddFluxKnowledgeMarketplaceAsync(
            string marketplaceRoot,
            CancellationToken cancellationToken)
        {
            Commands.Add("codex plugin marketplace add");
            AddedMarketplaceRoot = marketplaceRoot;
            return ValueTask.FromResult(new NativeCodexMarketplaceCommandResult(0, string.Empty, string.Empty));
        }

        public ValueTask<NativeCodexMarketplaceCommandResult> ListMarketplacesJsonAsync(
            CancellationToken cancellationToken)
        {
            Commands.Add("codex plugin marketplace list --json");
            ListCancellationWasBounded = cancellationToken.CanBeCanceled;
            return ValueTask.FromResult(new NativeCodexMarketplaceCommandResult(0, listJson, afterStructuralHash));
        }
    }

    private sealed class NonCompletingMarketplaceCommandRunner(
        bool stallAdd,
        string listJson,
        string afterStructuralHash) : INativeCodexMarketplaceCommandRunner
    {
        private readonly TaskCompletionSource<NativeCodexMarketplaceCommandResult> _never =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public List<string> Commands { get; } = [];

        public ValueTask<NativeCodexMarketplaceCommandResult> AddFluxKnowledgeMarketplaceAsync(
            string marketplaceRoot,
            CancellationToken cancellationToken)
        {
            Commands.Add("codex plugin marketplace add");
            return stallAdd
                ? new ValueTask<NativeCodexMarketplaceCommandResult>(_never.Task)
                : ValueTask.FromResult(new NativeCodexMarketplaceCommandResult(0, string.Empty, string.Empty));
        }

        public ValueTask<NativeCodexMarketplaceCommandResult> ListMarketplacesJsonAsync(
            CancellationToken cancellationToken)
        {
            Commands.Add("codex plugin marketplace list --json");
            return stallAdd
                ? ValueTask.FromResult(new NativeCodexMarketplaceCommandResult(0, listJson, afterStructuralHash))
                : new ValueTask<NativeCodexMarketplaceCommandResult>(_never.Task);
        }
    }
}
