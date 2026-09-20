using FluxKnowledge.Application.Documents;
using FluxKnowledge.Infrastructure.Inference.Documents;
using FluxKnowledge.Infrastructure.Inference.Models;
using FluxKnowledge.Integrations.Models;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Models;

public sealed class DocumentOcrLocalModelCompatibilityTests
{
    [Fact]
    [Trait("Category", "local-model-compatibility")]
    public async Task Verified_local_ocr_models_create_directml_sessions_when_explicitly_requested()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("FLUX_KB_RUN_LOCAL_MODEL_COMPATIBILITY"),
                "1",
                StringComparison.Ordinal))
        {
            return;
        }

        var manifests = FixedDocumentOcrManifestReader.OpenProduction();
        var resolver = new DocumentOcrModelBundleResolver(
            new LocalModelStore(WindowsModelVerificationFiles.OpenProduction),
            manifests.ReadAsync);
        var runtime = new DocumentOcrRuntime(resolver, new DirectMlDocumentOcrSessionFactory(deviceId: 0));

        var result = await runtime.OpenAsync(CancellationToken.None);

        Assert.True(result.Succeeded, result.ReasonCode);
        using var lease = Assert.IsType<DocumentOcrRuntimeLease>(result.Lease);
        foreach (var role in Enum.GetValues<DocumentOcrModelRole>())
        {
            var session = lease.GetSession<DirectMlDocumentOcrSession>(role);
            Assert.NotEmpty(session.InputNames);
            Assert.NotEmpty(session.OutputNames);
        }
    }
}
