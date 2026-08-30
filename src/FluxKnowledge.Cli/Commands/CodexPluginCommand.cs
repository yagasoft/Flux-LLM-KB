using System.Text.Json;
using FluxKnowledge.Integrations.Codex;

namespace FluxKnowledge.Cli.Commands;

/// <summary>Explicit operator commands for checking the known marketplace; repair needs typed go-live authority.</summary>
public static class CodexPluginCommand
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public static async Task<int> ExecuteFromEnvironmentAsync(
        string[] args,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken = default)
    {
        var registrar = NativeCodexPluginRegistrar.CreateStatusOnly(
            CodexRegistrationPaths.Production,
            new NativeCodexPluginManifestWriter());
        return await ExecuteAsync(args, registrar, output, error, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<int> ExecuteAsync(
        string[] args,
        NativeCodexPluginRegistrar registrar,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(registrar);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);
        if (args.Length != 2 || args[0] != "plugin" || args[1] is not ("status" or "repair")) return await WriteFailureAsync(output, "invalid-command").ConfigureAwait(false);

        var repair = args[1] == "repair"
            ? await registrar.RepairAsync(cancellationToken).ConfigureAwait(false)
            : null;
        var status = repair?.Status ?? await registrar.StatusAsync(cancellationToken).ConfigureAwait(false);
        var ok = status.Health == NativeCodexPluginHealth.Healthy && repair?.Reason is null;
        await output.WriteLineAsync(JsonSerializer.Serialize(new
        {
            ok,
            result = new { health = status.Health.ToString().ToLowerInvariant(), changed = repair?.Changed },
            reasonCode = repair?.Reason ?? status.Reason,
            message = status.Health == NativeCodexPluginHealth.Healthy ? null : "The request could not be completed.",
            retryable = false
        }, SerializerOptions)).ConfigureAwait(false);
        return ok ? 0 : 1;
    }

    private static async Task<int> WriteFailureAsync(TextWriter output, string reasonCode)
    {
        await output.WriteLineAsync(JsonSerializer.Serialize(new { ok = false, result = (object?)null, reasonCode, message = "The request could not be completed.", retryable = false }, SerializerOptions)).ConfigureAwait(false);
        return 1;
    }
}
