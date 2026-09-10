using System.Text.Json;
using FluxKnowledge.Application.Models;
using FluxKnowledge.Infrastructure.Inference.Models;
using FluxKnowledge.Integrations.Models;
using FluxKnowledge.Integrations.Windows.NativeGoLive;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Models;

public sealed class LocalModelStoreTests
{
    [Fact]
    public async Task Complete_bundle_returns_a_held_lease_and_an_immutable_receipt()
    {
        await using var fixture = await LocalModelFixture.CreateAsync();
        var manifest = await fixture.SeedCompleteBundleAsync();

        var result = await fixture.Store.ResolveAsync(manifest, CancellationToken.None);

        Assert.True(result.Succeeded, result.ReasonCode);
        Assert.True(result.ReceiptPersisted);
        var lease = Assert.IsType<VerifiedLocalModelLease>(result.Lease);
        Assert.Equal("model-bundle-verified", result.ReasonCode);
        Assert.True(await fixture.ReceiptExistsAsync(result.ReceiptLocation));

        var buffer = new byte[4];
        var read = await lease.ReadAsync("weights.bin", 0, buffer, CancellationToken.None);
        Assert.Equal(4, read);
        Assert.Equal("flux", System.Text.Encoding.UTF8.GetString(buffer));

        lease.Dispose();
    }

