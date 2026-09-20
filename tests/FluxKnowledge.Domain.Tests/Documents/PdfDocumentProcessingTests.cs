using System.Security.Cryptography;
using System.Text;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Sources;
using FluxKnowledge.Application.Documents;
using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Infrastructure.Inference.Documents;
using FluxKnowledge.Infrastructure.Inference.Models;
using FluxKnowledge.Integrations.Documents;
using FluxKnowledge.Integrations.Models;
using SkiaSharp;
using Syncfusion.Telemetry;
using Xunit;

namespace FluxKnowledge.Domain.Tests.Documents;

public sealed class PdfDocumentProcessingTests
{
    [Fact]
    public void Licence_registration_refuses_an_unexpected_provider_failure_without_exposing_a_provider_exception()
    {
        using var fixture = new LicenceFixture();
        var registration = new SyncfusionLicenceRegistration(
            () => fixture.Path,
            static _ => throw new NotSupportedException("provider failure"));

        var failure = Assert.Throws<RetainedProcessorException>(registration.EnsureRegistered);

        Assert.Equal("pdf-license-unavailable", failure.OutcomeCode);
    }

    [Fact]
    public void Licence_registration_refuses_a_missing_explicit_file_without_calling_the_provider()
    {
        var registrations = 0;
        var registration = new SyncfusionLicenceRegistration(
            () => Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.lic"),
            _ => registrations++);

        var failure = Assert.Throws<RetainedProcessorException>(registration.EnsureRegistered);

        Assert.Equal("pdf-license-unavailable", failure.OutcomeCode);
        Assert.Equal(0, registrations);
    }

    [Fact]
    public void Licence_registration_disables_vendor_telemetry_before_calling_the_provider()
    {
        using var fixture = new LicenceFixture();
        Telemetry.IsTelemetryEnabled = true;
        var telemetryEnabledDuringRegistration = true;
        var registration = new SyncfusionLicenceRegistration(
            () => fixture.Path,
            _ => telemetryEnabledDuringRegistration = Telemetry.IsTelemetryEnabled);

        registration.EnsureRegistered();

        Assert.False(telemetryEnabledDuringRegistration);
    }

    [Fact]
    public async Task Native_pdf_text_is_extracted_without_any_model_provider()
    {
        using var fixture = new LicenceFixture();
        var registrations = 0;
        var extractor = new SyncfusionPdfDocumentExtractor(
            new SyncfusionLicenceRegistration(() => fixture.Path, _ => registrations++));
        var bytes = CreatePdf("Native PDF paragraph");

        var result = await extractor.ExtractAsync(CreateRetained(bytes), CancellationToken.None);

        Assert.True(result.IsComplete);
        Assert.Contains("Native PDF paragraph", result.Text, StringComparison.Ordinal);
        Assert.Empty(result.Warnings);
        Assert.Equal(1, registrations);
    }

    [Fact]
    public async Task Blank_pdf_is_not_mislabelled_as_an_ocr_requirement()
    {
        using var fixture = new LicenceFixture();
        var extractor = new SyncfusionPdfDocumentExtractor(
            new SyncfusionLicenceRegistration(() => fixture.Path, static _ => { }),
            static _ => false);

        var result = await extractor.ExtractAsync(CreateRetained(CreatePdf(null)), CancellationToken.None);

        Assert.False(result.IsComplete, $"Blank-PDF extraction unexpectedly returned: {result.Text}");
        Assert.Empty(result.Text);
        Assert.Equal(["pdf-no-extractable-text"], result.Warnings);
    }

    [Fact]
    public async Task Whitespace_only_native_text_does_not_hide_a_visible_page_from_ocr()
    {
        using var fixture = new LicenceFixture();
        var extractor = new SyncfusionPdfDocumentExtractor(
            new SyncfusionLicenceRegistration(() => fixture.Path, static _ => { }));
        var bytes = CreatePdfWithContents("BT\n/F1 12 Tf\n72 720 Td\n(  ) Tj\nET\n0 0 0 rg\n72 680 100 20 re f\n");

        var result = await extractor.ExtractAsync(CreateRetained(bytes), CancellationToken.None);

        Assert.False(result.IsComplete);
        Assert.Equal(["pdf-ocr-required"], result.Warnings);
        Assert.True(Assert.Single(result.Pages).RequiresOcr);
    }

