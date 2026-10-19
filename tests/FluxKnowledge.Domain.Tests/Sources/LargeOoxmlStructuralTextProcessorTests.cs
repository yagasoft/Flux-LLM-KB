using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using FluxKnowledge.Application.Documents;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Sources;
using FluxKnowledge.Domain.Sources;
using Xunit;

namespace FluxKnowledge.Domain.Tests.Sources;

public sealed class LargeOoxmlStructuralTextProcessorTests
{
    [Theory]
    [InlineData(65)]
    [InlineData(128)]
    public async Task Large_word_xml_within_the_new_part_budget_publishes_only_its_structural_text(int partMiB)
    {
        var bytes = CreateLargePackage(partMiB * 1024 * 1024, visio: false);
        using (var package = new ZipArchive(new MemoryStream(bytes, writable: false), ZipArchiveMode.Read))
        {
            var main = package.GetEntry("word/document.xml")!;
            Assert.Equal(partMiB * 1024L * 1024, main.Length);
            Assert.InRange((double)main.Length / main.CompressedLength, 1, 169);
            Assert.True(bytes.Length < 128L * 1024 * 1024);
        }
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var claim = Claim(hash);
        var writer = new RecordingWriter();

        var result = await new OoxmlStructuralTextProcessor(writer).ProcessAsync(
            claim, new RetainedSourceBytes(claim.SourceRevisionId, bytes, hash, bytes.Length),
            new RetainedProcessorOptions(), CancellationToken.None);

        var child = Assert.Single(result.Members);
        Assert.Equal("bounded large document\n", writer.Text);
        var text = Assert.IsType<string>(writer.Text);
        Assert.Equal(Encoding.UTF8.GetByteCount(text), child.ByteLength);
        Assert.Equal(200L * 1024 * 1024, writer.MaximumByteLength);
    }

    [Fact]
    public async Task Xml_part_above_128_mib_is_refused_from_metadata_before_reading_or_publishing_content()
    {
        var bytes = CreateClaimedOversizedPackage(128 * 1024 * 1024 + 1);
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var claim = Claim(hash);
        var writer = new RecordingWriter();

        var error = await Assert.ThrowsAsync<RetainedProcessorException>(() =>
            new OoxmlStructuralTextProcessor(writer).ProcessAsync(claim,
                new RetainedSourceBytes(claim.SourceRevisionId, bytes, hash, bytes.Length),
                new RetainedProcessorOptions(), CancellationToken.None).AsTask());

        Assert.Equal("office-document-expanded-xml-limit", error.OutcomeCode);
        Assert.Null(writer.Text);
    }

