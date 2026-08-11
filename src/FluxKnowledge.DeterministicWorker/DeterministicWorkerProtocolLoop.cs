using System.IO.Pipes;
using System.Text;
using FluxKnowledge.Application.Gpu;

namespace FluxKnowledge.DeterministicWorker;

internal static class DeterministicWorkerProtocolLoop
{
    private const int ProtocolRejectedExitCode = 12;
    private const int ExitBeforeAcknowledgementExitCode = 23;
    private const string PostReadyReadEventVariable = "FLUX_NATIVE_WORKER_TEST_POST_READY_READ_EVENT";

    public static async Task<int> RunAsync(string[] args, CancellationToken cancellationToken)
    {
        if (!TryParseArguments(args, out var options))
        {
            return 2;
        }

        try
        {
            GpuExecutorBatchHandle? retainedHandle = null;
            while (!cancellationToken.IsCancellationRequested)
            {
                await using var pipe = new NamedPipeClientStream(".", options.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                try
                {
                    await pipe.ConnectAsync(cancellationToken);
                }
                catch (IOException) when (!cancellationToken.IsCancellationRequested)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(25), cancellationToken);
                    continue;
                }
                using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
                var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };

                try
                {
                    await SendAsync(writer, new NativeWorkerFrame(NativeWorkerFrameKind.Hello, options.ProtocolVersion, options.InstanceId), cancellationToken);
                    NativeWorkerFrame welcome;
                    try { welcome = await ReadAsync(reader, cancellationToken); }
                    catch (EndOfStreamException) { continue; }
                    catch (NativeWorkerProtocolException) { await SendRejectedAsync(writer, options, null, cancellationToken); return ProtocolRejectedExitCode; }

                    if (welcome.Kind != NativeWorkerFrameKind.Welcome || welcome.SessionNonce is null) { await SendRejectedAsync(writer, options, null, cancellationToken); return ProtocolRejectedExitCode; }

                    try { welcome.ValidateFor(options.ProtocolVersion, options.InstanceId, null); }
                    catch (ArgumentException) { await SendRejectedAsync(writer, options, null, cancellationToken); return ProtocolRejectedExitCode; }

                    var nonce = welcome.SessionNonce.Value;
                    await SendAsync(writer, new NativeWorkerFrame(NativeWorkerFrameKind.Ready, options.ProtocolVersion, options.InstanceId, nonce), cancellationToken);
                    var result = await ProcessFramesAsync(reader, writer, options, nonce, retainedHandle, cancellationToken);
                    retainedHandle = result.Handle;
                    if (!result.Reconnect) return result.ExitCode;
                }
                finally
                {
                    try
                    {
                        await writer.DisposeAsync().ConfigureAwait(false);
                    }
                    catch (IOException) when (!cancellationToken.IsCancellationRequested)
                    {
                    }
                }
            }
            return 0;
        }
        catch (NativeWorkerProtocolException)
        {
            return ProtocolRejectedExitCode;
        }
    }

    private static async Task<WorkerLoopResult> ProcessFramesAsync(
        StreamReader reader,
        StreamWriter writer,
        WorkerOptions options,
        Guid nonce, GpuExecutorBatchHandle? handle,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            NativeWorkerFrame frame;
            try
            {
                var nextFrame = ReadAsync(reader, cancellationToken);
                SignalPostReadyReadForControlledTest();
                frame = await nextFrame;
                frame.ValidateFor(options.ProtocolVersion, options.InstanceId, nonce);
            }
            catch (EndOfStreamException)
            {
                return new WorkerLoopResult(0, handle, true);
            }
            catch (IOException) when (!cancellationToken.IsCancellationRequested)
            {
                return new WorkerLoopResult(0, handle, true);
            }
            catch (NativeWorkerProtocolException)
            {
                await SendRejectedAsync(writer, options, nonce, cancellationToken);
                return new WorkerLoopResult(ProtocolRejectedExitCode, handle, false);
            }
            catch (ArgumentException)
            {
                await SendRejectedAsync(writer, options, nonce, cancellationToken);
                return new WorkerLoopResult(ProtocolRejectedExitCode, handle, false);
            }

            switch (frame.Kind)
            {
                case NativeWorkerFrameKind.Dispatch:
                    handle = frame.Handle;
                    break;
                case NativeWorkerFrameKind.TestInstruction when handle is not null:
                    switch (frame.TestInstruction)
                    {
                        case NativeWorkerTestInstruction.AcknowledgeAndHold:
                            await SendAsync(writer, new NativeWorkerFrame(NativeWorkerFrameKind.Acknowledgement, options.ProtocolVersion, options.InstanceId, nonce, handle), cancellationToken);
                            break;
                        case NativeWorkerTestInstruction.ReceiptAndComplete:
                            await SendAsync(writer, new NativeWorkerFrame(NativeWorkerFrameKind.Acknowledgement, options.ProtocolVersion, options.InstanceId, nonce, handle), cancellationToken);
                            await SendAsync(writer, new NativeWorkerFrame(NativeWorkerFrameKind.Receipt, options.ProtocolVersion, options.InstanceId, nonce, handle, Disposition: NativeWorkerTaskDisposition.Completed), cancellationToken);
                            await SendAsync(writer, new NativeWorkerFrame(NativeWorkerFrameKind.Callback, options.ProtocolVersion, options.InstanceId, nonce), cancellationToken);
                            return new WorkerLoopResult(0, handle, false);
                        case NativeWorkerTestInstruction.ExitBeforeAcknowledgement:
                            return new WorkerLoopResult(ExitBeforeAcknowledgementExitCode, handle, false);
                        case NativeWorkerTestInstruction.Unresponsive:
                            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                            return new WorkerLoopResult(0, handle, false);
                        default:
                            await SendRejectedAsync(writer, options, nonce, cancellationToken);
                            return new WorkerLoopResult(ProtocolRejectedExitCode, handle, false);
                    }

                    break;
                case NativeWorkerFrameKind.StopRequested when handle is null:
                case NativeWorkerFrameKind.StopRequested when handle is not null:
                    await SendAsync(writer, new NativeWorkerFrame(NativeWorkerFrameKind.Stopped, options.ProtocolVersion, options.InstanceId, nonce), cancellationToken);
                    return new WorkerLoopResult(0, handle, false);
                default:
                    await SendRejectedAsync(writer, options, nonce, cancellationToken);
                    return new WorkerLoopResult(ProtocolRejectedExitCode, handle, false);
            }
        }
    }

    private static async Task<NativeWorkerFrame> ReadAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        var builder = new StringBuilder();
        var buffer = new char[1];
        while (true)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken);
            if (read == 0)
            {
                throw new EndOfStreamException();
            }

            if (buffer[0] == '\n')
            {
                break;
            }

            builder.Append(buffer[0]);
            if (Encoding.UTF8.GetByteCount(builder.ToString()) > NativeWorkerFrameCodec.MaximumFrameBytes)
            {
                throw new NativeWorkerProtocolException();
            }
        }

        try
        {
            return NativeWorkerFrameCodec.Deserialize(builder.ToString());
        }
        catch (System.Text.Json.JsonException exception)
        {
            throw new NativeWorkerProtocolException(exception);
        }
    }

    private static Task SendAsync(StreamWriter writer, NativeWorkerFrame frame, CancellationToken cancellationToken) =>
        writer.WriteLineAsync(NativeWorkerFrameCodec.Serialize(frame).AsMemory(), cancellationToken);

    private static Task SendRejectedAsync(StreamWriter writer, WorkerOptions options, Guid? nonce, CancellationToken cancellationToken) =>
        SendAsync(writer, new NativeWorkerFrame(NativeWorkerFrameKind.ProtocolRejected, options.ProtocolVersion, options.InstanceId, nonce), cancellationToken);

    private static void SignalPostReadyReadForControlledTest()
    {
        var eventName = Environment.GetEnvironmentVariable(PostReadyReadEventVariable);
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(eventName)) return;
        try
        {
            using var signal = EventWaitHandle.OpenExisting(eventName);
            signal.Set();
        }
        catch (WaitHandleCannotBeOpenedException)
        {
        }
    }

    private static bool TryParseArguments(string[] args, out WorkerOptions options)
    {
        options = default;
        if (args.Length != 6)
        {
            return false;
        }

        string? pipeName = null;
        string? instanceText = null;
        string? protocolVersion = null;
        for (var index = 0; index < args.Length; index += 2)
        {
            var name = args[index];
            var value = args[index + 1];
            if (string.IsNullOrWhiteSpace(value) || (name == "--pipe" && pipeName is not null) || (name == "--instance" && instanceText is not null) || (name == "--protocol-version" && protocolVersion is not null))
            {
                return false;
            }

            switch (name)
            {
                case "--pipe":
                    pipeName = value;
                    break;
                case "--instance":
                    instanceText = value;
                    break;
                case "--protocol-version":
                    protocolVersion = value;
                    break;
                default:
                    return false;
            }
        }

        if (pipeName is null || instanceText is null || protocolVersion is null || !Guid.TryParse(instanceText, out var instanceId) || instanceId == Guid.Empty)
        {
            return false;
        }

        try
        {
            NativeWorkerProtocol.RequireVersion(protocolVersion, nameof(protocolVersion));
        }
        catch (ArgumentException)
        {
            return false;
        }

        options = new WorkerOptions(pipeName, instanceId, protocolVersion);
        return true;
    }

    private readonly record struct WorkerOptions(string PipeName, Guid InstanceId, string ProtocolVersion);
    private readonly record struct WorkerLoopResult(int ExitCode, GpuExecutorBatchHandle? Handle, bool Reconnect);

    private sealed class NativeWorkerProtocolException(Exception? innerException = null) : Exception("Native worker protocol rejected.", innerException);
}