    [Fact]
    public async Task Mixed_native_and_graphical_pdf_requires_ocr_without_claiming_complete_coverage()
    {
        using var fixture = new LicenceFixture();
        var extractor = new SyncfusionPdfDocumentExtractor(
            new SyncfusionLicenceRegistration(() => fixture.Path, static _ => { }));

        var result = await extractor.ExtractAsync(CreateRetained(CreatePdfWithGraphicPage("native first page")), CancellationToken.None);

        Assert.False(result.IsComplete);
        Assert.Contains("native first page", result.Text, StringComparison.Ordinal);
        Assert.Equal(["pdf-ocr-required"], result.Warnings);
        Assert.Collection(
            result.Pages,
            page =>
            {
                Assert.Equal(0, page.PageIndex);
                Assert.Contains("native first page", page.Text, StringComparison.Ordinal);
                Assert.False(page.RequiresOcr);
            },
            page =>
            {
                Assert.Equal(1, page.PageIndex);
                Assert.Empty(page.Text);
                Assert.True(page.RequiresOcr);
            });
    }

    [Fact]
    public async Task Rasterizer_converts_only_the_retained_pdf_bytes_to_bounded_page_images()
    {
        using var fixture = new LicenceFixture();
        var rasterizer = new SyncfusionPdfPageRasterizer(
            new SyncfusionLicenceRegistration(() => fixture.Path, static _ => { }));

        var pages = await rasterizer.RenderAsync(CreateRetained(CreatePdf("Raster-only proof")), CancellationToken.None);

        var page = Assert.Single(pages);
        Assert.Equal(0, page.PageIndex);
        Assert.True(page.PngBytes.Length > 8);
        Assert.Equal((byte)137, page.PngBytes[0]);
    }

    [Fact]
    public async Task Rasterizer_renders_only_the_explicit_ocr_pages_in_original_page_coordinates()
    {
        using var fixture = new LicenceFixture();
        var rasterizer = new SyncfusionPdfPageRasterizer(
            new SyncfusionLicenceRegistration(() => fixture.Path, static _ => { }));

        var pages = await rasterizer.RenderAsync(
            CreateRetained(CreatePdfWithGraphicPage("native first page")),
            CancellationToken.None,
            new HashSet<int> { 1 });

        var page = Assert.Single(pages);
        Assert.Equal(1, page.PageIndex);
        Assert.True(page.PngBytes.Length > 8);
        Assert.Equal((byte)137, page.PngBytes[0]);
    }

