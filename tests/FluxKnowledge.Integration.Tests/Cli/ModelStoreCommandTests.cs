using System.Text.Json;
using FluxKnowledge.Cli;
using FluxKnowledge.Cli.Commands;
using FluxKnowledge.Integration.Tests.Models;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Cli;

public sealed class ModelStoreCommandTests
{
    [Fact]
    public async Task Verify_command_reports_local_success_without_initialising_a_service_host()
    {
        await using var fixture = await LocalModelFixture.CreateAsync();
        var manifest = await fixture.SeedCompleteBundleAsync();
        var manifestPath = await fixture.WriteManifestAsync(manifest);
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = await ModelStoreCommand.ExecuteAsync(
            ["verify", "--manifest", manifestPath],
            fixture.Store,
            output,
            error);

        Assert.Equal(0, exitCode);
        Assert.Empty(error.ToString());
        using var document = JsonDocument.Parse(output.ToString());
        Assert.True(document.RootElement.GetProperty("succeeded").GetBoolean());
        Assert.Equal("model-bundle-verified", document.RootElement.GetProperty("reasonCode").GetString());
        Assert.True(document.RootElement.GetProperty("receiptPersisted").GetBoolean());
    }

    [Fact]
    public async Task Verify_command_rejects_root_override_before_opening_a_store()
    {
        await using var fixture = await LocalModelFixture.CreateAsync();
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = await ModelStoreCommand.ExecuteAsync(
            ["verify", "--root", "C:\\not-a-model-store"],
            fixture.Store,
            output,
            error);

        Assert.Equal(2, exitCode);
        Assert.Empty(output.ToString());
        Assert.Contains("Usage:", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Verify_command_rejects_non_utf8_manifest_bytes_without_using_another_encoding()
    {
        await using var fixture = await LocalModelFixture.CreateAsync();
        var manifestPath = await fixture.WriteRawManifestAsync([0xff, 0xfe, 0xfd]);
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = await ModelStoreCommand.ExecuteAsync(
            ["verify", "--manifest", manifestPath],
            fixture.Store,
            output,
            error);

        Assert.Equal(2, exitCode);
        Assert.Empty(output.ToString());
        Assert.Contains("invalid", error.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Verify_command_rejects_a_unc_manifest_before_any_remote_open_or_store_access()
    {
        await using var fixture = await LocalModelFixture.CreateAsync();
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = await ModelStoreCommand.ExecuteAsync(
            ["verify", "--manifest", @"\\server\share\model-manifest.json"],
            fixture.Store,
            output,
            error);

        Assert.Equal(2, exitCode);
        Assert.Empty(output.ToString());
        Assert.Contains("Usage:", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Verify_command_returns_a_refusal_exit_code_for_a_missing_companion()
    {
        await using var fixture = await LocalModelFixture.CreateAsync();
        var manifest = await fixture.SeedCompleteBundleAsync();
        var manifestPath = await fixture.WriteManifestAsync(manifest);
        await fixture.RemoveAsync(manifest.Files[1]);
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = await ModelStoreCommand.ExecuteAsync(
            ["verify", "--manifest", manifestPath],
            fixture.Store,
            output,
            error);

        Assert.Equal(1, exitCode);
        Assert.Empty(error.ToString());
        using var result = JsonDocument.Parse(output.ToString());
        Assert.False(result.RootElement.GetProperty("succeeded").GetBoolean());
        Assert.Equal("model-artifact-missing", result.RootElement.GetProperty("reasonCode").GetString());
    }

    [Fact]
    public async Task Public_cli_dispatches_an_invalid_models_invocation_without_opening_the_production_store()
    {
        var exitCode = await CliProgram.Main(["models", "verify", "--root", "C:\\not-a-model-store"]);

        Assert.Equal(2, exitCode);
    }
}
