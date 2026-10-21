using System.Text;
using System.Text.Json;
using FluxKnowledge.Integrations.Files;

namespace FluxKnowledge.Cli.Commands;

public static class SourceAccessCommand
{
    // Deliberately bypasses application/SQL/model composition. The privileged
    // process accepts only this closed command family and its fixed read grant.
    public static async Task<int> ExecuteAsync(string[] args, TextWriter output, TextWriter error,
        CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows())
        { await error.WriteLineAsync("Source-access administration requires Windows."); return 2; }
        try
        {
            byte[]? result = null;
            if (args is ["plan-root-read", "--request", var requestPath])
            {
                using var file = new FileStream(requestPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (file.Length is <= 0 or > 2 * 1024 * 1024) throw new InvalidDataException("SourceAccessRootRequestInvalid");
                var request = new byte[checked((int)file.Length)];
                await file.ReadExactlyAsync(request, cancellationToken);
                result = WindowsSourceAccessAdministration.PlanRootReadGrant(request);
            }
            else if (args is ["apply-root-read", "--broker-root", var root, "--plan-sha256", var sha])
                result = WindowsSourceAccessAdministration.ApplyRootReadGrant(root, sha);
            else if (args is ["serve", "--broker-root", var brokerRoot])
            {
                using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                ConsoleCancelEventHandler cancel = (_, eventArgs) => { eventArgs.Cancel = true; lifetime.Cancel(); };
                Console.CancelKeyPress += cancel;
                try { await WindowsSourceAccessAdministration.ServeAsync(brokerRoot, lifetime.Token); }
                finally { Console.CancelKeyPress -= cancel; }
                return 0;
            }
            else
            {
                await error.WriteLineAsync("Usage: source-access plan-root-read --request <json> | apply-root-read --broker-root <protected folder> --plan-sha256 <approved hash> | serve --broker-root <protected folder>");
                return 2;
            }
            await output.WriteLineAsync(Encoding.UTF8.GetString(result));
            return 0;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { return 0; }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException or FormatException or System.ComponentModel.Win32Exception)
        {
            await error.WriteLineAsync(exception.Message);
            return 1;
        }
    }
}
