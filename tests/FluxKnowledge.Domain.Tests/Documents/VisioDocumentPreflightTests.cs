using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using FluxKnowledge.Application.Documents;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Sources;
using FluxKnowledge.Domain.Sources;
using Xunit;

namespace FluxKnowledge.Domain.Tests.Documents;

public sealed class VisioDocumentPreflightTests
{
    [Fact]
    public async Task ValidateAsync_rejects_an_external_relationship_before_Visio_can_open_the_package()
    {
        var retained = Retained(CreatePackage((archive) =>
        {
            WriteEntry(archive, "_rels/.rels", """
                <?xml version="1.0" encoding="utf-8"?>
                <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
                  <Relationship Id="rId1" Type="http://schemas.microsoft.com/visio/2010/relationships/document" Target="http://example.invalid/document.vsdx" TargetMode="External" />
                </Relationships>
                """);
        }));

        var error = await Assert.ThrowsAsync<RetainedProcessorException>(() =>
            VisioDocumentPreflight.ValidateAsync(retained, CancellationToken.None).AsTask());

        Assert.Equal("visio-document-external-relationship", error.OutcomeCode);
    }

    [Fact]
    public async Task ValidateAsync_accepts_a_safe_minimal_VSDX_container_without_opening_a_document()
    {
        var retained = Retained(CreatePackage((archive) =>
        {
            WriteEntry(archive, "[Content_Types].xml", "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\" />");
            WriteEntry(archive, "visio/document.xml", "<VisioDocument xmlns=\"http://schemas.microsoft.com/office/visio/2012/main\" />");
        }));

        await VisioDocumentPreflight.ValidateAsync(retained, CancellationToken.None);
    }

    [Fact]
    public async Task ValidateAsync_rejects_the_Visio_connections_relationship_used_for_refreshable_data()
    {
        var retained = Retained(CreatePackage((archive) =>
        {
            WriteEntry(archive, "visio/_rels/document.xml.rels", """
                <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
                  <Relationship Id="rId1" Type="http://schemas.microsoft.com/visio/2010/relationships/connections" Target="connections.xml" />
                </Relationships>
                """);
        }));

        var error = await Assert.ThrowsAsync<RetainedProcessorException>(() =>
            VisioDocumentPreflight.ValidateAsync(retained, CancellationToken.None).AsTask());

        Assert.Equal("visio-document-active-relationship", error.OutcomeCode);
    }

    [Fact]
    public async Task Malformed_relationship_XML_returns_a_safe_terminal_reason_not_raw_parser_details()
    {
        var retained = Retained(CreatePackage(archive => WriteEntry(archive, "_rels/.rels", "<Relationships><")));
        var error = await Assert.ThrowsAsync<RetainedProcessorException>(() =>
            VisioDocumentPreflight.ValidateAsync(retained, default).AsTask());
        Assert.Equal("visio-document-container-invalid", error.OutcomeCode);
    }

    private static RetainedSourceBytes Retained(byte[] bytes) => new(
        SourceRevisionId.New(),
        bytes,
        Convert.ToHexStringLower(SHA256.HashData(bytes)),
        bytes.Length);

    private static byte[] CreatePackage(Action<ZipArchive> write)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            write(archive);
        }
        return stream.ToArray();
    }

    private static void WriteEntry(ZipArchive archive, string name, string value)
    {
        using var writer = new StreamWriter(archive.CreateEntry(name).Open(), new UTF8Encoding(false));
        writer.Write(value);
    }
}
