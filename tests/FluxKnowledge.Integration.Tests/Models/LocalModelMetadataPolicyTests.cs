using FluxKnowledge.Application.Models;
using FluxKnowledge.Infrastructure.Inference.Models;
using FluxKnowledge.Integrations.Models;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Models;

public sealed class LocalModelMetadataPolicyTests
{
    [Fact]
    public async Task Fresh_model_resolvers_open_cached_files_without_reading_or_hashing_their_payloads()
    {
        await using var fixture = await LocalModelFixture.CreateAsync();
        var manifest = await fixture.SeedCompleteBundleAsync();
        for (var restart = 0; restart < 2; restart++)
        {
            var store = new LocalModelStore(() => new ReadForbiddenFiles(
                WindowsModelVerificationFiles.OpenForTest(fixture.ModelRoot)));
            var result = await store.ResolveAsync(manifest, default);
            using var lease = result.Lease;
            Assert.True(result.Succeeded, result.ReasonCode);
            Assert.NotNull(lease);
            Assert.All(result.Files, file => Assert.Null(file.ObservedSha256));
        }
    }

    [Fact]
    public async Task Model_metadata_policy_accepts_same_length_local_changes_without_claiming_checksum_verification()
    {
        await using var fixture = await LocalModelFixture.CreateAsync();
        var manifest = await fixture.SeedCompleteBundleAsync();
        await File.WriteAllTextAsync(fixture.ArtifactPath(manifest.Files[0]), "flux-modeX");
        var result = await fixture.Store.ResolveAsync(manifest, default);
        using var lease = result.Lease;
        Assert.True(result.Succeeded, result.ReasonCode);
        var receipt = System.Text.Json.JsonDocument.Parse(await fixture.ReadReceiptAsync(result.ReceiptLocation));
        Assert.False(receipt.RootElement.GetProperty("verified").GetBoolean());
        Assert.Equal(0, receipt.RootElement.GetProperty("verifiedBytes").GetInt64());
    }

    private sealed class ReadForbiddenFiles(IModelVerificationFiles inner) : IModelVerificationFiles
    {
        public async ValueTask<ModelVerificationFileOpenResult> OpenReadAsync(ModelArtifactSpecification specification, CancellationToken ct)
        {
            var opened = await inner.OpenReadAsync(specification, ct);
            return opened.File is null ? opened : ModelVerificationFileOpenResult.Found(new ReadForbiddenFile(opened.File));
        }

        public ValueTask<ModelReceiptPersistenceResult> PersistReceiptAsync(ModelVerificationReceipt receipt, CancellationToken ct) =>
            inner.PersistReceiptAsync(receipt, ct);
        public void Dispose() => inner.Dispose();
    }

    private sealed class ReadForbiddenFile(IModelVerificationFile inner) : IModelVerificationFile
    {
        public long ByteLength => inner.ByteLength;
        public string? ProtectedLocalPath => inner.ProtectedLocalPath;
        public ValueTask<int> ReadAsync(long offset, Memory<byte> buffer, CancellationToken ct) =>
            throw new IOException("Ordinary model resolution must not read the model body.");
        public void Dispose() => inner.Dispose();
    }
}
