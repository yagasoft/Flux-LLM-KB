using System.Diagnostics;
using System.Reflection;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using FluxKnowledge.Application.Gpu;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Workers;

[Collection("native-worker-process")]
public sealed class DeterministicWorkerProcessTests
{
    [Theory]
    [InlineData("--source", "private.txt")]
    [InlineData("--model", "model-id")]
    [InlineData("--gpu", "0")]
    public async Task Worker_rejects_unsupported_source_model_and_gpu_arguments(string argument, string value)
    {
        using var process = StartWorkerWithRequiredArguments(argument, value);

        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.NotEqual(0, process.ExitCode);
    }

    [Fact]
    public async Task Worker_rejects_an_unsupported_protocol_version_before_connecting()
    {
        using var process = StartWorker(
            "--pipe",
            "unused-pipe",
            "--instance",
            "11111111-1111-1111-1111-111111111111",
            "--protocol-version",
            "v2");

        try
        {
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(1));
            Assert.NotEqual(0, process.ExitCode);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            }
        }
    }

    [Fact]
    public async Task Worker_sends_hello_without_creating_files_in_its_working_directory()
    {
        await using var harness = await WorkerPipeHarness.StartAsync();

        var hello = await harness.ReadFrameAsync();

        Assert.Equal(NativeWorkerFrameKind.Hello, hello.Kind);
        Assert.Equal(harness.ProtocolVersion, hello.ProtocolVersion);
        Assert.Equal(harness.InstanceId, hello.InstanceId);
        Assert.Empty(Directory.EnumerateFileSystemEntries(harness.WorkingDirectory));
    }

    [Fact]
    public void Worker_dependency_and_assembly_reference_closures_exclude_application_and_network_capability()
    {
        var workerDirectory = Path.GetDirectoryName(WorkerPipeHarness.WorkerAssemblyPath)
            ?? throw new InvalidOperationException("The worker assembly directory is required.");
        var dependencyDocument = JsonDocument.Parse(File.ReadAllText(Path.ChangeExtension(WorkerPipeHarness.WorkerAssemblyPath, ".deps.json")));
        var dependencyNames = dependencyDocument.RootElement
            .GetProperty("libraries")
            .EnumerateObject()
            .Select(value => value.Name)
            .ToArray();
        var shippedAssemblyReferences = dependencyNames
            .Where(value => value.StartsWith("FluxKnowledge.", StringComparison.Ordinal))
            .Select(value => value[..value.IndexOf('/')])
            .Select(value => Path.Combine(workerDirectory, $"{value}.dll"))
            .Select(Assembly.LoadFrom)
            .SelectMany(value => value.GetReferencedAssemblies())
            .Select(value => value.Name)
            .ToArray();

        Assert.DoesNotContain("FluxKnowledge.Application", shippedAssemblyReferences);
        Assert.DoesNotContain("FluxKnowledge.Domain", shippedAssemblyReferences);
        Assert.DoesNotContain(shippedAssemblyReferences, name => name is not null && (name.StartsWith("System.Net", StringComparison.Ordinal) || name.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal)));
        Assert.DoesNotContain(dependencyNames, name => name.StartsWith("FluxKnowledge.Application/", StringComparison.Ordinal) || name.StartsWith("FluxKnowledge.Domain/", StringComparison.Ordinal) || name.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal) || name.StartsWith("System.Net", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(NativeWorkerTestInstruction.AcknowledgeAndHold)]
    [InlineData(NativeWorkerTestInstruction.ReceiptAndComplete)]
    [InlineData(NativeWorkerTestInstruction.ExitBeforeAcknowledgement)]
    [InlineData(NativeWorkerTestInstruction.Unresponsive)]
    public async Task Worker_executes_each_bounded_test_instruction_through_closed_frames(
        NativeWorkerTestInstruction instruction)
    {
        await using var harness = await WorkerPipeHarness.StartAsync();
        var process = harness.Process;
        Assert.Equal(NativeWorkerFrameKind.Hello, (await harness.ReadFrameAsync()).Kind);
        await harness.SendWelcomeAsync();
        Assert.Equal(NativeWorkerFrameKind.Ready, (await harness.ReadFrameAsync()).Kind);
        await harness.SendAsync(new NativeWorkerFrame(
            NativeWorkerFrameKind.Dispatch,
            harness.ProtocolVersion,
            harness.InstanceId,
            harness.SessionNonce,
            CreateHandle()));
        await harness.SendAsync(new NativeWorkerFrame(
            NativeWorkerFrameKind.TestInstruction,
            harness.ProtocolVersion,
            harness.InstanceId,
            harness.SessionNonce,
            TestInstruction: instruction));

        switch (instruction)
        {
            case NativeWorkerTestInstruction.AcknowledgeAndHold:
                Assert.Equal(NativeWorkerFrameKind.Acknowledgement, (await harness.ReadFrameAsync()).Kind);
                await harness.SendAsync(new NativeWorkerFrame(
                    NativeWorkerFrameKind.StopRequested,
                    harness.ProtocolVersion,
                    harness.InstanceId,
                    harness.SessionNonce));
                Assert.Equal(NativeWorkerFrameKind.Stopped, (await harness.ReadFrameAsync()).Kind);
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                Assert.Equal(0, process.ExitCode);
                break;
            case NativeWorkerTestInstruction.ReceiptAndComplete:
                Assert.Equal(NativeWorkerFrameKind.Acknowledgement, (await harness.ReadFrameAsync()).Kind);
                var receipt = await harness.ReadFrameAsync();
                Assert.Equal(NativeWorkerFrameKind.Receipt, receipt.Kind);
                Assert.Equal(NativeWorkerTaskDisposition.Completed, receipt.Disposition);
                Assert.Equal(NativeWorkerFrameKind.Callback, (await harness.ReadFrameAsync()).Kind);
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                Assert.Equal(0, process.ExitCode);
                break;
            case NativeWorkerTestInstruction.ExitBeforeAcknowledgement:
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                Assert.NotEqual(0, process.ExitCode);
                break;
            case NativeWorkerTestInstruction.Unresponsive:
                await Task.Delay(TimeSpan.FromMilliseconds(250));
                Assert.False(process.HasExited);
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                break;
            default:
                throw new InvalidOperationException($"Unexpected instruction {instruction}.");
        }
    }

    [Fact]
    public async Task Worker_rejects_a_replayed_welcome_nonce()
    {
        await using var harness = await WorkerPipeHarness.StartAsync();
        var process = harness.Process;
        await harness.ReadFrameAsync();
        await harness.SendWelcomeAsync();
        Assert.Equal(NativeWorkerFrameKind.Ready, (await harness.ReadFrameAsync()).Kind);
        await harness.SendWelcomeAsync();

        Assert.Equal(NativeWorkerFrameKind.ProtocolRejected, (await harness.ReadFrameAsync()).Kind);
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.NotEqual(0, process.ExitCode);
    }

    [Fact]
    public async Task Worker_rejects_a_protocol_version_mismatch()
    {
        await using var harness = await WorkerPipeHarness.StartAsync();
        var process = harness.Process;
        await harness.ReadFrameAsync();
        await harness.SendRawAsync("{\"kind\":\"Welcome\",\"protocolVersion\":\"v2\",\"instanceId\":\"11111111-1111-1111-1111-111111111111\",\"sessionNonce\":\"22222222-2222-2222-2222-222222222222\"}", appendNewline: true);

        Assert.Equal(NativeWorkerFrameKind.ProtocolRejected, (await harness.ReadFrameAsync()).Kind);
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.NotEqual(0, process.ExitCode);
    }

    [Fact]
    public async Task Worker_accepts_an_exactly_16KiB_terminated_frame()
    {
        await using var harness = await WorkerPipeHarness.StartAsync();
        var process = harness.Process;
        await harness.ReadFrameAsync();
        await harness.SendWelcomeAsync();
        await harness.ReadFrameAsync();
        const string frame = "{\"kind\":\"StopRequested\",\"protocolVersion\":\"v1\",\"instanceId\":\"11111111-1111-1111-1111-111111111111\",\"sessionNonce\":\"22222222-2222-2222-2222-222222222222\"}";
        var paddedFrame = frame.PadRight(NativeWorkerFrameCodec.MaximumFrameBytes);

        await harness.SendRawAsync(paddedFrame, appendNewline: true);

        Assert.Equal(NativeWorkerFrameKind.Stopped, (await harness.ReadFrameAsync()).Kind);
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(0, process.ExitCode);
    }

    [Fact]
    public async Task Worker_rejects_an_over_limit_frame_before_processing_it()
    {
        await using var harness = await WorkerPipeHarness.StartAsync();
        var process = harness.Process;
        await harness.ReadFrameAsync();
        await harness.SendWelcomeAsync();
        await harness.ReadFrameAsync();

        await harness.SendRawAsync(new string(' ', NativeWorkerFrameCodec.MaximumFrameBytes + 1), appendNewline: false);

        Assert.Equal(NativeWorkerFrameKind.ProtocolRejected, (await harness.ReadFrameAsync()).Kind);
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.NotEqual(0, process.ExitCode);
    }

    [Fact]
    public async Task Worker_does_not_process_an_unterminated_frame()
    {
        await using var harness = await WorkerPipeHarness.StartAsync();
        var process = harness.Process;
        await harness.ReadFrameAsync();
        await harness.SendWelcomeAsync();
        await harness.ReadFrameAsync();
        const string frame = "{\"kind\":\"StopRequested\",\"protocolVersion\":\"v1\",\"instanceId\":\"11111111-1111-1111-1111-111111111111\",\"sessionNonce\":\"22222222-2222-2222-2222-222222222222\"}";

        await harness.SendRawAsync(frame[..^1], appendNewline: false);
        await Task.Delay(TimeSpan.FromMilliseconds(250));
        Assert.False(process.HasExited);
        await harness.SendRawAsync(frame[^1..], appendNewline: true);

        Assert.Equal(NativeWorkerFrameKind.Stopped, (await harness.ReadFrameAsync()).Kind);
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(0, process.ExitCode);
    }

    [Fact]
    public async Task Worker_reconnects_after_controlled_eof_with_same_identity_and_a_fresh_nonce()
    {
        const string protocolVersion = "v1";
        var instanceId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var firstNonce = Guid.Parse("55555555-5555-5555-5555-555555555555");
        var secondNonce = Guid.Parse("66666666-6666-6666-6666-666666666666");
        var pipeName = $"fluxknowledge-native-worker-reconnect-{Guid.NewGuid():N}";

        await using var firstPipe = CreateSequentialPipe(pipeName);
        using var process = StartWorker(
            "--pipe", pipeName,
            "--instance", instanceId.ToString(),
            "--protocol-version", protocolVersion);
        var expectedProcessId = process.Id;
        var expectedStartTime = process.StartTime.ToUniversalTime();

        await firstPipe.WaitForConnectionAsync().WaitAsync(TimeSpan.FromSeconds(10));
        var firstReader = new StreamReader(firstPipe, Encoding.UTF8, leaveOpen: true);
        var firstWriter = new StreamWriter(firstPipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        var firstHello = await ReadFrameAsync(firstReader);
        Assert.Equal(NativeWorkerFrameKind.Hello, firstHello.Kind);
        Assert.Equal(instanceId, firstHello.InstanceId);
        await WriteFrameAsync(firstWriter, new NativeWorkerFrame(NativeWorkerFrameKind.Welcome, protocolVersion, instanceId, firstNonce));
        var firstReady = await ReadFrameAsync(firstReader);
        Assert.Equal(NativeWorkerFrameKind.Ready, firstReady.Kind);
        Assert.Equal(firstNonce, firstReady.SessionNonce);

        await using var secondPipe = CreateSequentialPipe(pipeName);
        await firstWriter.DisposeAsync();
        firstReader.Dispose();
        await firstPipe.DisposeAsync();

        await WaitForConnectionOrWorkerExitAsync(secondPipe, process);
        using var secondReader = new StreamReader(secondPipe, Encoding.UTF8, leaveOpen: true);
        await using var secondWriter = new StreamWriter(secondPipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        var secondHello = await ReadFrameAsync(secondReader);
        Assert.Equal(NativeWorkerFrameKind.Hello, secondHello.Kind);
        Assert.Equal(instanceId, secondHello.InstanceId);
        Assert.False(process.HasExited);
        Assert.Equal(expectedProcessId, process.Id);
        Assert.Equal(expectedStartTime, process.StartTime.ToUniversalTime());

        await WriteFrameAsync(secondWriter, new NativeWorkerFrame(NativeWorkerFrameKind.Welcome, protocolVersion, instanceId, secondNonce));
        var secondReady = await ReadFrameAsync(secondReader);
        Assert.Equal(NativeWorkerFrameKind.Ready, secondReady.Kind);
        Assert.Equal(secondNonce, secondReady.SessionNonce);
        Assert.NotEqual(firstReady.SessionNonce, secondReady.SessionNonce);

        await WriteFrameAsync(secondWriter, new NativeWorkerFrame(NativeWorkerFrameKind.StopRequested, protocolVersion, instanceId, secondNonce));
        Assert.Equal(NativeWorkerFrameKind.Stopped, (await ReadFrameAsync(secondReader)).Kind);

        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(0, process.ExitCode);
    }

    private static NamedPipeServerStream CreateSequentialPipe(string pipeName) => new(
        pipeName,
        PipeDirection.InOut,
        NamedPipeServerStream.MaxAllowedServerInstances,
        PipeTransmissionMode.Byte,
        PipeOptions.Asynchronous);

    private static async Task WaitForConnectionOrWorkerExitAsync(
        NamedPipeServerStream pipe,
        Process process)
    {
        var connection = pipe.WaitForConnectionAsync();
        var exit = process.WaitForExitAsync();
        var completed = await Task.WhenAny(connection, exit, Task.Delay(TimeSpan.FromSeconds(10)));
        if (completed == connection)
        {
            await connection;
            return;
        }

        if (completed == exit)
        {
            await exit;
            var standardError = await process.StandardError.ReadToEndAsync();
            throw new InvalidOperationException($"The worker exited before reconnecting (exit code {process.ExitCode}): {standardError}");
        }

        throw new TimeoutException($"The worker remained alive but did not reconnect within 10 seconds (has exited: {process.HasExited}).");
    }

    private static async Task<NativeWorkerFrame> ReadFrameAsync(StreamReader reader) =>
        NativeWorkerFrameCodec.Deserialize(await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10))
            ?? throw new InvalidOperationException("The worker closed the pipe before sending a frame."));

    private static Task WriteFrameAsync(StreamWriter writer, NativeWorkerFrame frame) =>
        writer.WriteLineAsync(NativeWorkerFrameCodec.Serialize(frame));

    private static async Task DeleteWorkingDirectoryAsync(string path)
    {
        for (var attempt = 0; attempt < 4; attempt++)
        {
            try
            {
                Directory.Delete(path, recursive: true);
                return;
            }
            catch (IOException) when (attempt < 3)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(50));
            }
        }
    }

    private static GpuExecutorBatchHandle CreateHandle() => new(
        Guid.Parse("33333333-3333-3333-3333-333333333333"),
        "slot-a",
        "executor-a",
        1,
        Guid.Parse("44444444-4444-4444-4444-444444444444"));

    private static Process StartWorker(params string[] arguments)
    {
        var startInfo = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add(WorkerPipeHarness.WorkerAssemblyPath);
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return Process.Start(startInfo) ?? throw new InvalidOperationException("The deterministic worker did not start.");
    }

    private static Process StartWorkerWithRequiredArguments(string unsupportedArgument, string unsupportedValue) => StartWorker(
        "--pipe",
        "unused-pipe",
        "--instance",
        "11111111-1111-1111-1111-111111111111",
        "--protocol-version",
        "v1",
        unsupportedArgument,
        unsupportedValue);

    private sealed class WorkerPipeHarness : IAsyncDisposable
    {
        private readonly NamedPipeServerStream _pipe;
        private readonly StreamReader _reader;
        private readonly StreamWriter _writer;
        private readonly CancellationTokenSource _cleanup = new();

        private WorkerPipeHarness(
            NamedPipeServerStream pipe,
            StreamReader reader,
            StreamWriter writer,
            Process process,
            string workingDirectory)
        {
            _pipe = pipe;
            _reader = reader;
            _writer = writer;
            Process = process;
            WorkingDirectory = workingDirectory;
        }

        public string ProtocolVersion { get; } = "v1";

        public static string WorkerAssemblyPath => Path.Combine(
            AppContext.BaseDirectory,
            "deterministic-worker",
            "FluxKnowledge.DeterministicWorker.dll");

        public Guid InstanceId { get; } = Guid.Parse("11111111-1111-1111-1111-111111111111");

        public Guid SessionNonce { get; } = Guid.Parse("22222222-2222-2222-2222-222222222222");

        public Process Process { get; }

        public string WorkingDirectory { get; }

        public static async Task<WorkerPipeHarness> StartAsync()
        {
            var pipeName = $"fluxknowledge-native-worker-{Guid.NewGuid():N}";
            var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            var workingDirectory = Path.Combine(Path.GetTempPath(), $"fluxknowledge-native-worker-{Guid.NewGuid():N}");
            Directory.CreateDirectory(workingDirectory);
            var startInfo = new ProcessStartInfo("dotnet")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = workingDirectory
            };
            startInfo.ArgumentList.Add(WorkerAssemblyPath);
            startInfo.ArgumentList.Add("--pipe");
            startInfo.ArgumentList.Add(pipeName);
            startInfo.ArgumentList.Add("--instance");
            startInfo.ArgumentList.Add("11111111-1111-1111-1111-111111111111");
            startInfo.ArgumentList.Add("--protocol-version");
            startInfo.ArgumentList.Add("v1");
            var process = Process.Start(startInfo) ?? throw new InvalidOperationException("The deterministic worker did not start.");
            await pipe.WaitForConnectionAsync().WaitAsync(TimeSpan.FromSeconds(10));
            var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
            var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
            return new WorkerPipeHarness(pipe, reader, writer, process, workingDirectory);
        }

        public async Task<NativeWorkerFrame> ReadFrameAsync()
        {
            var line = await _reader.ReadLineAsync(_cleanup.Token).AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            return NativeWorkerFrameCodec.Deserialize(line ?? throw new InvalidOperationException("The worker closed the pipe before sending a frame."));
        }

        public Task SendWelcomeAsync() => SendAsync(new NativeWorkerFrame(
            NativeWorkerFrameKind.Welcome,
            ProtocolVersion,
            InstanceId,
            SessionNonce));

        public Task SendAsync(NativeWorkerFrame frame) => _writer.WriteLineAsync(NativeWorkerFrameCodec.Serialize(frame));

        public async Task SendRawAsync(string frame, bool appendNewline)
        {
            await _writer.WriteAsync(frame);
            if (appendNewline)
            {
                await _writer.WriteAsync("\n");
            }

            await _writer.FlushAsync();
        }

        public async ValueTask DisposeAsync()
        {
            _cleanup.Cancel();
            if (!Process.HasExited)
            {
                Process.Kill(entireProcessTree: true);
                await Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            }

            _reader.Dispose();
            _writer.Dispose();
            await _pipe.DisposeAsync();
            _cleanup.Dispose();
            await DeleteWorkingDirectoryAsync(WorkingDirectory);
        }
    }
}
