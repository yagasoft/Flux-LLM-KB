using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using FluxKnowledge.Application.Documents;
using FluxKnowledge.Application.Operations;
using FluxKnowledge.Application.Pipeline;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Integrations.Documents;
using FluxKnowledge.Integrations.Media;
using Xunit;
using Xunit.Abstractions;

namespace FluxKnowledge.Integration.Tests.Documents;

public sealed class MediaTranscriptionLocalWarmExecutorTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _root = Path.Combine(AppContext.BaseDirectory, $"media-warm-{Guid.NewGuid():N}");
    private static readonly byte[] Input = [1, 2, 3];
    private static readonly string InputHash = Convert.ToHexStringLower(SHA256.HashData(Input));
    private static readonly SourceRevisionId Source = new(Guid.NewGuid());

    [Theory]
    [InlineData("general", "transcription-complete", true, false, false)]
    [InlineData("egyptian-ar-en", "transcription-complete", true, false, false)]
    [InlineData("wrong-model", "transcription-output-invalid", false, true, false)]
    [InlineData("wrong-id", "transcription-worker-interrupted", false, true, false)]
    [InlineData("no-speech", "transcription-no-speech-found", false, true, false)]
    [InlineData("no-permission", "transcription-native-start-refused", false, false, true)]
    public async Task Warm_wrapper_binds_the_claim_result_and_matching_native_phase(
        string mode, string reason, bool expectIdle, bool expectRelease, bool expectUnsent)
    {
        var layout = CreateLayout();
        var executor = new MediaTranscriptionLocalExecutor(layout, new RetainedReader());
        var execution = CreateExecution();
        var residentId = Guid.NewGuid();
        await using var process = OwnedWarmModelProcess.Start(Child(mode), residentId,
            execution.Lease.Supervisor.Process.MachineFingerprint);
        using var job = OwnedOcrJob.TryOpen(residentId);
        Assert.NotNull(job);
        var directory = Path.Combine(layout.TempRoot, "media-transcription", execution.Lease.ExecutionInstanceId.ToString("N"));
        OwnedWarmModelCommand? permitted = null;

        var outcome = await executor.RunWarmAsync(Source, InputHash, execution, process, (lease, command, _) =>
        {
            Assert.Equal(execution.Lease, lease);
            Assert.Equal(residentId, command.ResidentId);
            Assert.Equal(lease.ExecutionInstanceId, command.ExecutionId);
            Assert.Equal(Input, File.ReadAllBytes(Path.Combine(directory, "input.media")));
            Assert.True(File.Exists(Path.Combine(directory, "runtime.json")));
            Assert.False(File.Exists(Path.Combine(directory, "output.json")));
            permitted = command;
            return ValueTask.FromResult(mode != "no-permission");
        }, default);

        Assert.NotNull(permitted);
        Assert.Equal(reason, outcome.Result.ReasonCode);
        Assert.Equal(expectIdle, outcome.Result.Succeeded);
        Assert.Equal(expectIdle, outcome.Idle is not null);
        Assert.Equal(expectRelease, outcome.NativeReleaseConfirmed);
        Assert.Equal(expectUnsent, outcome.CommandUnsent);
        Assert.Equal(expectRelease, job.ActiveProcesses == 0);
        if (outcome.Idle is { } idle)
        {
            Assert.Equal(mode, idle.Model);
            Assert.Equal(permitted.Process, idle.Process);
            Assert.Equal(permitted.ExecutionId, idle.ExecutionId);
            Assert.Equal("[00:00:00.100] hello", outcome.Result.Transcript);
            Assert.Equal(outcome.Result, await executor.ReadRecoveredResultAsync(idle.ExecutionId, default));
            Assert.Equal(["output.json"], Directory.GetFileSystemEntries(directory).Select(Path.GetFileName));
            executor.RemoveSettledFiles(idle.ExecutionId);
        }
        Assert.False(Directory.Exists(directory));
    }

    [Fact]
    public async Task Warm_wrapper_never_authorizes_a_source_with_a_different_hash()
    {
        var layout = CreateLayout();
        var executor = new MediaTranscriptionLocalExecutor(layout, new RetainedReader());
        var execution = CreateExecution();
        await using var process = OwnedWarmModelProcess.Start(Child("general"), Guid.NewGuid(),
            execution.Lease.Supervisor.Process.MachineFingerprint);

        var outcome = await executor.RunWarmAsync(Source, new string('0', 64), execution, process,
            (_, _, _) => throw new InvalidOperationException("unexpected native start"), default);

        Assert.Equal("transcription-input-binding-invalid", outcome.Result.ReasonCode);
        Assert.Null(outcome.Idle);
        Assert.False(outcome.NativeReleaseConfirmed);
        Assert.True(outcome.CommandUnsent);
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(layout.TempRoot, "media-transcription")));
    }

    [Fact]
    public async Task Fresh_claim_reuses_the_contained_child_after_previous_files_are_removed()
    {
        var layout = CreateLayout();
        var executor = new MediaTranscriptionLocalExecutor(layout, new RetainedReader());
        var first = CreateExecution();
        var second = CreateExecution();
        var residentId = Guid.NewGuid();
        await using var process = OwnedWarmModelProcess.Start(Child("general"), residentId,
            first.Lease.Supervisor.Process.MachineFingerprint);
        var requests = new List<Guid>();

        var before = await RunAsync(first);
        Assert.NotNull(before.Idle);
        executor.RemoveSettledFiles(first.Lease.ExecutionInstanceId);
        var after = await RunAsync(second);
        Assert.NotNull(after.Idle);
        Assert.Equal(before.Idle.Process, after.Idle.Process);
        Assert.Equal(residentId, after.Idle.ResidentId);
        Assert.Equal([first.Lease.ExecutionInstanceId, second.Lease.ExecutionInstanceId], requests);
        Assert.False(Directory.Exists(Path.Combine(layout.TempRoot, "media-transcription", before.Idle.ExecutionId.ToString("N"))));

        ValueTask<MediaTranscriptionWarmExecutionResult> RunAsync(DocumentOcrExecutionContext execution) =>
            executor.RunWarmAsync(Source, InputHash, execution, process, (_, command, _) =>
            {
                requests.Add(command.ExecutionId);
                return ValueTask.FromResult(true);
            }, default);
    }

    [MediaWarmNativeTrialFact]
    public async Task Native_synthetic_trial_reuses_the_pinned_model_and_confirms_windows_job_exit()
    {
        var layout = CreateLayout();
        var wav = Path.Combine(_root, "synthetic.wav");
        await CreateSpeechAsync(wav);
        var bytes = await File.ReadAllBytesAsync(wav);
        Assert.InRange(bytes.Length, 128, 2 * 1024 * 1024);
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var executor = new MediaTranscriptionLocalExecutor(layout, new RetainedReader(bytes));
        var first = CreateExecution(TimeSpan.FromSeconds(90));
        var residentId = Guid.NewGuid();

        var baseline = await ReadGpuMemoryAsync();
        Assert.True((long)baseline.FreeMib * 1024 * 1024 >= MediaTranscriptionLocalRuntime.Profile.EstimatedBytes,
            $"Insufficient GPU headroom for the native trial. Free VRAM is {baseline.FreeMib} MiB.");
        await using var process = await executor.StartWarmProcessAsync(residentId,
            first.Lease.Supervisor.Process.MachineFingerprint, default);
        using var job = OwnedOcrJob.TryOpen(residentId);
        Assert.NotNull(job);
        var (initial, initialMs) = await RunAsync(first);
        var idleMemory = await ReadGpuMemoryAsync();
        var firstIdleProcesses = job.ActiveProcesses;
        Assert.True(firstIdleProcesses > 0);
        executor.RemoveSettledFiles(first.Lease.ExecutionInstanceId);
        var (subsequent, subsequentMs) = await RunAsync(CreateExecution(TimeSpan.FromSeconds(90)));
        var reusedMemory = await ReadGpuMemoryAsync();
        var reusedIdleProcesses = job.ActiveProcesses;
        Assert.True(reusedIdleProcesses > 0);
        Assert.Equal(initial.Idle!.Process, subsequent.Idle!.Process);
        Assert.NotEqual(initial.Idle.ExecutionId, subsequent.Idle.ExecutionId);
        Assert.Equal(initial.Result.Transcript, subsequent.Result.Transcript);

        using var stopBudget = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        Assert.True(await process.StopAndConfirmAsync(stopBudget.Token));
        Assert.Equal(0u, job.ActiveProcesses);
        await Task.Delay(TimeSpan.FromMilliseconds(750));
        var released = await ReadGpuMemoryAsync();
        output.WriteLine($"Media request times in milliseconds. First {initialMs}, warm {subsequentMs}.");
        output.WriteLine($"Device VRAM in MiB. Baseline {baseline.UsedMib}, first idle {idleMemory.UsedMib}, warmed idle {reusedMemory.UsedMib}, after Job empty {released.UsedMib}.");
        output.WriteLine($"The same worker PID served both requests. Windows Job process counts were {firstIdleProcesses} at first idle, {reusedIdleProcesses} at warm idle and {job.ActiveProcesses} after stop.");

        async Task<(MediaTranscriptionWarmExecutionResult Result, long Elapsed)> RunAsync(DocumentOcrExecutionContext context)
        {
            using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            var timer = Stopwatch.StartNew();
            var result = await executor.RunWarmAsync(Source, hash, context, process,
                (_, _, _) => ValueTask.FromResult(true), budget.Token);
            Assert.True(result.Result.Succeeded, result.Result.ReasonCode);
            Assert.NotNull(result.Idle);
            Assert.Equal("general", result.Idle.Model);
            Assert.Contains("synthetic", result.Result.Transcript!, StringComparison.OrdinalIgnoreCase);
            return (result, timer.ElapsedMilliseconds);
        }
    }

    private static async Task CreateSpeechAsync(string destination)
    {
        const string script = """
            Add-Type -AssemblyName System.Speech
            $speaker = New-Object System.Speech.Synthesis.SpeechSynthesizer
            try {
                $speaker.SelectVoiceByHints([System.Speech.Synthesis.VoiceGender]::NotSet,
                    [System.Speech.Synthesis.VoiceAge]::NotSet, 0, [Globalization.CultureInfo]::GetCultureInfo('en-US'))
                $speaker.SetOutputToWaveFile($env:FLUX_KB_SYNTHETIC_MEDIA_WAVE)
                $speaker.Speak('This is a local synthetic timing test. The first and second recording contain exactly the same words. The model transcribes English speech offline.')
            } finally { $speaker.Dispose() }
            """;
        var start = new ProcessStartInfo("powershell.exe");
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-EncodedCommand",
                     Convert.ToBase64String(Encoding.Unicode.GetBytes(script)) })
            start.ArgumentList.Add(argument);
        start.Environment["FLUX_KB_SYNTHETIC_MEDIA_WAVE"] = destination;
        _ = await RunBoundedToolAsync(start);
    }

    private static async Task<(int UsedMib, int FreeMib)> ReadGpuMemoryAsync()
    {
        var start = new ProcessStartInfo("nvidia-smi.exe");
        foreach (var argument in new[] { "--id=0", "--query-gpu=memory.used,memory.free", "--format=csv,noheader,nounits" })
            start.ArgumentList.Add(argument);
        var values = (await RunBoundedToolAsync(start)).Trim().Split(',');
        Assert.Equal(2, values.Length);
        return (int.Parse(values[0], CultureInfo.InvariantCulture), int.Parse(values[1], CultureInfo.InvariantCulture));
    }

    private static async Task<string> RunBoundedToolAsync(ProcessStartInfo start)
    {
        start.UseShellExecute = false;
        start.CreateNoWindow = true;
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        using var child = Process.Start(start)!;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var stdout = child.StandardOutput.ReadToEndAsync(deadline.Token);
        var stderr = child.StandardError.ReadToEndAsync(deadline.Token);
        try { await child.WaitForExitAsync(deadline.Token); }
        catch (OperationCanceledException)
        {
            if (!child.HasExited) child.Kill(entireProcessTree: true);
            throw;
        }
        Assert.True(child.ExitCode == 0, await stderr);
        return await stdout;
    }

    private sealed class MediaWarmNativeTrialFactAttribute : FactAttribute
    {
        public MediaWarmNativeTrialFactAttribute()
        {
            if (!OperatingSystem.IsWindows() || Environment.GetEnvironmentVariable("FLUX_KB_RUN_MEDIA_WARM_NATIVE_TRIAL") != "1")
                Skip = "Explicit opt-in required for the bounded native GPU trial.";
        }
    }

    private LiveRootLayout CreateLayout()
    {
        var layout = LiveRootLayout.CreateForIsolatedTests(_root);
        Directory.CreateDirectory(layout.TempRoot);
        return layout;
    }

    private static DocumentOcrExecutionContext CreateExecution(TimeSpan? duration = null)
    {
        var context = OcrTestExecutionContext.Create(duration ?? TimeSpan.FromSeconds(20));
        return context with { Lease = context.Lease with
        {
            Handle = context.Lease.Handle with { ExecutorKey = MediaTranscriptionLocalRuntime.Profile.ExecutorKey }
        } };
    }

    private static ProcessStartInfo Child(string mode)
    {
        var script = """
            $mode = '__MODE__'
            while ($frame = [Console]::ReadLine()) {
                $request = ConvertFrom-Json -InputObject $frame
                if ($request.operation -eq 'shutdown') { exit 0 }
                $manifest = Get-Content -LiteralPath $request.manifest -Raw | ConvertFrom-Json
                if (-not $manifest.generalModel -or -not $manifest.egyptianModel) { exit 96 }
                $inputPath = Join-Path (Split-Path -Parent $request.manifest) 'input.media'
                if ([Convert]::ToBase64String([IO.File]::ReadAllBytes($inputPath)) -ne 'AQID') { exit 95 }
                $profile = if ($mode -eq 'egyptian-ar-en') { 'egyptian-ar-en' } else { 'general' }
                $ackModel = if ($mode -eq 'wrong-model') { 'egyptian-ar-en' } else { $profile }
                $id = if ($mode -eq 'wrong-id') { '00000000000000000000000000000000' } else { $request.execution_id }
                $language = if ($profile -eq 'egyptian-ar-en') { 'ar' } else { 'en' }
                $reason = if ($profile -eq 'egyptian-ar-en') { 'arabic-speech' } else { 'other-language' }
                $result = @{succeeded=$true;result=@{profile=$profile;selectionReason=$reason;language=$language;
                    languageConfidence=0.98;durationMillis=1000; languageEvidence=@(@{language=$language; confidence=0.98});
                    cues=@(@{startMillis=100;endMillis=900;text='hello'})}}
                if ($mode -eq 'no-speech') {
                    $ackModel = $null
                    $result = @{succeeded=$false;reasonCode='transcription-no-speech-found'}
                }
                $payload = [Text.Encoding]::UTF8.GetBytes(($result | ConvertTo-Json -Compress -Depth 6))
                $output = [IO.File]::Open($request.output,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write)
                try { $output.Write($payload, 0, $payload.Length); $output.Flush($true) }
                finally { $output.Dispose() }
                [Console]::WriteLine((@{execution_id=$id;status='idle';model=$ackModel} | ConvertTo-Json -Compress))
            }
            """.Replace("__MODE__", mode);
        var start = new ProcessStartInfo("powershell.exe");
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-EncodedCommand",
                     Convert.ToBase64String(Encoding.Unicode.GetBytes(script)) })
            start.ArgumentList.Add(argument);
        return start;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private sealed class RetainedReader(byte[]? media = null) : IRetainedSourceReader
    {
        public ValueTask<RetainedSourceBytes> ReadBytesAsync(SourceRevisionId sourceRevisionId, CancellationToken cancellationToken)
        {
            var input = media ?? Input;
            return ValueTask.FromResult(new RetainedSourceBytes(Source, input,
                Convert.ToHexStringLower(SHA256.HashData(input)), input.Length));
        }

        public ValueTask<Utf8FileSource> ReadUtf8Async(SourceRevisionId sourceRevisionId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