    [Fact]
    [Trait("Category", "local-model-compatibility")]
    public async Task Native_ocr_reads_rendered_english_text_when_explicitly_requested()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("FLUX_KB_RUN_LOCAL_MODEL_COMPATIBILITY"), "1", StringComparison.Ordinal))
        {
            return;
        }

        using var fixture = new LicenceFixture();
        var rasterizer = new SyncfusionPdfPageRasterizer(
            new SyncfusionLicenceRegistration(() => fixture.Path, static _ => { }));
        var manifests = FixedDocumentOcrManifestReader.OpenProduction();
        var runtime = new DocumentOcrRuntime(
            new DocumentOcrModelBundleResolver(
                new LocalModelStore(WindowsModelVerificationFiles.OpenProduction),
                manifests.ReadAsync),
            new DirectMlDocumentOcrSessionFactory(deviceId: 0));
        var engine = new NativeDocumentOcrEngine(runtime);

        var result = await engine.ExtractAsync(
            await rasterizer.RenderAsync(CreateRetained(CreatePdf("Flux OCR 2026")), CancellationToken.None),
            CancellationToken.None);

        Assert.True(result.Succeeded, result.ReasonCode);
        Assert.Contains("Flux", result.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    [Trait("Category", "local-model-compatibility")]
    public async Task Native_ocr_reads_a_generated_arabic_line_when_explicitly_requested()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("FLUX_KB_RUN_LOCAL_MODEL_COMPATIBILITY"), "1", StringComparison.Ordinal))
        {
            return;
        }

        var manifests = FixedDocumentOcrManifestReader.OpenProduction();
        var runtime = new DocumentOcrRuntime(
            new DocumentOcrModelBundleResolver(
                new LocalModelStore(WindowsModelVerificationFiles.OpenProduction),
                manifests.ReadAsync),
            new DirectMlDocumentOcrSessionFactory(deviceId: 0));
        var engine = new NativeDocumentOcrEngine(runtime);

        var result = await engine.ExtractAsync(
            [new PdfRasterizedPage(0, CreatePng("مرحبا"))],
            CancellationToken.None);

        Assert.True(result.Succeeded, result.ReasonCode);
        Assert.Contains(result.Text, static character => character is >= '\u0600' and <= '\u06FF');
    }

    private static RetainedSourceBytes CreateRetained(byte[] bytes) => new(
        SourceRevisionId.New(),
        bytes,
        Convert.ToHexStringLower(SHA256.HashData(bytes)),
        bytes.Length);

    private static byte[] CreatePdf(string? text) => CreatePdfWithContents(
        text is null ? null : $"BT\n/F1 12 Tf\n72 720 Td\n({text}) Tj\nET\n");

    private static byte[] CreatePng(string text)
    {
        using var bitmap = new SKBitmap(1200, 300);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.White);
        using var paint = new SKPaint
        {
            Color = SKColors.Black,
            IsAntialias = true
        };
        using var font = new SKFont(SKTypeface.FromFamilyName("Segoe UI"), 128);
        canvas.DrawText(text, 120, 190, SKTextAlign.Left, font, paint);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, quality: 100);
        return data.ToArray();
    }

    private static byte[] CreatePdfWithGraphicPage(string text) => CreatePdfWithContents(
        $"BT\n/F1 12 Tf\n72 720 Td\n({text}) Tj\nET\n",
        "0 0 0 rg\n72 720 100 20 re f\n");

    private static byte[] CreatePdfWithContents(params string?[] pageContents)
    {
        var fontObjectId = 3 + (pageContents.Length * 2);
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            $"<< /Type /Pages /Kids [{string.Join(" ", Enumerable.Range(0, pageContents.Length).Select(index => $"{3 + (index * 2)} 0 R"))}] /Count {pageContents.Length} >>"
        };
        for (var index = 0; index < pageContents.Length; index++)
        {
            var pageObjectId = 3 + (index * 2);
            var contentObjectId = pageObjectId + 1;
            var contents = pageContents[index];
            if (contents is null)
            {
                objects.Add("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>");
                objects.Add("<< /Length 0 >>\nstream\nendstream");
            }
            else
            {
                objects.Add($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 {fontObjectId} 0 R >> >> /Contents {contentObjectId} 0 R >>");
                objects.Add($"<< /Length {Encoding.ASCII.GetByteCount(contents)} >>\nstream\n{contents}endstream");
            }
        }
        objects.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");
        var document = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>(objects.Count);
        for (var index = 0; index < objects.Count; index++)
        {
            offsets.Add(Encoding.ASCII.GetByteCount(document.ToString()));
            document.Append(index + 1).Append(" 0 obj\n").Append(objects[index]).Append("\nendobj\n");
        }

        var xrefOffset = Encoding.ASCII.GetByteCount(document.ToString());
        document.Append("xref\n0 ").Append(objects.Count + 1).Append("\n0000000000 65535 f \n");
        foreach (var offset in offsets)
        {
            document.Append(offset.ToString("D10", System.Globalization.CultureInfo.InvariantCulture)).Append(" 00000 n \n");
        }

        document.Append("trailer\n<< /Size ").Append(objects.Count + 1).Append(" /Root 1 0 R >>\nstartxref\n")
            .Append(xrefOffset).Append("\n%%EOF\n");
        return Encoding.ASCII.GetBytes(document.ToString());
    }

    private sealed class LicenceFixture : IDisposable
    {
        public LicenceFixture()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"flux-pdf-{Guid.NewGuid():N}.lic");
            File.WriteAllText(Path, "test-only-license", new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }

        public string Path { get; }

        public void Dispose()
        {
            if (File.Exists(Path))
            {
                File.Delete(Path);
            }
        }
    }
}
