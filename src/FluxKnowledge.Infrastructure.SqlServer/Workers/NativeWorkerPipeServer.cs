using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using FluxKnowledge.Application.Gpu;

namespace FluxKnowledge.Infrastructure.SqlServer.Workers;

/// <summary>
/// A one-connection, current-user pipe that authenticates an app-launched child before a
/// transient session nonce is issued. Pipe names and nonces intentionally never leave this type.
/// </summary>
public sealed class NativeWorkerPipeServer : IAsyncDisposable
{
    private readonly NamedPipeServerStream _pipe;
    private readonly INativeWorkerClientProcessIdentityReader _clientIdentityReader;

    public NativeWorkerPipeServer(string pipeName, INativeWorkerClientProcessIdentityReader? clientIdentityReader = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
        PipeName = pipeName;
        _clientIdentityReader = clientIdentityReader ?? new WindowsNativeWorkerClientProcessIdentityReader();
        _pipe = new NamedPipeServerStream(
            pipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
    }

    internal string PipeName { get; }

    public async Task<NativeWorkerPipeSession?> AcceptAsync(
        NativeWorkerInstanceHandle expectedInstance,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(expectedInstance);
        expectedInstance.Validate();
        await _pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
        var reader = new StreamReader(_pipe, new UTF8Encoding(false), leaveOpen: true);
        var writer = new StreamWriter(_pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        NativeWorkerFrame hello;
        try
        {
            hello = await ReadFrameAsync(reader, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is ArgumentException or System.Text.Json.JsonException or EndOfStreamException)
        {
            return null;
        }

        if (hello.Kind != NativeWorkerFrameKind.Hello ||
            !string.Equals(hello.ProtocolVersion, expectedInstance.ProtocolVersion, StringComparison.Ordinal) ||
            hello.InstanceId != expectedInstance.InstanceId)
        {
            return null;
        }

        var clientProcessId = _clientIdentityReader.GetClientProcessId(_pipe.SafePipeHandle);
        if (clientProcessId != expectedInstance.ProcessId || !MatchesStartTime(clientProcessId, expectedInstance.ProcessStartedAtUtc))
        {
            return null;
        }

        var nonce = Guid.NewGuid();
        await WriteFrameAsync(writer, new NativeWorkerFrame(
            NativeWorkerFrameKind.Welcome,
            expectedInstance.ProtocolVersion,
            expectedInstance.InstanceId,
            nonce), cancellationToken).ConfigureAwait(false);
        return new NativeWorkerPipeSession(_pipe, reader, writer, expectedInstance, nonce);
    }

    public ValueTask DisposeAsync() => _pipe.DisposeAsync();

    private static bool MatchesStartTime(uint processId, DateTimeOffset expectedStartedAtUtc)
    {
        try
        {
            using var process = Process.GetProcessById(checked((int)processId));
            var actualStartedAtUtc = new DateTimeOffset(process.StartTime.ToUniversalTime());
            return actualStartedAtUtc == expectedStartedAtUtc;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    internal static async Task<NativeWorkerFrame> ReadFrameAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        var builder = new StringBuilder();
        var character = new char[1];
        while (true)
        {
            if (await reader.ReadAsync(character.AsMemory(), cancellationToken).ConfigureAwait(false) == 0)
            {
                throw new EndOfStreamException();
            }

            if (character[0] == '\n')
            {
                return NativeWorkerFrameCodec.Deserialize(builder.ToString());
            }

            builder.Append(character[0]);
            if (Encoding.UTF8.GetByteCount(builder.ToString()) > NativeWorkerFrameCodec.MaximumFrameBytes)
            {
                throw new System.Text.Json.JsonException("A native worker frame exceeds the maximum size.");
            }
        }
    }

    internal static Task WriteFrameAsync(StreamWriter writer, NativeWorkerFrame frame, CancellationToken cancellationToken) =>
        writer.WriteLineAsync(NativeWorkerFrameCodec.Serialize(frame).AsMemory(), cancellationToken);
}

public interface INativeWorkerClientProcessIdentityReader
{
    uint GetClientProcessId(SafePipeHandle pipeHandle);
}

internal sealed class WindowsNativeWorkerClientProcessIdentityReader : INativeWorkerClientProcessIdentityReader
{
    public uint GetClientProcessId(SafePipeHandle pipeHandle)
    {
        ArgumentNullException.ThrowIfNull(pipeHandle);
        if (!OperatingSystem.IsWindows() || !GetNamedPipeClientProcessId(pipeHandle, out var processId))
        {
            throw new InvalidOperationException("The native worker pipe client identity could not be attested.");
        }

        return processId;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint clientProcessId);
}

public sealed class NativeWorkerPipeSession(
    NamedPipeServerStream pipe,
    StreamReader reader,
    StreamWriter writer,
    NativeWorkerInstanceHandle instance,
    Guid sessionNonce) : IAsyncDisposable
{
    private readonly CancellationTokenSource _disposed = new();
    private readonly TaskCompletionSource _disposedObserved = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object _disposeSync = new();
    private Task? _disposeTask;

    public NativeWorkerInstanceHandle Instance { get; } = instance;
    internal Guid SessionNonce { get; } = sessionNonce;
    internal Task Disposed => _disposedObserved.Task;

    public async Task<NativeWorkerFrame> ReadAsync(CancellationToken cancellationToken)
    {
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _disposed.Token);
        NativeWorkerFrame frame;
        try
        {
            frame = await NativeWorkerPipeServer.ReadFrameAsync(reader, linkedCancellation.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (_disposed.IsCancellationRequested && exception is OperationCanceledException or EndOfStreamException)
        {
            throw new IOException("The native worker pipe session was disposed before its pending read completed.", exception);
        }
        frame.ValidateFor(Instance.ProtocolVersion, Instance.InstanceId, SessionNonce);
        return frame;
    }

    public Task WriteAsync(NativeWorkerFrame frame, CancellationToken cancellationToken)
    {
        frame.ValidateFor(Instance.ProtocolVersion, Instance.InstanceId, SessionNonce);
        return NativeWorkerPipeServer.WriteFrameAsync(writer, frame, cancellationToken);
    }

    public ValueTask DisposeAsync()
    {
        lock (_disposeSync)
        {
            _disposeTask ??= DisposeCoreAsync();
            return new ValueTask(_disposeTask);
        }
    }

    private async Task DisposeCoreAsync()
    {
        _disposed.Cancel();
        _disposedObserved.TrySetResult();
        writer.Dispose();
        reader.Dispose();
        await pipe.DisposeAsync().ConfigureAwait(false);
    }
}
