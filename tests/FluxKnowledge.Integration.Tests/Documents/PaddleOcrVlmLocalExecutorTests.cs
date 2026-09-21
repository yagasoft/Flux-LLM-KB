using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluxKnowledge.Application.Documents;
using FluxKnowledge.Application.Models;
using FluxKnowledge.Application.Operations;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Sources;
using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Infrastructure.Inference.Models;
using FluxKnowledge.Integrations.Documents;
using FluxKnowledge.Integrations.Models;
using FluxKnowledge.Integrations.Windows.NativeGoLive;
using Syncfusion.Pdf;
using Syncfusion.Pdf.Graphics;
using SkiaSharp;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Documents;

public sealed class PaddleOcrVlmLocalExecutorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"FluxKnowledgeOcrExecutor_{Guid.NewGuid():N}");

    public PaddleOcrVlmLocalExecutorTests()
    {
        Directory.CreateDirectory(_root);
    }

    [Fact]
    public void Fixed_offline_python_worker_is_present_in_the_application_output()
    {
        Assert.True(File.Exists(PaddleOcrVlmPythonScript.Path), PaddleOcrVlmPythonScript.Path);
    }

    [Fact]
    public async Task Local_executor_refuses_a_model_cache_miss_before_rasterizing_or_launching_a_provider()
    {
        var rasterizer = new RecordingRasterizer();
        var runner = new RecordingProcessRunner();
        using var gate = CreateGate(failStore: true);
        var executor = new PaddleOcrVlmLocalExecutor(
            rasterizer,
            gate,
            LiveRootLayout.CreateForIsolatedTests(_root),
            runner);

        var result = await executor.ExecuteAsync(CreateRetained(), [0], CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal("document-ocr-model-artifact-missing", result.ReasonCode);
        Assert.Equal(0, rasterizer.Calls);
        Assert.Equal(0, runner.Calls);
    }

    [Fact]
    public async Task Local_executor_passes_only_selected_rendered_pages_to_the_fixed_process_and_removes_temporary_files()
    {
        var rasterizer = new RecordingRasterizer(90, 1200, 800, "rotate-90");
        var runner = new RecordingProcessRunner();
        using var gate = CreateGate();
        var layout = LiveRootLayout.CreateForIsolatedTests(_root);
        Directory.CreateDirectory(layout.TempRoot);
        var executor = new PaddleOcrVlmLocalExecutor(rasterizer, gate, layout, runner);

        var result = await executor.ExecuteAsync(CreateRetained(), [2], CancellationToken.None);

        Assert.True(result.Succeeded, result.ReasonCode);
        var page = Assert.Single(result.Pages);
        Assert.Equal(2, page.PageIndex);
        Assert.Equal(90, page.OrientationDegrees);
        Assert.Equal(1200, page.SourceWidth);
        Assert.Equal(800, page.SourceHeight);
        Assert.Equal("rotate-90", page.SourceTransform);
        Assert.Equal("OCR evidence", Assert.Single(page.Blocks).Text);
        Assert.Equal(1, rasterizer.Calls);
        Assert.Equal([2], rasterizer.PageSelections.Single());
        var request = Assert.Single(runner.Requests);
        Assert.Equal([2], request.PageIndexes);
        Assert.All(request.ImageFilenames, filename => Assert.DoesNotContain(Path.DirectorySeparatorChar, filename));
        var executionRoot = Path.Combine(layout.TempRoot, "document-ocr");
        Assert.True(Directory.Exists(executionRoot));
        Assert.Empty(Directory.EnumerateDirectories(executionRoot));
    }

    [Fact]
    public async Task Document_rasterizer_accepts_one_retained_png_as_page_zero()
    {
        using var bitmap = new SKBitmap(320, 120);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.White);
            using var paint = new SKPaint { Color = SKColors.Black };
            canvas.DrawRect(20, 20, 280, 80, paint);
        }
        using var image = SKImage.FromBitmap(bitmap);
        using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
        var bytes = encoded.ToArray();
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var retained = new RetainedSourceBytes(SourceRevisionId.New(), bytes, hash, bytes.Length);

        var pages = await new SyncfusionPdfPageRasterizer(new SyncfusionLicenceRegistration())
            .RenderAsync(retained, CancellationToken.None, new HashSet<int> { 0 });

        var page = Assert.Single(pages);
        Assert.Equal(0, page.PageIndex);
        using var decoded = SKBitmap.Decode(page.PngBytes);
        Assert.Equal(320, decoded.Width);
        Assert.Equal(120, decoded.Height);
    }

    [Fact]
    public async Task Document_rasterizer_records_jpeg_exif_rotation_and_rejects_mirroring()
    {
        using var bitmap = new SKBitmap(80, 40);
        using var image = SKImage.FromBitmap(bitmap);
        using var encoded = image.Encode(SKEncodedImageFormat.Jpeg, 90);
        var rasterizer = new SyncfusionPdfPageRasterizer(new SyncfusionLicenceRegistration());

        var rotated = Assert.Single(await rasterizer.RenderAsync(
            CreateRetained(WithExifOrientation(encoded.ToArray(), 6)), CancellationToken.None, new HashSet<int> { 0 }));

        Assert.Equal(90, rotated.SourceOrientationDegrees);
        Assert.Equal(80, rotated.SourceWidth);
        Assert.Equal(40, rotated.SourceHeight);
        Assert.Equal("rotate-90", rotated.SourceTransform);
        using var rotatedBitmap = SKBitmap.Decode(rotated.PngBytes);
        Assert.Equal(40, rotatedBitmap.Width);
        Assert.Equal(80, rotatedBitmap.Height);
        var refusal = await Assert.ThrowsAsync<RetainedProcessorException>(async () => await rasterizer.RenderAsync(
            CreateRetained(WithExifOrientation(encoded.ToArray(), 2)), CancellationToken.None, new HashSet<int> { 0 }));
        Assert.Equal("image-document-orientation-unsupported", refusal.OutcomeCode);
    }

    [Fact]
    [Trait("Category", "local-ocr")]
    public async Task Fixed_local_gpu_ocr_reads_a_rendered_english_pdf_when_explicitly_requested()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("FLUX_KB_RUN_LOCAL_PADDLE_OCR"), "1", StringComparison.Ordinal))
        {
            return;
        }

        var root = Path.Combine(Path.GetTempPath(), $"FluxKnowledgeLocalPaddleOcr_{Guid.NewGuid():N}");
        try
        {
            var layout = LiveRootLayout.CreateForIsolatedTests(root);
            Directory.CreateDirectory(layout.TempRoot);
            using var gate = new PaddleOcrVlmModelGate(new LocalModelStore(WindowsModelVerificationFiles.OpenProduction));
            var licence = new SyncfusionLicenceRegistration();
            licence.EnsureRegistered();
            var executor = new PaddleOcrVlmLocalExecutor(
                new SyncfusionPdfPageRasterizer(licence),
                gate,
                layout);

            var result = await executor.ExecuteAsync(
                CreateRetained(CreateVisibleTextPdf("Flux OCR 2026")),
                [0],
                CancellationToken.None);

            Assert.True(result.Succeeded, result.ReasonCode);
            var text = string.Join('\n', result.Pages.SelectMany(static page => page.Blocks).Select(static block => block.Text));
            Assert.Contains("Flux", text, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("2026", text, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    [Trait("Category", "local-ocr")]
    public async Task Fixed_local_gpu_ocr_reads_a_retained_english_image_when_explicitly_requested()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("FLUX_KB_RUN_LOCAL_PADDLE_OCR"), "1", StringComparison.Ordinal))
            return;

        var root = Path.Combine(Path.GetTempPath(), $"FluxKnowledgeLocalImageOcr_{Guid.NewGuid():N}");
        try
        {
            var layout = LiveRootLayout.CreateForIsolatedTests(root);
            Directory.CreateDirectory(layout.TempRoot);
            using var gate = new PaddleOcrVlmModelGate(new LocalModelStore(WindowsModelVerificationFiles.OpenProduction));
            var executor = new PaddleOcrVlmLocalExecutor(
                new SyncfusionPdfPageRasterizer(new SyncfusionLicenceRegistration()), gate, layout);
            using var bitmap = new SKBitmap(1400, 360);
            using (var canvas = new SKCanvas(bitmap))
            using (var paint = new SKPaint { Color = SKColors.Black, IsAntialias = true })
            using (var font = new SKFont(SKTypeface.Default, 96))
            {
                canvas.Clear(SKColors.White);
                canvas.DrawText("Flux Image OCR 2026", 80, 210, SKTextAlign.Left, font, paint);
            }
            using var image = SKImage.FromBitmap(bitmap);
            using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);

            var result = await executor.ExecuteAsync(CreateRetained(encoded.ToArray()), [0], CancellationToken.None);

            Assert.True(result.Succeeded, result.ReasonCode);
            var text = string.Join('\n', result.Pages.SelectMany(static page => page.Blocks).Select(static block => block.Text));
            Assert.Contains("Flux", text, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("2026", text, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    [Trait("Category", "local-ocr")]
    public async Task Fixed_local_pdf_classifier_keeps_a_truly_blank_page_out_of_ocr_when_explicitly_requested()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("FLUX_KB_RUN_LOCAL_PADDLE_OCR"), "1", StringComparison.Ordinal))
        {
            return;
        }

        var licence = new SyncfusionLicenceRegistration();
        licence.EnsureRegistered();
        var extraction = await new SyncfusionPdfDocumentExtractor(licence).ExtractAsync(
            CreateRetained(CreateBlankPdf()),
            CancellationToken.None);

        Assert.False(extraction.IsComplete);
        Assert.Equal(["pdf-no-extractable-text"], extraction.Warnings);
        var page = Assert.Single(extraction.Pages);
        Assert.Empty(page.Text);
        Assert.False(page.RequiresOcr);
    }

    [Fact]
    [Trait("Category", "local-ocr")]
    public async Task Fixed_local_gpu_ocr_reads_only_the_scanned_page_of_a_mixed_pdf_when_explicitly_requested()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("FLUX_KB_RUN_LOCAL_PADDLE_OCR"), "1", StringComparison.Ordinal))
        {
            return;
        }

        const string publicRotatedScan =
            @"E:\Temp\flux-ocr-assessment-20260919\public-scan-supplement\phototest-rotated-L.png";
        Assert.True(File.Exists(publicRotatedScan), "The approved public rotated-scan fixture is required for this opt-in test.");

        var root = Path.Combine(Path.GetTempPath(), $"FluxKnowledgeLocalPaddleOcrMixed_{Guid.NewGuid():N}");
        try
        {
            var licence = new SyncfusionLicenceRegistration();
            licence.EnsureRegistered();
            var retained = CreateRetained(CreateMixedPdfWithImage("native page text", publicRotatedScan));
            var extraction = await new SyncfusionPdfDocumentExtractor(licence).ExtractAsync(retained, CancellationToken.None);
            Assert.False(extraction.IsComplete);
            Assert.Collection(
                extraction.Pages,
                page =>
                {
                    Assert.Equal(0, page.PageIndex);
                    Assert.Contains("native page text", page.Text, StringComparison.OrdinalIgnoreCase);
                    Assert.False(page.RequiresOcr);
                },
                page =>
                {
                    Assert.Equal(1, page.PageIndex);
                    Assert.Empty(page.Text);
                    Assert.True(page.RequiresOcr);
                });

            var layout = LiveRootLayout.CreateForIsolatedTests(root);
            Directory.CreateDirectory(layout.TempRoot);
            using var gate = new PaddleOcrVlmModelGate(new LocalModelStore(WindowsModelVerificationFiles.OpenProduction));
            var executor = new PaddleOcrVlmLocalExecutor(
                new SyncfusionPdfPageRasterizer(licence),
                gate,
                layout);

            var result = await executor.ExecuteAsync(retained, [1], CancellationToken.None);

            Assert.True(result.Succeeded, result.ReasonCode);
            var pageResult = Assert.Single(result.Pages);
            Assert.Equal(1, pageResult.PageIndex);
            var text = string.Join('\n', pageResult.Blocks.Select(static block => block.Text));
            Assert.Contains("12 point text", text, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("quick brown dog", text, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static PaddleOcrVlmModelGate CreateGate(bool failStore = false) => new(
        new GateStore(failStore),
        static (path, _, _) => ValueTask.FromResult(Manifest(path)),
        static (_, _) => ValueTask.FromResult<IDisposable>(new NoopDisposable()));

    private static byte[] Manifest(string path)
    {
        var revision = path switch
        {
            PaddleOcrVlmModelGate.VlmManifestPath => PaddleOcrVlmModelGate.VlmRevision,
            PaddleOcrVlmModelGate.LayoutManifestPath => PaddleOcrVlmModelGate.LayoutRevision,
            _ => PaddleOcrVlmModelGate.OrientationRevision
        };
        var bytes = Encoding.UTF8.GetBytes(path);
        return JsonSerializer.SerializeToUtf8Bytes(new
        {
            schemaVersion = 1,
            files = new[]
            {
                new
                {
                    revision,
                    filename = "model.bin",
                    sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)),
                    byteLength = bytes.LongLength
                }
            }
        });
    }

    private static RetainedSourceBytes CreateRetained() => CreateRetained("%PDF-1.7\n"u8.ToArray());

    private static RetainedSourceBytes CreateRetained(byte[] bytes)
    {
        return new RetainedSourceBytes(
            SourceRevisionId.New(),
            bytes,
            Convert.ToHexStringLower(SHA256.HashData(bytes)),
            bytes.Length);
    }

    private static byte[] CreateVisibleTextPdf(string text)
    {
        using var document = new PdfDocument();
        var page = document.Pages.Add();
        page.Graphics.DrawString(
            text,
            new PdfStandardFont(PdfFontFamily.Helvetica, 36),
            PdfBrushes.Black,
            new Syncfusion.Drawing.PointF(72, 72));
        using var output = new MemoryStream();
        document.Save(output);
        return output.ToArray();
    }

    private static byte[] CreateBlankPdf()
    {
        using var document = new PdfDocument();
        document.Pages.Add();
        using var output = new MemoryStream();
        document.Save(output);
        return output.ToArray();
    }

    private static byte[] CreateMixedPdfWithImage(string nativeText, string imagePath)
    {
        using var document = new PdfDocument();
        var nativePage = document.Pages.Add();
        nativePage.Graphics.DrawString(
            nativeText,
            new PdfStandardFont(PdfFontFamily.Helvetica, 24),
            PdfBrushes.Black,
            new Syncfusion.Drawing.PointF(72, 72));
        var scannedPage = document.Pages.Add();
        using var source = new MemoryStream(File.ReadAllBytes(imagePath), writable: false);
        using var image = new PdfBitmap(source);
        scannedPage.Graphics.DrawImage(image, new Syncfusion.Drawing.RectangleF(40, 140, 500, 375));
        using var output = new MemoryStream();
        document.Save(output);
        return output.ToArray();
    }

    private static byte[] WithExifOrientation(byte[] jpeg, ushort orientation)
    {
        var exif = new byte[]
        {
            0xff, 0xe1, 0x00, 0x22,
            (byte)'E', (byte)'x', (byte)'i', (byte)'f', 0x00, 0x00,
            (byte)'I', (byte)'I', 0x2a, 0x00, 0x08, 0x00, 0x00, 0x00,
            0x01, 0x00,
            0x12, 0x01, 0x03, 0x00, 0x01, 0x00, 0x00, 0x00,
            (byte)orientation, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00
        };
        var result = new byte[jpeg.Length + exif.Length];
        jpeg.AsSpan(0, 2).CopyTo(result);
        exif.CopyTo(result, 2);
        jpeg.AsSpan(2).CopyTo(result.AsSpan(2 + exif.Length));
        return result;
    }

    private sealed class RecordingRasterizer(
        int sourceOrientationDegrees = 0,
        int? sourceWidth = null,
        int? sourceHeight = null,
        string? sourceTransform = null) : IPdfPageRasterizer
    {
        public int Calls { get; private set; }
        public List<IReadOnlyList<int>> PageSelections { get; } = [];

        public ValueTask<IReadOnlyList<PdfRasterizedPage>> RenderAsync(
            RetainedSourceBytes retained,
            CancellationToken cancellationToken,
            IReadOnlySet<int>? pageIndexes = null)
        {
            Calls++;
            var selectedPages = pageIndexes ?? throw new Xunit.Sdk.XunitException("The executor must select explicit OCR pages.");
            PageSelections.Add(selectedPages.Order().ToArray());
            return ValueTask.FromResult<IReadOnlyList<PdfRasterizedPage>>(
                selectedPages.Order().Select(index => new PdfRasterizedPage(
                    index,
                    [137, 80, 78, 71],
                    sourceOrientationDegrees,
                    sourceWidth,
                    sourceHeight,
                    sourceTransform)).ToArray());
        }
    }

    private sealed class RecordingProcessRunner : IPaddleOcrVlmProcessRunner
    {
        public int Calls { get; private set; }
        public List<PaddleOcrVlmProcessRequest> Requests { get; } = [];

        public async ValueTask<PaddleOcrVlmProcessResult> RunAsync(
            PaddleOcrVlmProcessRequest request,
            CancellationToken cancellationToken)
        {
            Calls++;
            Requests.Add(request);
            var output = new
            {
                succeeded = true,
                reasonCode = "document-ocr-complete",
                pages = request.PageIndexes.Select(index => new
                {
                    pageIndex = index,
                    orientationDegrees = 0,
                    blocks = new[]
                    {
                        new { kind = "text", left = 1, top = 2, width = 3, height = 4, text = "OCR evidence" }
                    }
                }).ToArray()
            };
            await File.WriteAllBytesAsync(request.OutputPath, JsonSerializer.SerializeToUtf8Bytes(output), cancellationToken);
            return PaddleOcrVlmProcessResult.Completed;
        }
    }

    private sealed class GateStore(bool failStore) : ILocalModelStore
    {
        public ValueTask<ModelResolutionResult> ResolveAsync(
            ModelBundleSpecification specification,
            CancellationToken cancellationToken)
        {
            if (failStore)
            {
                return ValueTask.FromResult(new ModelResolutionResult(
                    false,
                    ModelStoreReasons.ArtifactMissing,
                    null,
                    [],
                    true,
                    "inventory/verifications/refusal.json",
                    null));
            }

            var file = specification.Files.Single();
            var lease = new VerifiedLocalModelLease(
                new Dictionary<string, IModelVerificationFile>
                {
                    [file.Filename] = new EmptyModelFile(file.ByteLength)
                },
                new NoopDisposable());
            return ValueTask.FromResult(new ModelResolutionResult(
                true,
                ModelStoreReasons.BundleVerified,
                ModelManifestCodec.Fingerprint(specification),
                [],
                true,
                "inventory/verifications/success.json",
                lease));
        }
    }

    private sealed class EmptyModelFile(long byteLength) : IModelVerificationFile
    {
        public long ByteLength => byteLength;

        public ValueTask<int> ReadAsync(long offset, Memory<byte> buffer, CancellationToken cancellationToken) =>
            ValueTask.FromResult(0);

        public void Dispose()
        {
        }
    }

    private sealed class NoopDisposable : IDisposable
    {
        public void Dispose()
        {
        }
    }
}
