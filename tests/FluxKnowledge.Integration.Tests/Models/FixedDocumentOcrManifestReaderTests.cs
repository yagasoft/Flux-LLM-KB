using System.Text;
using FluxKnowledge.Application.Documents;
using FluxKnowledge.Integrations.Models;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Models;

public sealed class FixedDocumentOcrManifestReaderTests
{
    [Fact]
    public async Task Reads_each_role_from_its_fixed_manifest_beneath_the_production_model_root()
    {
        var requestedPaths = new List<string>();
        var reader = new FixedDocumentOcrManifestReader((path, maximumBytes, cancellationToken) =>
        {
            Assert.Equal(ModelManifestMaximumBytes, maximumBytes);
            cancellationToken.ThrowIfCancellationRequested();
            requestedPaths.Add(path);
            return Task.FromResult(Encoding.UTF8.GetBytes(Manifest));
        });

        foreach (var role in Enum.GetValues<DocumentOcrModelRole>())
        {
            var manifest = await reader.ReadAsync(role, CancellationToken.None);
            Assert.Single(manifest.Files);
        }

        Assert.Equal(
            [
                @"J:\Models\manifests\document-ocr-20260919\text-detection.json",
                @"J:\Models\manifests\document-ocr-20260919\english-recognition.json",
                @"J:\Models\manifests\document-ocr-20260919\arabic-recognition.json",
                @"J:\Models\manifests\document-ocr-20260919\page-orientation.json",
                @"J:\Models\manifests\document-ocr-20260919\line-orientation.json",
                @"J:\Models\manifests\document-ocr-20260919\layout.json",
                @"J:\Models\manifests\document-ocr-20260919\tables.json"
            ],
            requestedPaths);
    }

    private const int ModelManifestMaximumBytes = 1024 * 1024;
    private const string Manifest = """
        {"schemaVersion":1,"files":[{"revision":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","filename":"inference.onnx","sha256":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","byteLength":0}]}
        """;
}
