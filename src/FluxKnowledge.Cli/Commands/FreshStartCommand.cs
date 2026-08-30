using System.Text.Json;
using FluxKnowledge.Application.Operations;
using FluxKnowledge.Integrations.Windows;

namespace FluxKnowledge.Cli.Commands;

/// <summary>Emits the guarded live-root/VSS plan. Task 6 deliberately exposes no executor.</summary>
public static class FreshStartCommand
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public static async Task<int> ExecuteAsync(
        string[] args,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);
        cancellationToken.ThrowIfCancellationRequested();

        if (args.Length != 1 || !string.Equals(args[0], FreshStartPlan.RequiredMode, StringComparison.Ordinal))
        {
            await error.WriteLineAsync("Usage: FluxKnowledge.Cli fresh-start").ConfigureAwait(false);
            return 2;
        }

        var plan = FreshStartPlan.CreateProduction(args[0]);
        var vss = VssRecoveryPolicy.CreatePlan(plan.Layout);
        await output.WriteLineAsync(JsonSerializer.Serialize(new
        {
            root = plan.Layout.Root,
            executionAvailable = false,
            reasonCode = "live-execution-unavailable",
            layout = new
            {
                app = plan.Layout.ApplicationRoot,
                config = plan.Layout.ConfigRoot,
                sqlDataFile = plan.Layout.SqlDataFilePath,
                sqlLogFile = plan.Layout.SqlLogFilePath,
                index = plan.Layout.IndexRoot,
                retained = plan.Layout.RetainedRoot,
                spool = plan.Layout.SpoolRoot,
                temp = plan.Layout.TempRoot,
                logs = plan.Layout.LogsRoot,
                codexPlugin = plan.Layout.CodexPluginRoot,
                recovery = plan.Layout.RecoveryRoot
            },
            vss = new
            {
                vss.Volume,
                vss.MaximumStorageFraction
            }
        }, SerializerOptions)).ConfigureAwait(false);
        return 0;
    }
}
