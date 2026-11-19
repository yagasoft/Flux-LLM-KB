using System.Diagnostics;
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
using Xunit.Abstractions;

namespace FluxKnowledge.Integration.Tests.Documents;

public sealed class PaddleOcrVlmLocalExecutorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"FluxKnowledgeOcrExecutor_{Guid.NewGuid():N}");

    private readonly ITestOutputHelper _output;

    public PaddleOcrVlmLocalExecutorTests(ITestOutputHelper output)
    {
        _output = output;
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

        var result = await executor.ExecuteAsync(CreateRetained(), [0], OcrTestExecutionContext.Create(), CancellationToken.None);

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

        var result = await executor.ExecuteAsync(CreateRetained(), [2], OcrTestExecutionContext.Create(), CancellationToken.None);

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

    [Theory]
    [InlineData("complete", "document-ocr-complete", true, false)]
    [InlineData("wrong-page", "document-ocr-provider-result-invalid", true, false)]
    [InlineData("provider-failure", "document-ocr-recognition-failed", false, true)]
    [InlineData("wrong-model", "document-ocr-native-supervision-failed", false, true)]
    [InlineData("refused", "document-ocr-native-start-refused", false, false)]
    public async Task Warm_executor_binds_the_native_command_and_separates_result_from_idle_and_release(
        string behaviour, string reason, bool expectIdle, bool expectRelease)
    {
        var rasterizer = new RecordingRasterizer();
        var coldRunner = new RecordingProcessRunner();
        using var modelGate = CreateGate();
        var layout = LiveRootLayout.CreateForIsolatedTests(_root);
        Directory.CreateDirectory(layout.TempRoot);
        var executor = new PaddleOcrVlmLocalExecutor(rasterizer, modelGate, layout, coldRunner);
        var execution = OcrTestExecutionContext.Create(TimeSpan.FromSeconds(20));
        var residentId = Guid.NewGuid();
        await using var worker = OwnedWarmModelProcess.Start(WarmOcrChild(behaviour), residentId,
            execution.Lease.Supervisor.Process.MachineFingerprint);
        using var observedJob = OwnedOcrJob.TryOpen(residentId);
        Assert.NotNull(observedJob);
        var root = Path.Combine(layout.TempRoot, "document-ocr");
        DocumentOcrExecutionLease? authorizedLease = null;
        OwnedWarmModelCommand? authorizedCommand = null;
        string[]? filesBeforePermission = null;

        var outcome = await executor.ExecuteWarmAsync(CreateRetained(), [2], execution, worker,
            (lease, command, _) =>
            {
                authorizedLease = lease;
                authorizedCommand = command;
                filesBeforePermission = Directory.GetFiles(Directory.GetDirectories(root).Single())
                    .Select(Path.GetFileName).Order().ToArray()!;
                return ValueTask.FromResult(behaviour != "refused");
            }, CancellationToken.None);

        Assert.Equal(0, coldRunner.Calls);
        Assert.Equal(execution.Lease, authorizedLease);
        Assert.NotNull(authorizedCommand);
        Assert.Equal(residentId, authorizedCommand.ResidentId);
        Assert.Equal(execution.Lease.ExecutionInstanceId, authorizedCommand.ExecutionId);
        Assert.Equal(new[] { "input.json", "page-00000002.png" }, filesBeforePermission);
        Assert.Equal(reason, outcome.Result.ReasonCode);
        Assert.Equal(behaviour == "complete", outcome.Result.Succeeded);
        Assert.Equal(expectIdle, outcome.Idle is not null);
        Assert.Equal(expectRelease, outcome.NativeReleaseConfirmed);
        Assert.Equal(expectRelease, observedJob.ActiveProcesses == 0);
        if (outcome.Idle is not null)
        {
            Assert.Equal("ocr", outcome.Idle.Model);
            Assert.Equal(authorizedCommand.Process, outcome.Idle.Process);
            Assert.Equal(authorizedCommand.ExecutionId, outcome.Idle.ExecutionId);
        }
        Assert.Empty(Directory.EnumerateFileSystemEntries(root));
    }

    [Fact]
    public async Task Warm_executor_does_not_send_or_authorize_a_command_after_the_claim_deadline()
    {
        using var modelGate = CreateGate();
        var layout = LiveRootLayout.CreateForIsolatedTests(_root);
        Directory.CreateDirectory(layout.TempRoot);
        var executor = new PaddleOcrVlmLocalExecutor(new RecordingRasterizer(), modelGate, layout, new RecordingProcessRunner());
        var context = OcrTestExecutionContext.Create();
        var execution = context with
        {
            Lease = context.Lease with
            {
                ClaimedAtUtc = context.Lease.ClaimedAtUtc.AddMinutes(-2),
                DeadlineAtUtc = context.Lease.ClaimedAtUtc.AddMinutes(-1),
            }
        };
        await using var worker = OwnedWarmModelProcess.Start(WarmOcrChild("complete"), Guid.NewGuid(),
            execution.Lease.Supervisor.Process.MachineFingerprint);
        var authorized = false;
        var outcome = await executor.ExecuteWarmAsync(CreateRetained(), [2], execution, worker,
            (_, _, _) =>
            {
                authorized = true;
                return ValueTask.FromResult(true);
            }, CancellationToken.None);
        Assert.False(authorized);
        Assert.Null(outcome.Idle);
        Assert.False(outcome.NativeReleaseConfirmed);
        Assert.Equal("document-ocr-runtime-timeout", outcome.Result.ReasonCode);
    }

    [Fact]
    public async Task Warm_executor_confirms_release_when_the_native_child_misses_the_claim_deadline()
    {
        using var modelGate = CreateGate();
        var layout = LiveRootLayout.CreateForIsolatedTests(_root);
        Directory.CreateDirectory(layout.TempRoot);
        var executor = new PaddleOcrVlmLocalExecutor(new RecordingRasterizer(), modelGate, layout, new RecordingProcessRunner());
        var execution = OcrTestExecutionContext.Create(TimeSpan.FromSeconds(3));
        var residentId = Guid.NewGuid();
        await using var worker = OwnedWarmModelProcess.Start(WarmOcrChild("slow"), residentId,
            execution.Lease.Supervisor.Process.MachineFingerprint);
        using var observedJob = OwnedOcrJob.TryOpen(residentId);
        Assert.NotNull(observedJob);
        var authorized = false;

        var run = executor.ExecuteWarmAsync(CreateRetained(), [2], execution, worker,
            (_, _, _) =>
            {
                authorized = true;
                return ValueTask.FromResult(true);
            }, CancellationToken.None).AsTask();
        try
        {
            var outcome = await run.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(authorized);
            Assert.Null(outcome.Idle);
            Assert.True(outcome.NativeReleaseConfirmed);
            Assert.Equal("document-ocr-runtime-timeout", outcome.Result.ReasonCode);
            Assert.Equal(0u, observedJob.ActiveProcesses);
        }
        finally
        {
            if (!run.IsCompleted)
                await observedJob.TerminateAndConfirmAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
        }
    }

    [Fact]
    public async Task Warm_launch_checks_the_fixed_model_gate_before_creating_runtime_storage()
    {
        using var modelGate = CreateGate(failStore: true);
        var layout = LiveRootLayout.CreateForIsolatedTests(_root);
        Directory.CreateDirectory(layout.TempRoot);
        var executor = new PaddleOcrVlmLocalExecutor(new RecordingRasterizer(), modelGate, layout, new RecordingProcessRunner());

        var refusal = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            executor.StartWarmProcessAsync(Guid.NewGuid(), new string('a', 64), CancellationToken.None).AsTask());

        Assert.Equal("document-ocr-model-artifact-missing", refusal.Message);
        Assert.Empty(Directory.EnumerateFileSystemEntries(layout.TempRoot));
    }


    private static ProcessStartInfo WarmOcrChild(string behaviour)
    {
        var script = """
            while ($frame = [Console]::ReadLine()) {
                $request = ConvertFrom-Json -InputObject $frame
                if ($request.operation -eq 'shutdown') { exit 0 }
                $manifest = Get-Content -LiteralPath $request.manifest -Raw | ConvertFrom-Json
                $page = $manifest.pages[0]
                $png = Join-Path (Split-Path -Parent $request.manifest) $page.image
                if ($manifest.pages.Count -ne 1 -or $page.pageIndex -ne 2 -or -not (Test-Path -LiteralPath $png)) { exit 96 }
                $model = 'ocr'
                $result = @{succeeded=$true; reasonCode='document-ocr-complete'; pages=@(
                    @{pageIndex=2; orientationDegrees=0; blocks=@()})}
                __BEHAVIOUR__
                [IO.File]::WriteAllText($request.output, ($result | ConvertTo-Json -Compress -Depth 5))
                if (-not $result.succeeded) { exit 1 }
                [Console]::WriteLine((@{execution_id=$request.execution_id;status='idle';model=$model} | ConvertTo-Json -Compress))
            }
            """.Replace("__BEHAVIOUR__", behaviour switch
            {
                "wrong-page" => "$result.pages[0].pageIndex = 3",
                "provider-failure" => "$result = @{succeeded=$false;reasonCode='document-ocr-recognition-failed';pages=@()}",
                "wrong-model" => "$model = 'general'",
                "slow" => "Start-Sleep -Seconds 45",
                _ => "",
            });
        var start = new ProcessStartInfo("powershell.exe");
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-NonInteractive");
        start.ArgumentList.Add("-EncodedCommand");
        start.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(script)));
        return start;
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

    [Theory]
    [InlineData(7780, 12)]
    [InlineData(5120, 5120)]
    public async Task Document_rasterizer_preserves_full_image_resolution_within_its_decoded_memory_budget(int width, int height)
    {
        byte[] bytes;
        using (var source = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Premul))
        {
            source.Erase(SKColors.White);
            using var canvas = new SKCanvas(source);
            using var paint = new SKPaint { Color = SKColors.Black };
            canvas.DrawRect(1, 1, 8, 8, paint);
            canvas.Flush();
            using var image = SKImage.FromBitmap(source);
            using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
            bytes = encoded.ToArray();
        }
        var page = Assert.Single(await new SyncfusionPdfPageRasterizer(new SyncfusionLicenceRegistration())
            .RenderAsync(CreateRetained(bytes), CancellationToken.None, new HashSet<int> { 0 }));

        Assert.Equal(width, page.SourceWidth);
        Assert.Equal(height, page.SourceHeight);
        Assert.Equal("identity", page.SourceTransform);
        using var decoded = SKBitmap.Decode(page.PngBytes);
        Assert.Equal(width, decoded.Width);
        Assert.Equal(height, decoded.Height);
        Assert.Equal(SKColors.Black, decoded.GetPixel(3, 3));
    }

    [Fact]
    public async Task Document_rasterizer_refuses_a_declared_image_allocation_before_decoding_or_launching_any_provider()
    {
        byte[] bytes;
        using (var source = new SKBitmap(16, 16))
        using (var image = SKImage.FromBitmap(source))
        using (var encoded = image.Encode(SKEncodedImageFormat.Jpeg, 90))
            bytes = encoded.ToArray();
        var frame = Enumerable.Range(2, bytes.Length - 11).First(index => bytes[index] == 0xff && bytes[index + 1] == 0xc0);
        bytes[frame + 5] = 0x40; bytes[frame + 6] = 0;
        bytes[frame + 7] = 0x40; bytes[frame + 8] = 0;
        using (var codec = SKCodec.Create(new MemoryStream(bytes, writable: false)))
        {
            Assert.NotNull(codec);
            Assert.Equal(16_384, codec.Info.Width);
            Assert.Equal(16_384, codec.Info.Height);
        }

        var failure = await Assert.ThrowsAsync<RetainedProcessorException>(() =>
            new SyncfusionPdfPageRasterizer(new SyncfusionLicenceRegistration())
                .RenderAsync(CreateRetained(bytes), CancellationToken.None, new HashSet<int> { 0 }).AsTask());

        Assert.Equal("image-document-decoded-memory-budget-exceeded", failure.OutcomeCode);
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

    [Theory]
    [InlineData(8192, 8192, 1, null)]
    [InlineData(8192, 8193, 1, "image-document-decoded-memory-budget-exceeded")]
    [InlineData(8192, 4096, 6, null)]
    [InlineData(8192, 4097, 6, "image-document-decoded-memory-budget-exceeded")]
    public async Task Document_rasterizer_accounts_for_both_rotation_bitmaps_at_the_allocation_boundary(
        int width, int height, int orientation, string? expectedReason)
    {
        byte[] bytes;
        using (var source = new SKBitmap(16, 16))
        using (var image = SKImage.FromBitmap(source))
        using (var encoded = image.Encode(SKEncodedImageFormat.Jpeg, 90))
            bytes = encoded.ToArray();
        var frame = Enumerable.Range(2, bytes.Length - 11).First(index => bytes[index] == 0xff && bytes[index + 1] == 0xc0);
        bytes[frame + 5] = (byte)(height >> 8); bytes[frame + 6] = (byte)height;
        bytes[frame + 7] = (byte)(width >> 8); bytes[frame + 8] = (byte)width;
        bytes = WithExifOrientation(bytes, checked((ushort)orientation));

        var rasterizer = new SyncfusionPdfPageRasterizer(new SyncfusionLicenceRegistration());
        if (expectedReason is null)
        {
            // The JPEG codec accepts and pads this synthetic fixture. At the exact
            // allocation budget the full declared image must survive orientation.
            var page = Assert.Single(await rasterizer.RenderAsync(CreateRetained(bytes), CancellationToken.None, new HashSet<int> { 0 }));
            using var decoded = SKBitmap.Decode(page.PngBytes);
            Assert.Equal(orientation == 6 ? height : width, decoded.Width);
            Assert.Equal(orientation == 6 ? width : height, decoded.Height);
        }
        else
        {
            var failure = await Assert.ThrowsAsync<RetainedProcessorException>(() =>
                rasterizer.RenderAsync(CreateRetained(bytes), CancellationToken.None, new HashSet<int> { 0 }).AsTask());
            Assert.Equal(expectedReason, failure.OutcomeCode);
        }
    }

    [Fact]
    public async Task Document_rasterizer_refuses_large_encoded_output_within_the_decoded_bitmap_budget()
    {
        byte[] bytes;
        using (var bitmap = new SKBitmap(2400, 2400, SKColorType.Bgra8888, SKAlphaType.Premul))
        {
            var noise = new byte[bitmap.ByteCount];
            new Random(74561).NextBytes(noise);
            for (var offset = 3; offset < noise.Length; offset += 4) noise[offset] = byte.MaxValue;
            System.Runtime.InteropServices.Marshal.Copy(noise, 0, bitmap.GetPixels(), noise.Length);
            using var pixmap = bitmap.PeekPixels();
            using var encoded = pixmap.Encode(SKEncodedImageFormat.Png, 100);
            Assert.NotNull(encoded);
            bytes = encoded.ToArray();
        }
        Assert.True(bytes.Length > SyncfusionPdfPageRasterizer.MaximumPagePngBytes);
        Assert.True(bytes.Length < ImageDocumentInputProcessor.MaximumInputBytes);

        var failure = await Assert.ThrowsAsync<RetainedProcessorException>(() =>
            new SyncfusionPdfPageRasterizer(new SyncfusionLicenceRegistration())
                .RenderAsync(CreateRetained(bytes), CancellationToken.None, new HashSet<int> { 0 }).AsTask());

        Assert.Equal("image-ocr-raster-page-output-too-large", failure.OutcomeCode);
    }

    [Fact]
    [Trait("Category", "local-ocr-warm")]
    public async Task Fixed_pinned_GPU_OCR_runs_two_documents_in_the_same_warm_worker()
    {
        if (Environment.GetEnvironmentVariable("FLUX_KB_RUN_LOCAL_PADDLE_OCR_WARM") != "1") return;

        var layout = LiveRootLayout.CreateForIsolatedTests(_root);
        Directory.CreateDirectory(layout.TempRoot);
        using var gate = new PaddleOcrVlmModelGate(new LocalModelStore(WindowsModelVerificationFiles.OpenProduction));
        var executor = new PaddleOcrVlmLocalExecutor(new SyncfusionPdfPageRasterizer(new SyncfusionLicenceRegistration()), gate, layout);
        var first = OcrTestExecutionContext.Create(TimeSpan.FromMinutes(2));
        using var budget = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var token = budget.Token;
        var residentId = Guid.NewGuid();
        _output.WriteLine($"GPU before launch (used/free MiB): {await ReadGpuMemoryAsync(token)}");
        await using var worker = await executor.StartWarmProcessAsync(residentId, first.Lease.Supervisor.Process.MachineFingerprint, token);
        using var observedJob = OwnedOcrJob.TryOpen(residentId);
        Assert.NotNull(observedJob);

        var cold = await RunAsync(first, "2026");
        var second = OcrTestExecutionContext.Create(TimeSpan.FromMinutes(2));
        var warmed = await RunAsync(second, "2027");
        Assert.Equal(cold.Process, warmed.Process);
        Assert.NotEqual(cold.ExecutionId, warmed.ExecutionId);
        Assert.True(observedJob.ActiveProcesses > 0);
        Assert.True(await worker.StopAndConfirmAsync(token));
        Assert.Equal(0u, observedJob.ActiveProcesses);
        _output.WriteLine($"GPU after release (used/free MiB): {await ReadGpuMemoryAsync(token)}");

        async Task<OwnedWarmModelIdle> RunAsync(DocumentOcrExecutionContext execution, string year)
        {
            using var bitmap = new SKBitmap(1400, 360);
            using (var canvas = new SKCanvas(bitmap))
            using (var paint = new SKPaint { Color = SKColors.Black, IsAntialias = true })
            using (var font = new SKFont(SKTypeface.Default, 96))
            {
                canvas.Clear(SKColors.White);
                canvas.DrawText($"Flux OCR {year}", 80, 210, SKTextAlign.Left, font, paint);
            }
            using var image = SKImage.FromBitmap(bitmap);
            using var png = image.Encode(SKEncodedImageFormat.Png, 100);
            var retained = CreateRetained(png.ToArray());
            var started = Stopwatch.GetTimestamp();
            var result = await executor.ExecuteWarmAsync(retained, [0],
                execution, worker, (lease, command, _) => ValueTask.FromResult(
                    lease == execution.Lease && command.ExecutionId == lease.ExecutionInstanceId && command.ResidentId == residentId), token);
            var elapsed = Stopwatch.GetElapsedTime(started);
            Assert.True(result.Result.Succeeded, result.Result.ReasonCode);
            Assert.False(result.NativeReleaseConfirmed);
            var idle = Assert.IsType<OwnedWarmModelIdle>(result.Idle);
            Assert.Equal("ocr", idle.Model);
            var page = Assert.Single(result.Result.Pages);
            var text = string.Join(' ', page.Blocks.Select(block => block.Text));
            Assert.Contains("Flux", text, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(year, text, StringComparison.Ordinal);
            _output.WriteLine($"Request {year}: {elapsed.TotalSeconds:F3}s, PID {idle.Process.ProcessId}, GPU idle (used/free MiB): {await ReadGpuMemoryAsync(token)}");
            return idle;
        }
    }

    private static async Task<string> ReadGpuMemoryAsync(CancellationToken ct)
    {
        var query = new ProcessStartInfo("nvidia-smi")
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true,
        };
        query.ArgumentList.Add("--query-gpu=memory.used,memory.free");
        query.ArgumentList.Add("--format=csv,noheader,nounits");
        query.ArgumentList.Add("--id=0");
        using var process = Process.Start(query) ?? throw new InvalidOperationException("OCR test GPU query did not start.");
        var output = await process.StandardOutput.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        Assert.Equal(0, process.ExitCode);
        return output.Trim();
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
                OcrTestExecutionContext.Create(), CancellationToken.None);

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

            var result = await executor.ExecuteAsync(CreateRetained(encoded.ToArray()), [0], OcrTestExecutionContext.Create(), CancellationToken.None);

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

            var result = await executor.ExecuteAsync(retained, [1], OcrTestExecutionContext.Create(), CancellationToken.None);

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