    [Fact]
    public async Task Vsdx_keeps_its_existing_64_mib_part_budget()
    {
        var bytes = CreateLargePackage(65 * 1024 * 1024, visio: true);
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));

        var error = await Assert.ThrowsAsync<RetainedProcessorException>(() =>
            new VsdxDocumentExtractor().ExtractAsync(
                new RetainedSourceBytes(SourceRevisionId.New(), bytes, hash, bytes.Length),
                CancellationToken.None).AsTask());

        Assert.Equal("office-document-part-unsupported", error.OutcomeCode);
    }

    [Theory]
    [InlineData("phase-5-ooxml-structural-v1", "phase-5-ooxml-retained-structural-v1", true)]
    [InlineData("phase-5-ooxml-structural-v2", "phase-5-ooxml-retained-structural-v2", true)]
    [InlineData("phase-5-ooxml-structural-v3", "phase-5-ooxml-retained-structural-v3", true)]
    [InlineData("phase-5-ooxml-structural-v1", "phase-5-ooxml-retained-structural-v2", false)]
    [InlineData("phase-5-ooxml-structural-v3", "phase-5-ooxml-retained-structural-v2", false)]
    public void Historical_readback_accepts_only_exact_known_descriptor_pairs(string version, string fingerprint, bool expected)
    {
        Assert.Equal(expected, OoxmlStructuralTextProcessor.IsKnownProcessor(version, fingerprint));
    }

    private static RetainedProcessorClaim Claim(string hash) =>
        new(Guid.NewGuid(), SourceRevisionId.New(), "large-office-fixture", hash, "test-owner", 1,
            DateTimeOffset.UtcNow.AddMinutes(5));

    private static byte[] CreateLargePackage(int xmlBytes, bool visio)
    {
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            var main = visio ? "visio/document.xml" : "word/document.xml";
            var contentType = visio ? "application/vnd.ms-visio.drawing.main+xml" :
                "application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml";
            Write(archive, "[Content_Types].xml",
                $"<Types xmlns='http://schemas.openxmlformats.org/package/2006/content-types'><Override PartName='/{main}' ContentType='{contentType}'/></Types>");
            Write(archive, "_rels/.rels",
                $"<Relationships xmlns='http://schemas.openxmlformats.org/package/2006/relationships'><Relationship Id='r1' Type='http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument' Target='{main}'/></Relationships>");
            var prefix = visio
                ? "<VisioDocument xmlns='http://schemas.microsoft.com/visio/2010/core'><!--"
                : "<w:document xmlns:w='http://schemas.openxmlformats.org/wordprocessingml/2006/main'><!--";
            var suffix = visio ? "--></VisioDocument>" :
                "--><w:body><w:p><w:r><w:t>bounded large document</w:t></w:r></w:p></w:body></w:document>";
            using var entry = archive.CreateEntry(main, CompressionLevel.Fastest).Open();
            entry.Write(Encoding.UTF8.GetBytes(prefix));
            // A deterministic 1MiB period exceeds the DEFLATE dictionary, keeping the fixture
            // below the compressed-input limit without tripping the expansion-ratio guard.
            var padding = new byte[1024 * 1024];
            new Random(429).NextBytes(padding);
            const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
            for (var index = 0; index < padding.Length; index++) padding[index] = (byte)alphabet[padding[index] & 63];
            var remaining = xmlBytes - prefix.Length - suffix.Length;
            while (remaining > 0)
            {
                var count = Math.Min(remaining, padding.Length);
                entry.Write(padding.AsSpan(0, count));
                remaining -= count;
            }
            entry.Write(Encoding.UTF8.GetBytes(suffix));
        }
        return buffer.ToArray();
    }

    private static byte[] CreateClaimedOversizedPackage(uint claimedSize)
    {
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
            Write(archive, "word/document.xml", "<x/>");
        var bytes = buffer.ToArray();
        for (var offset = 0; offset <= bytes.Length - 4; offset++)
        {
            var signature = BitConverter.ToUInt32(bytes, offset);
            if (signature == 0x04034b50)
            {
                BitConverter.TryWriteBytes(bytes.AsSpan(offset + 18, 4), claimedSize);
                BitConverter.TryWriteBytes(bytes.AsSpan(offset + 22, 4), claimedSize);
            }
            if (signature == 0x02014b50)
            {
                BitConverter.TryWriteBytes(bytes.AsSpan(offset + 20, 4), claimedSize);
                BitConverter.TryWriteBytes(bytes.AsSpan(offset + 24, 4), claimedSize);
            }
        }
        return bytes;
    }

    private static void Write(ZipArchive archive, string name, string xml)
    {
        using var writer = new StreamWriter(archive.CreateEntry(name).Open(), new UTF8Encoding(false));
        writer.Write(xml);
    }

    private sealed class RecordingWriter : IRetainedArtifactWriter
    {
        public string? Text { get; private set; }
        public long MaximumByteLength { get; private set; }
        public async ValueTask<RetainedArtifactWriteReceipt> WriteAsync(SourceRevisionId sourceRevisionId,
            Stream stream, long maximumByteLength, CancellationToken cancellationToken)
        {
            MaximumByteLength = maximumByteLength;
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
            Text = await reader.ReadToEndAsync(cancellationToken);
            var bytes = Encoding.UTF8.GetBytes(Text);
            var sha = Convert.ToHexStringLower(SHA256.HashData(bytes));
            return new RetainedArtifactWriteReceipt(sha, "lab/" + sha, bytes.Length, true, false);
        }
    }
}
