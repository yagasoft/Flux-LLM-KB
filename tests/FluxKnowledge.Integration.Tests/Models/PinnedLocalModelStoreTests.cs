using FluxKnowledge.Application.Models;
using FluxKnowledge.Infrastructure.Inference.Models;
using FluxKnowledge.Integrations.Models;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Models;

public sealed class PinnedLocalModelStoreTests
{
    [Fact]
    public async Task Reused_verification_keeps_exact_files_and_ancestors_protected_until_the_last_borrower_exits()
    {
        await using var fixture = await LocalModelFixture.CreateAsync();
        var manifest = await fixture.SeedCompleteBundleAsync();
        var bundle = Path.Combine(fixture.ModelRoot, "bundles", "synthetic");
        Directory.CreateDirectory(bundle);
        foreach (var artifact in manifest.Files)
            File.Copy(fixture.ArtifactPath(artifact), Path.Combine(bundle, artifact.Filename));

        using var store = new PinnedLocalModelStore(new LocalModelStore(() =>
            WindowsModelVerificationFiles.OpenBundleForTest(fixture.ModelRoot, ["bundles", "synthetic"])));
        var first = await store.ResolveAsync(manifest, CancellationToken.None);
        Assert.True(first.Succeeded, first.ReasonCode);
        var firstLease = Assert.IsType<VerifiedLocalModelLease>(first.Lease);
        var second = await store.ResolveAsync(manifest, CancellationToken.None);
        Assert.True(second.Succeeded, second.ReasonCode);
        var secondLease = Assert.IsType<VerifiedLocalModelLease>(second.Lease);
        Assert.Equal(first.ReceiptLocation, second.ReceiptLocation);
        Assert.Single(Directory.GetFiles(Path.Combine(fixture.ModelRoot, "inventory", "verifications")));

        firstLease.Dispose();
        Assert.Throws<IOException>(() => File.Open(Path.Combine(bundle, "weights.bin"), FileMode.Open, FileAccess.Write, FileShare.Read));
        Assert.Throws<IOException>(() => Directory.Move(bundle, bundle + "-moved"));
        store.Dispose();
        Assert.Equal("flux-model", await File.ReadAllTextAsync(secondLease.GetVerifiedLocalPath("weights.bin")));
        Assert.Throws<IOException>(() => Directory.Move(bundle, bundle + "-moved"));
        secondLease.Dispose();
        Directory.Move(bundle, bundle + "-moved");
    }

    [Fact]
    public async Task Concurrent_resolutions_verify_once_and_each_get_an_independent_lease()
    {
        await using var fixture = await LocalModelFixture.CreateAsync();
        var manifest = await fixture.SeedCompleteBundleAsync();
        var inner = new CountingStore(fixture.CreateStore());
        using var store = new PinnedLocalModelStore(inner);

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
            store.ResolveAsync(manifest, CancellationToken.None).AsTask()));
        Assert.Equal(1, inner.Resolutions);
        Assert.All(results, result => Assert.True(result.Succeeded, result.ReasonCode));
        foreach (var result in results)
            Assert.Equal("flux-model", await File.ReadAllTextAsync(result.Lease!.GetVerifiedLocalPath("weights.bin")));
        foreach (var result in results) result.Lease!.Dispose();
    }

    [Fact]
    public async Task Different_bundle_fingerprints_are_verified_independently()
    {
        await using var fixture = await LocalModelFixture.CreateAsync();
        var complete = await fixture.SeedCompleteBundleAsync();
        var weightsOnly = new ModelBundleSpecification(1, [complete.Files.Single(file => file.Filename == "weights.bin")]);
        var inner = new CountingStore(fixture.CreateStore());
        using var store = new PinnedLocalModelStore(inner);

        var weights = await store.ResolveAsync(weightsOnly, CancellationToken.None);
        var both = await store.ResolveAsync(complete, CancellationToken.None);
        var repeatedWeights = await store.ResolveAsync(weightsOnly, CancellationToken.None);
        Assert.True(weights.Succeeded, weights.ReasonCode);
        Assert.True(both.Succeeded, both.ReasonCode);
        Assert.True(repeatedWeights.Succeeded, repeatedWeights.ReasonCode);
        Assert.Equal(2, inner.Resolutions);
        Assert.NotEqual(weights.BundleFingerprint, both.BundleFingerprint);
        Assert.Equal(weights.ReceiptLocation, repeatedWeights.ReceiptLocation);
        Assert.Throws<KeyNotFoundException>(() => weights.Lease!.GetVerifiedLocalPath("processor.json"));
        Assert.Equal("{}", await File.ReadAllTextAsync(both.Lease!.GetVerifiedLocalPath("processor.json")));
        weights.Lease!.Dispose();
        both.Lease!.Dispose();
        repeatedWeights.Lease!.Dispose();
    }

    [Fact]
    public async Task Missing_store_fails_closed_and_is_never_cached_as_a_success()
    {
        await using var fixture = await LocalModelFixture.CreateAsync();
        var manifest = await fixture.SeedCompleteBundleAsync();
        var absent = Path.Combine(fixture.Root, "absent");
        var inner = new CountingStore(new LocalModelStore(() => WindowsModelVerificationFiles.OpenForTest(absent)));
        using var store = new PinnedLocalModelStore(inner);

        var first = await store.ResolveAsync(manifest, CancellationToken.None);
        var second = await store.ResolveAsync(manifest, CancellationToken.None);
        Assert.False(first.Succeeded);
        Assert.False(second.Succeeded);
        Assert.Null(first.Lease);
        Assert.Null(second.Lease);
        Assert.Equal(2, inner.Resolutions);
    }

    private sealed class CountingStore(ILocalModelStore inner) : ILocalModelStore
    {
        private int _resolutions;
        public int Resolutions => Volatile.Read(ref _resolutions);

        public ValueTask<ModelResolutionResult> ResolveAsync(ModelBundleSpecification specification, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _resolutions);
            return inner.ResolveAsync(specification, cancellationToken);
        }
    }
}