    [Fact]
    public async Task Missing_companion_refuses_without_recreating_the_payload()
    {
        await using var fixture = await LocalModelFixture.CreateAsync();
        var manifest = await fixture.SeedCompleteBundleAsync();
        await fixture.RemoveAsync(manifest.Files[1]);

        var result = await fixture.Store.ResolveAsync(manifest, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal("model-artifact-missing", result.ReasonCode);
        Assert.Null(result.Lease);
        Assert.True(result.ReceiptPersisted);
        Assert.False(fixture.ArtifactExists(manifest.Files[1]));
    }

    [Fact]
    public async Task Incorrect_declared_length_refuses_before_a_receipt_can_become_a_lease()
    {
        await using var fixture = await LocalModelFixture.CreateAsync();
        var manifest = await fixture.SeedCompleteBundleAsync();
        var wrongLength = manifest.Files[0] with { ByteLength = manifest.Files[0].ByteLength + 1 };
        var invalidManifest = new ModelBundleSpecification(1, [wrongLength, manifest.Files[1]]);

        var result = await fixture.Store.ResolveAsync(invalidManifest, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal("model-artifact-length-mismatch", result.ReasonCode);
        Assert.Null(result.Lease);
        Assert.True(result.ReceiptPersisted);
    }

    [Fact]
    public async Task Incorrect_declared_hash_refuses_after_a_fresh_read()
    {
        await using var fixture = await LocalModelFixture.CreateAsync();
        var manifest = await fixture.SeedCompleteBundleAsync();
        var wrongHash = manifest.Files[0] with { Sha256 = new string('0', 64) };
        await fixture.PlaceContentAtAsync(wrongHash, manifest.Files[0]);
        var invalidManifest = new ModelBundleSpecification(1, [wrongHash, manifest.Files[1]]);

        var result = await fixture.Store.ResolveAsync(invalidManifest, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal("model-artifact-sha256-mismatch", result.ReasonCode);
        Assert.Null(result.Lease);
        Assert.True(result.ReceiptPersisted);
    }

    [Fact]
    public async Task Reparse_payload_ancestor_refuses_without_touching_its_target()
    {
        await using var fixture = await LocalModelFixture.CreateAsync();
        var manifest = await fixture.SeedCompleteBundleAsync();
        var outsideSentinel = await fixture.ReplaceArtifactsWithReparsePointAsync();

        var result = await fixture.Store.ResolveAsync(manifest, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal("model-path-unsafe", result.ReasonCode);
        Assert.Null(result.Lease);
        Assert.Equal("outside", await File.ReadAllTextAsync(outsideSentinel));
    }

    [Fact]
    public async Task Held_lease_denies_payload_writes_until_disposal()
    {
        await using var fixture = await LocalModelFixture.CreateAsync();
        var manifest = await fixture.SeedCompleteBundleAsync();
        var result = await fixture.Store.ResolveAsync(manifest, CancellationToken.None);
        var lease = Assert.IsType<VerifiedLocalModelLease>(result.Lease);

        Assert.Throws<IOException>(() => File.Open(fixture.ArtifactPath(manifest.Files[0]), FileMode.Open, FileAccess.Write, FileShare.None));

        lease.Dispose();

        await using var writable = File.Open(fixture.ArtifactPath(manifest.Files[0]), FileMode.Open, FileAccess.Write, FileShare.None);
        Assert.True(writable.CanWrite);
    }

    [Fact]
    public async Task Concurrent_resolvers_publish_distinct_receipts_without_changing_payloads()
    {
        await using var fixture = await LocalModelFixture.CreateAsync();
        var manifest = await fixture.SeedCompleteBundleAsync();
        var before = await File.ReadAllBytesAsync(fixture.ArtifactPath(manifest.Files[0]));

        var results = await Task.WhenAll(
            fixture.Store.ResolveAsync(manifest, CancellationToken.None).AsTask(),
            fixture.Store.ResolveAsync(manifest, CancellationToken.None).AsTask());

        Assert.All(results, result => Assert.True(result.Succeeded, result.ReasonCode));
        Assert.NotEqual(results[0].ReceiptLocation, results[1].ReceiptLocation);
        Assert.Equal(before, await File.ReadAllBytesAsync(fixture.ArtifactPath(manifest.Files[0])));
        foreach (var result in results) result.Lease!.Dispose();
    }

    [Fact]
    public async Task Unsafe_manifest_path_refuses_before_any_store_access()
    {
        var unsafeManifest = new ModelBundleSpecification(
            1,
            [new ModelArtifactSpecification(new string('a', 40), @"..\weights.bin", new string('0', 64), 1)]);
        var store = new LocalModelStore(() => throw new InvalidOperationException("Store access is forbidden."));

        var result = await store.ResolveAsync(unsafeManifest, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal("model-path-unsafe", result.ReasonCode);
        Assert.False(result.ReceiptPersisted);
    }

    [Theory]
    [InlineData(@"..\weights.bin")]
    [InlineData(@"C:\weights.bin")]
    [InlineData(@"\\server\share\weights.bin")]
    [InlineData("weights.bin:alternate")]
    [InlineData("weights.bin.")]
    [InlineData("CON.bin")]
    public async Task Unsafe_manifest_filename_alias_refuses_before_any_store_access(string filename)
    {
        var unsafeManifest = new ModelBundleSpecification(
            1,
            [new ModelArtifactSpecification(new string('a', 40), filename, new string('0', 64), 1)]);
        var store = new LocalModelStore(() => throw new InvalidOperationException("Store access is forbidden."));

        var result = await store.ResolveAsync(unsafeManifest, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal("model-path-unsafe", result.ReasonCode);
        Assert.False(result.ReceiptPersisted);
    }

    [Fact]
    public async Task Unavailable_store_refuses_without_a_receipt_or_another_root()
    {
        await using var fixture = await LocalModelFixture.CreateAsync();
        var manifest = await fixture.SeedCompleteBundleAsync();
        var store = new LocalModelStore(() => throw new ModelVerificationFilesException("model-store-unavailable"));

        var result = await store.ResolveAsync(manifest, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal("model-store-unavailable", result.ReasonCode);
        Assert.False(result.ReceiptPersisted);
        Assert.Null(result.Lease);
    }

    [Fact]
    public async Task Missing_model_root_refuses_without_creating_another_root()
    {
        await using var fixture = await LocalModelFixture.CreateAsync();
        var manifest = await fixture.SeedCompleteBundleAsync();
        var missingRoot = Path.Combine(fixture.Root, "missing-model-root");
        var store = new LocalModelStore(() => WindowsModelVerificationFiles.OpenForTest(missingRoot));

        var result = await store.ResolveAsync(manifest, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal("model-store-unavailable", result.ReasonCode);
        Assert.False(result.ReceiptPersisted);
        Assert.False(Directory.Exists(missingRoot));
    }

    [Fact]
    public async Task Reparse_model_root_refuses_as_an_unsafe_path_without_touching_its_target()
    {
        await using var fixture = await LocalModelFixture.CreateAsync();
        var manifest = await fixture.SeedCompleteBundleAsync();
        var (store, outsideSentinel) = await fixture.CreateRootReparseStoreAsync();

        var result = await store.ResolveAsync(manifest, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal("model-path-unsafe", result.ReasonCode);
        Assert.False(result.ReceiptPersisted);
        Assert.Equal("outside", await File.ReadAllTextAsync(outsideSentinel));
    }

    [Fact]
    public async Task Receipt_write_failure_prevents_a_success_lease()
    {
        await using var fixture = await LocalModelFixture.CreateAsync();
        var manifest = await fixture.SeedCompleteBundleAsync();
        var store = fixture.CreateStore(new WindowsModelVerificationTestOptions(ReceiptFailureReason: "model-store-receipt-failed"));

        var result = await store.ResolveAsync(manifest, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal("model-store-receipt-failed", result.ReasonCode);
        Assert.False(result.ReceiptPersisted);
        Assert.Null(result.Lease);
    }

    [Fact]
    public async Task Unwritable_receipt_store_refuses_without_a_success_lease()
    {
        await using var fixture = await LocalModelFixture.CreateAsync();
        var manifest = await fixture.SeedCompleteBundleAsync();
        var store = fixture.CreateStore(new WindowsModelVerificationTestOptions(ReceiptFailureReason: "model-store-not-writable"));

        var result = await store.ResolveAsync(manifest, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal("model-store-not-writable", result.ReasonCode);
        Assert.False(result.ReceiptPersisted);
        Assert.Null(result.Lease);
    }

    [Fact]
    public async Task Actual_receipt_directory_write_failure_prevents_a_success_lease()
    {
        await using var fixture = await LocalModelFixture.CreateAsync();
        var manifest = await fixture.SeedCompleteBundleAsync();
        var blocker = await fixture.CreateReceiptDirectoryBlockerAsync();

        var result = await fixture.Store.ResolveAsync(manifest, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal("model-store-receipt-failed", result.ReasonCode);
        Assert.False(result.ReceiptPersisted);
        Assert.Null(result.Lease);
        Assert.Equal("not-a-directory", await File.ReadAllTextAsync(blocker));
    }

    [Fact]
    public async Task Receipt_ancestor_reparse_refuses_without_writing_its_target()
    {
        await using var fixture = await LocalModelFixture.CreateAsync();
        var manifest = await fixture.SeedCompleteBundleAsync();
        var outsideSentinel = await fixture.CreateReceiptAncestorReparsePointAsync();

        var result = await fixture.Store.ResolveAsync(manifest, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal("model-path-unsafe", result.ReasonCode);
        Assert.False(result.ReceiptPersisted);
        Assert.Null(result.Lease);
        Assert.Equal("outside", await File.ReadAllTextAsync(outsideSentinel));
    }

    [Fact]
    public async Task Reparse_payload_file_refuses_without_reading_its_target()
    {
        await using var fixture = await LocalModelFixture.CreateAsync();
        var manifest = await fixture.SeedCompleteBundleAsync();
        var outsideSentinel = await fixture.ReplaceArtifactWithReparsePointAsync(manifest.Files[0]);

        var result = await fixture.Store.ResolveAsync(manifest, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal("model-path-unsafe", result.ReasonCode);
        Assert.Null(result.Lease);
        Assert.Equal("outside", await File.ReadAllTextAsync(outsideSentinel));
    }

    [Fact]
    public async Task Receipt_collision_never_overwrites_an_existing_receipt()
    {
        await using var fixture = await LocalModelFixture.CreateAsync();
        var manifest = await fixture.SeedCompleteBundleAsync();
        var receiptId = Guid.Parse("11111111-1111-1111-1111-111111111111");

        var first = await fixture.CreateStore(nextReceiptId: () => receiptId).ResolveAsync(manifest, CancellationToken.None);
        Assert.True(first.Succeeded, first.ReasonCode);
        first.Lease!.Dispose();
        var before = await fixture.ReadReceiptAsync(first.ReceiptLocation);

        var second = await fixture.CreateStore(nextReceiptId: () => receiptId).ResolveAsync(manifest, CancellationToken.None);

        Assert.False(second.Succeeded);
        Assert.Equal("model-store-receipt-failed", second.ReasonCode);
        Assert.False(second.ReceiptPersisted);
        Assert.Null(second.Lease);
        Assert.Equal(before, await fixture.ReadReceiptAsync(first.ReceiptLocation));
    }

    [Fact]
    public async Task Earlier_receipt_does_not_bypass_a_fresh_hash_after_payload_corruption()
    {
        await using var fixture = await LocalModelFixture.CreateAsync();
        var manifest = await fixture.SeedCompleteBundleAsync();
        var first = await fixture.Store.ResolveAsync(manifest, CancellationToken.None);
        Assert.True(first.Succeeded, first.ReasonCode);
        first.Lease!.Dispose();
        await File.WriteAllTextAsync(fixture.ArtifactPath(manifest.Files[0]), "xxxxxxxxxx");

        var second = await fixture.Store.ResolveAsync(manifest, CancellationToken.None);

        Assert.False(second.Succeeded);
        Assert.Equal("model-artifact-sha256-mismatch", second.ReasonCode);
        Assert.Null(second.Lease);
    }

    [Fact]
    public async Task Resolver_uses_only_local_open_read_and_receipt_operations()
    {
        await using var fixture = await LocalModelFixture.CreateAsync();
        var manifest = await fixture.SeedCompleteBundleAsync();
        var operations = new List<WindowsModelVerificationOperation>();
        var store = fixture.CreateStore(new WindowsModelVerificationTestOptions(Observe: operations.Add));

        var result = await store.ResolveAsync(manifest, CancellationToken.None);

        Assert.True(result.Succeeded, result.ReasonCode);
        Assert.Equal(
            [
                WindowsModelVerificationOperation.OpenRoot,
                WindowsModelVerificationOperation.OpenArtifact,
                WindowsModelVerificationOperation.OpenArtifact,
                WindowsModelVerificationOperation.PersistReceipt
            ],
            operations);
        Assert.Equal(2, result.Lease!.Files.Count);
        result.Lease.Dispose();
    }

    [Fact]
    public async Task Cancellation_while_verifying_releases_the_current_payload_handle()
    {
        await using var fixture = await LocalModelFixture.CreateAsync();
        var manifest = await fixture.SeedCompleteBundleAsync();
        using var cancellation = new CancellationTokenSource();
        var store = new LocalModelStore(
            () => new CancellingVerificationFiles(
                WindowsModelVerificationFiles.OpenForTest(fixture.ModelRoot),
                cancellation));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await store.ResolveAsync(manifest, cancellation.Token));

        await using var writable = File.Open(
            fixture.ArtifactPath(manifest.Files[0]),
            FileMode.Open,
            FileAccess.Write,
            FileShare.None);
        Assert.True(writable.CanWrite);
    }

    [Fact]
    public async Task Offline_payload_refuses_before_any_read_can_hydrate_it()
    {
        await using var fixture = await LocalModelFixture.CreateAsync();
        var manifest = await fixture.SeedCompleteBundleAsync();
        var payload = fixture.ArtifactPath(manifest.Files[0]);
        File.SetAttributes(payload, File.GetAttributes(payload) | FileAttributes.Offline);
        Assert.True((File.GetAttributes(payload) & FileAttributes.Offline) != 0);

        var result = await fixture.Store.ResolveAsync(manifest, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal("model-path-unsafe", result.ReasonCode);
        Assert.Null(result.Lease);
    }

    [Fact]
    public async Task Receipt_identifies_the_immutable_verifier_contract()
    {
        await using var fixture = await LocalModelFixture.CreateAsync();
        var manifest = await fixture.SeedCompleteBundleAsync();

        var result = await fixture.Store.ResolveAsync(manifest, CancellationToken.None);

        Assert.True(result.Succeeded, result.ReasonCode);
        using var receipt = JsonDocument.Parse(await fixture.ReadReceiptAsync(result.ReceiptLocation));
        Assert.Equal("flux-offline-model-gate/1", receipt.RootElement.GetProperty("verifierVersion").GetString());
        result.Lease!.Dispose();
    }

    [Fact]
    public async Task Read_failure_is_a_receipted_refusal_and_releases_the_current_payload_handle()
    {
        await using var fixture = await LocalModelFixture.CreateAsync();
        var manifest = await fixture.SeedCompleteBundleAsync();
        var store = new LocalModelStore(
            () => new FailingReadVerificationFiles(WindowsModelVerificationFiles.OpenForTest(fixture.ModelRoot)));

        var result = await store.ResolveAsync(manifest, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal("model-artifact-read-failed", result.ReasonCode);
        Assert.True(result.ReceiptPersisted);
        await using var writable = File.Open(
            fixture.ArtifactPath(manifest.Files[0]),
            FileMode.Open,
            FileAccess.Write,
            FileShare.None);
        Assert.True(writable.CanWrite);
    }

    [Fact]
    public async Task Receipt_publication_keeps_the_owned_staging_handle_until_the_no_overwrite_rename()
    {
        await using var fixture = await LocalModelFixture.CreateAsync();
        var manifest = await fixture.SeedCompleteBundleAsync();
        var replacementDenied = false;
        var store = fixture.CreateStore(new WindowsModelVerificationTestOptions(
            BeforeReceiptPublish: stagingName =>
            {
                var stagingPath = Path.Combine(fixture.ModelRoot, "inventory", "verifications", stagingName);
                try
                {
                    File.WriteAllText(stagingPath, "attacker-controlled-receipt");
                }
                catch (IOException)
                {
                    replacementDenied = true;
                }

                return ValueTask.CompletedTask;
            }));

        var result = await store.ResolveAsync(manifest, CancellationToken.None);

        Assert.True(result.Succeeded, result.ReasonCode);
        Assert.True(replacementDenied);
        Assert.DoesNotContain("attacker-controlled-receipt", await fixture.ReadReceiptAsync(result.ReceiptLocation));
        result.Lease!.Dispose();
    }

    [Fact]
    public async Task Failed_receipt_publication_deletes_only_the_original_owned_staging_file()
    {
        await using var fixture = await LocalModelFixture.CreateAsync();
        var manifest = await fixture.SeedCompleteBundleAsync();
        string? stagingPath = null;
        var store = fixture.CreateStore(new WindowsModelVerificationTestOptions(
            BeforeReceiptPublish: stagingName =>
            {
                stagingPath = Path.Combine(fixture.ModelRoot, "inventory", "verifications", stagingName);
                return ValueTask.FromException(new IOException("synthetic-publication-interruption"));
            }));

        var result = await store.ResolveAsync(manifest, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal("model-store-receipt-failed", result.ReasonCode);
        Assert.False(result.ReceiptPersisted);
        Assert.Null(result.Lease);
        Assert.NotNull(stagingPath);
        Assert.False(File.Exists(stagingPath));
    }

    [Theory]
    [InlineData(3u, true)]
    [InlineData(4u, false)]
    [InlineData(6u, false)]
    public void Only_fixed_local_drive_types_are_accepted_for_model_paths(uint driveType, bool expected)
    {
        Assert.Equal(expected, HandleRelativeNativeFileSystem.IsLocalFixedModelDriveTypeForTest(driveType));
    }

    private sealed class CancellingVerificationFiles(
        IModelVerificationFiles inner,
        CancellationTokenSource cancellation) : IModelVerificationFiles
    {
        public async ValueTask<ModelVerificationFileOpenResult> OpenReadAsync(
            ModelArtifactSpecification specification,
            CancellationToken cancellationToken)
        {
            var opened = await inner.OpenReadAsync(specification, cancellationToken);
            return opened.File is null
                ? opened
                : ModelVerificationFileOpenResult.Found(new CancellingVerificationFile(opened.File, cancellation));
        }

        public ValueTask<ModelReceiptPersistenceResult> PersistReceiptAsync(
            ModelVerificationReceipt receipt,
            CancellationToken cancellationToken) => inner.PersistReceiptAsync(receipt, cancellationToken);

        public void Dispose() => inner.Dispose();
    }

    private sealed class CancellingVerificationFile(
        IModelVerificationFile inner,
        CancellationTokenSource cancellation) : IModelVerificationFile
    {
        public long ByteLength => inner.ByteLength;

        public ValueTask<int> ReadAsync(long offset, Memory<byte> buffer, CancellationToken cancellationToken)
        {
            cancellation.Cancel();
            return ValueTask.FromCanceled<int>(cancellationToken);
        }

        public void Dispose() => inner.Dispose();
    }

    private sealed class FailingReadVerificationFiles(IModelVerificationFiles inner) : IModelVerificationFiles
    {
        public async ValueTask<ModelVerificationFileOpenResult> OpenReadAsync(
            ModelArtifactSpecification specification,
            CancellationToken cancellationToken)
        {
            var opened = await inner.OpenReadAsync(specification, cancellationToken);
            return opened.File is null
                ? opened
                : ModelVerificationFileOpenResult.Found(new FailingReadVerificationFile(opened.File));
        }

        public ValueTask<ModelReceiptPersistenceResult> PersistReceiptAsync(
            ModelVerificationReceipt receipt,
            CancellationToken cancellationToken) => inner.PersistReceiptAsync(receipt, cancellationToken);

        public void Dispose() => inner.Dispose();
    }

    private sealed class FailingReadVerificationFile(IModelVerificationFile inner) : IModelVerificationFile
    {
        public long ByteLength => inner.ByteLength;

        public ValueTask<int> ReadAsync(long offset, Memory<byte> buffer, CancellationToken cancellationToken) =>
            ValueTask.FromException<int>(new IOException("synthetic-read-failure"));

        public void Dispose() => inner.Dispose();
    }
}
