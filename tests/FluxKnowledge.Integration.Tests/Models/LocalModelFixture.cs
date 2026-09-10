using System.Security.Cryptography;
using System.Text.Json;
using FluxKnowledge.Application.Models;
using FluxKnowledge.Infrastructure.Inference.Models;
using FluxKnowledge.Integrations.Models;

namespace FluxKnowledge.Integration.Tests.Models;

internal sealed class LocalModelFixture : IAsyncDisposable
{
    private LocalModelFixture(string root)
    {
        Root = root;
        ModelRoot = Path.Combine(root, "Models");
        Directory.CreateDirectory(ModelRoot);
    }

    internal string Root { get; }

    internal string ModelRoot { get; }

    internal ILocalModelStore Store => CreateStore();

    internal ILocalModelStore CreateStore(
        WindowsModelVerificationTestOptions? options = null,
        Func<Guid>? nextReceiptId = null) =>
        new LocalModelStore(() => WindowsModelVerificationFiles.OpenForTest(ModelRoot, options), TimeProvider.System, nextReceiptId);

    internal static Task<LocalModelFixture> CreateAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "FluxKnowledgeModelStoreTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return Task.FromResult(new LocalModelFixture(root));
    }

    internal async Task<ModelBundleSpecification> SeedCompleteBundleAsync()
    {
        var content = "flux-model"u8.ToArray();
        var companion = "{}"u8.ToArray();
        var files = new[]
        {
            await SeedAsync("weights.bin", content),
            await SeedAsync("processor.json", companion)
        };
        return new ModelBundleSpecification(1, files);
    }

    internal Task<bool> ReceiptExistsAsync(string? receiptLocation) => Task.FromResult(
        receiptLocation is not null && File.Exists(Path.Combine(ModelRoot, receiptLocation.Replace('/', Path.DirectorySeparatorChar))));

    internal Task<string> ReadReceiptAsync(string? receiptLocation)
    {
        ArgumentNullException.ThrowIfNull(receiptLocation);
        return File.ReadAllTextAsync(Path.Combine(ModelRoot, receiptLocation.Replace('/', Path.DirectorySeparatorChar)));
    }

    internal async Task<string> WriteManifestAsync(ModelBundleSpecification manifest)
    {
        var inputDirectory = Path.Combine(Root, "input");
        Directory.CreateDirectory(inputDirectory);
        var path = Path.Combine(inputDirectory, "model-manifest.json");
        await File.WriteAllBytesAsync(
            path,
            JsonSerializer.SerializeToUtf8Bytes(manifest, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        return path;
    }

    internal async Task<string> WriteRawManifestAsync(byte[] bytes)
    {
        var inputDirectory = Path.Combine(Root, "input");
        Directory.CreateDirectory(inputDirectory);
        var path = Path.Combine(inputDirectory, "raw-model-manifest.json");
        await File.WriteAllBytesAsync(path, bytes);
        return path;
    }

    internal string ArtifactPath(ModelArtifactSpecification specification) =>
        Path.Combine(ModelRoot, "artifacts", "sha256", specification.Sha256, specification.Filename);

    internal bool ArtifactExists(ModelArtifactSpecification specification) => File.Exists(ArtifactPath(specification));

    internal Task RemoveAsync(ModelArtifactSpecification specification)
    {
        File.Delete(ArtifactPath(specification));
        return Task.CompletedTask;
    }

    internal async Task PlaceContentAtAsync(
        ModelArtifactSpecification destination,
        ModelArtifactSpecification source)
    {
        var directory = Path.GetDirectoryName(ArtifactPath(destination))
            ?? throw new InvalidOperationException("The synthetic artifact has no parent directory.");
        Directory.CreateDirectory(directory);
        await File.WriteAllBytesAsync(ArtifactPath(destination), await File.ReadAllBytesAsync(ArtifactPath(source)));
    }

    internal async Task<string> ReplaceArtifactsWithReparsePointAsync()
    {
        var artifacts = Path.Combine(ModelRoot, "artifacts");
        var parked = Path.Combine(Root, "artifacts-parked");
        var outside = Path.Combine(Root, "outside");
        Directory.Move(artifacts, parked);
        Directory.CreateDirectory(outside);
        var sentinel = Path.Combine(outside, "sentinel.txt");
        await File.WriteAllTextAsync(sentinel, "outside");
        Directory.CreateSymbolicLink(artifacts, outside);
        return sentinel;
    }

    internal async Task<(ILocalModelStore Store, string OutsideSentinel)> CreateRootReparseStoreAsync()
    {
        var parked = Path.Combine(Root, "Models-parked");
        var outside = Path.Combine(Root, "root-outside");
        Directory.Move(ModelRoot, parked);
        Directory.CreateDirectory(outside);
        var sentinel = Path.Combine(outside, "sentinel.txt");
        await File.WriteAllTextAsync(sentinel, "outside");
        Directory.CreateSymbolicLink(ModelRoot, outside);
        return (new LocalModelStore(() => WindowsModelVerificationFiles.OpenForTest(ModelRoot)), sentinel);
    }

    internal async Task<string> CreateReceiptAncestorReparsePointAsync()
    {
        var inventory = Path.Combine(ModelRoot, "inventory");
        var outside = Path.Combine(Root, "receipt-outside");
        Directory.CreateDirectory(outside);
        var sentinel = Path.Combine(outside, "sentinel.txt");
        await File.WriteAllTextAsync(sentinel, "outside");
        Directory.CreateSymbolicLink(inventory, outside);
        return sentinel;
    }

    internal async Task<string> CreateReceiptDirectoryBlockerAsync()
    {
        var inventory = Path.Combine(ModelRoot, "inventory");
        Directory.CreateDirectory(inventory);
        var blocker = Path.Combine(inventory, "verifications");
        await File.WriteAllTextAsync(blocker, "not-a-directory");
        return blocker;
    }

    internal async Task<string> ReplaceArtifactWithReparsePointAsync(ModelArtifactSpecification specification)
    {
        var payload = ArtifactPath(specification);
        var outside = Path.Combine(Root, "payload-outside.txt");
        await File.WriteAllTextAsync(outside, "outside");
        File.Delete(payload);
        File.CreateSymbolicLink(payload, outside);
        return outside;
    }

    public ValueTask DisposeAsync()
    {
        if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        return ValueTask.CompletedTask;
    }

    private async Task<ModelArtifactSpecification> SeedAsync(string filename, byte[] content)
    {
        var sha256 = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
        var directory = Path.Combine(ModelRoot, "artifacts", "sha256", sha256);
        Directory.CreateDirectory(directory);
        await File.WriteAllBytesAsync(Path.Combine(directory, filename), content);
        return new ModelArtifactSpecification(
            new string('a', 40),
            filename,
            sha256,
            content.LongLength);
    }
}
