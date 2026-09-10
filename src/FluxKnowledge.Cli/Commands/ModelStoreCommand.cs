using System.Text.Json;
using FluxKnowledge.Application.Models;
using FluxKnowledge.Infrastructure.Inference.Models;
using FluxKnowledge.Integrations.Models;

namespace FluxKnowledge.Cli.Commands;

public static class ModelStoreCommand
{
    public static Task<int> ExecuteAsync(
        string[] args,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken = default) =>
        ExecuteCoreAsync(
            args,
            () => new LocalModelStore(WindowsModelVerificationFiles.OpenProduction),
            output,
            error,
            cancellationToken);

    internal static Task<int> ExecuteAsync(
        string[] args,
        ILocalModelStore store,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        return ExecuteCoreAsync(args, () => store, output, error, cancellationToken);
    }

    private static async Task<int> ExecuteCoreAsync(
        string[] args,
        Func<ILocalModelStore> openStore,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(openStore);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);
        if (!TryReadManifestPath(args, out var manifestPath))
        {
            return await WriteUsageAsync(error).ConfigureAwait(false);
        }

        ModelBundleSpecification manifest;
        try
        {
            manifest = await ReadManifestAsync(manifestPath, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ModelManifestException)
        {
            await error.WriteLineAsync("The local model manifest is unavailable or invalid.").ConfigureAwait(false);
            return 2;
        }

        var store = openStore();
        var result = await store.ResolveAsync(manifest, cancellationToken).ConfigureAwait(false);
        result.Lease?.Dispose();
        await output.WriteLineAsync(JsonSerializer.Serialize(new
        {
            result.Succeeded,
            result.ReasonCode,
            result.BundleFingerprint,
            files = result.Files.Select(static file => new
            {
                file.Filename,
                file.ReasonCode,
                file.ObservedByteLength,
                file.ObservedSha256
            }),
            result.ReceiptPersisted,
            result.ReceiptLocation
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web))).ConfigureAwait(false);
        return result.Succeeded ? 0 : 1;
    }

    private static bool TryReadManifestPath(string[] args, out string manifestPath)
    {
        manifestPath = string.Empty;
        if (args.Length != 3 || args[0] != "verify" || args[1] != "--manifest" || string.IsNullOrWhiteSpace(args[2]))
        {
            return false;
        }

        if (args[2].StartsWith(@"\\", StringComparison.Ordinal) ||
            args[2].StartsWith("//", StringComparison.Ordinal) ||
            args[2].StartsWith("file:", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            manifestPath = Path.GetFullPath(args[2]);
            return Path.IsPathFullyQualified(manifestPath) &&
                   !manifestPath.StartsWith(@"\\", StringComparison.Ordinal) &&
                   !manifestPath.StartsWith("//", StringComparison.Ordinal);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    private static async Task<ModelBundleSpecification> ReadManifestAsync(string manifestPath, CancellationToken cancellationToken)
    {
        var bytes = await WindowsModelVerificationFiles.ReadLocalManifestAsync(
                manifestPath,
                ModelManifestCodec.MaximumBytes,
                cancellationToken)
            .ConfigureAwait(false);
        return ModelManifestCodec.Parse(bytes);
    }

    private static async Task<int> WriteUsageAsync(TextWriter error)
    {
        await error.WriteLineAsync("Usage: FluxKnowledge.Cli models verify --manifest <local-file>").ConfigureAwait(false);
        return 2;
    }
}
