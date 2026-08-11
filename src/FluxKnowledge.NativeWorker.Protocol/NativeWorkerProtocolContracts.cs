using System.Text.Json;
using System.Text.Json.Serialization;

namespace FluxKnowledge.Application.Gpu;

/// <summary>
/// Guards opaque scheduler keys whose exact SQL identity must not be altered by string padding.
/// </summary>
public static class GpuSchedulerOpaqueKeyValidator
{
    public const int MaximumExecutorFenceKeyLength = 256;

    public static void RequireCanonical(string? value, string parameterName, int? maximumLength = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (char.IsWhiteSpace(value[^1]))
        {
            throw new ArgumentException("Scheduler opaque keys cannot end with whitespace.", parameterName);
        }

        if (maximumLength is not null && value.Length > maximumLength.Value)
        {
            throw new ArgumentException($"Scheduler opaque keys cannot exceed {maximumLength.Value} characters.", parameterName);
        }
    }
}

/// <summary>
/// Opaque, immutable fence for one durably admitted executor dispatch.
/// </summary>
public sealed record GpuExecutorBatchHandle(
    Guid BatchId,
    string CapacitySlotKey,
    string ExecutorKey,
    long AdmissionGeneration,
    Guid DispatchId)
{
    public void Validate()
    {
        if (BatchId == Guid.Empty)
        {
            throw new ArgumentException("An executor handle requires a batch ID.", nameof(BatchId));
        }

        GpuSchedulerOpaqueKeyValidator.RequireCanonical(CapacitySlotKey, nameof(CapacitySlotKey), GpuSchedulerOpaqueKeyValidator.MaximumExecutorFenceKeyLength);
        GpuSchedulerOpaqueKeyValidator.RequireCanonical(ExecutorKey, nameof(ExecutorKey), GpuSchedulerOpaqueKeyValidator.MaximumExecutorFenceKeyLength);
        if (AdmissionGeneration <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(AdmissionGeneration));
        }

        if (DispatchId == Guid.Empty)
        {
            throw new ArgumentException("An executor handle requires a dispatch ID.", nameof(DispatchId));
        }
    }
}

/// <summary>
/// Opaque attestation for one application-owned native worker process.
/// </summary>
public sealed record NativeWorkerInstanceHandle(
    Guid InstanceId,
    string ExecutorKey,
    int ProcessId,
    DateTimeOffset ProcessStartedAtUtc,
    string ProtocolVersion)
{
    public static NativeWorkerInstanceHandle Create(
        Guid instanceId,
        string executorKey,
        int processId,
        DateTimeOffset processStartedAtUtc,
        string protocolVersion)
    {
        var handle = new NativeWorkerInstanceHandle(instanceId, executorKey, processId, processStartedAtUtc, protocolVersion);
        handle.Validate();
        return handle;
    }

    public void Validate()
    {
        if (InstanceId == Guid.Empty)
        {
            throw new ArgumentException("A native worker instance ID is required.", nameof(InstanceId));
        }

        GpuSchedulerOpaqueKeyValidator.RequireCanonical(ExecutorKey, nameof(ExecutorKey), GpuSchedulerOpaqueKeyValidator.MaximumExecutorFenceKeyLength);
        if (ProcessId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(ProcessId));
        }

        RequireUtcTimestamp(ProcessStartedAtUtc, nameof(ProcessStartedAtUtc));
        NativeWorkerProtocol.RequireVersion(ProtocolVersion, nameof(ProtocolVersion));
    }

    public static void RequireUtcTimestamp(DateTimeOffset value, string parameterName)
    {
        if (value == default || value.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("A native worker timestamp must be a non-default UTC value.", parameterName);
        }
    }
}

public enum NativeWorkerFrameKind
{
    Hello,
    Welcome,
    Ready,
    Heartbeat,
    Dispatch,
    TestInstruction,
    Acknowledgement,
    Receipt,
    Callback,
    StopRequested,
    Stopped,
    ProtocolRejected
}

public enum NativeWorkerTestInstruction
{
    AcknowledgeAndHold,
    ReceiptAndComplete,
    ExitBeforeAcknowledgement,
    Unresponsive
}

/// <summary>
/// Closed wire-level task disposition. The application maps this private protocol value to its
/// scheduler boundary; the worker does not carry Domain or task payload contracts.
/// </summary>
public enum NativeWorkerTaskDisposition
{
    Completed,
    OutcomeUncertain
}

public enum NativeWorkerLifecycleClass
{
    LaunchRequested,
    LaunchFailed,
    Connected,
    Ready,
    HeartbeatObserved,
    GracefulStopRequested,
    GracefulStopConfirmed,
    IdentityMismatch,
    Unresponsive,
    Exited,
    Lost,
    TerminationRequested,
    TerminationConfirmed,
    TerminationFailed
}

/// <summary>
/// Closed, serialisable private named-pipe frame. It deliberately contains no free-form detail.
/// </summary>
public sealed record NativeWorkerFrame(
    NativeWorkerFrameKind Kind,
    string ProtocolVersion,
    Guid InstanceId,
    Guid? SessionNonce = null,
    GpuExecutorBatchHandle? Handle = null,
    NativeWorkerTestInstruction? TestInstruction = null,
    NativeWorkerTaskDisposition? Disposition = null)
{
    public void ValidateFor(string expectedProtocolVersion, Guid expectedInstanceId, Guid? expectedSessionNonce)
    {
        NativeWorkerProtocol.RequireVersion(expectedProtocolVersion, nameof(expectedProtocolVersion));
        if (expectedInstanceId == Guid.Empty)
        {
            throw new ArgumentException("An expected worker instance ID is required.", nameof(expectedInstanceId));
        }

        Validate();
        if (!string.Equals(ProtocolVersion, expectedProtocolVersion, StringComparison.Ordinal))
        {
            throw new ArgumentException("The native worker protocol version does not match.", nameof(ProtocolVersion));
        }

        if (InstanceId != expectedInstanceId)
        {
            throw new ArgumentException("The native worker instance ID does not match.", nameof(InstanceId));
        }

        if (expectedSessionNonce is null)
        {
            if (Kind != NativeWorkerFrameKind.Welcome)
            {
                throw new ArgumentException("Only a welcome frame is valid before a session is established.", nameof(Kind));
            }

            return;
        }

        if (Kind is NativeWorkerFrameKind.Hello or NativeWorkerFrameKind.Welcome || SessionNonce != expectedSessionNonce)
        {
            throw new ArgumentException("The native worker session nonce does not match the established session.", nameof(SessionNonce));
        }
    }

    public void Validate()
    {
        if (!Enum.IsDefined(Kind))
        {
            throw new ArgumentOutOfRangeException(nameof(Kind));
        }

        NativeWorkerProtocol.RequireVersion(ProtocolVersion, nameof(ProtocolVersion));
        if (InstanceId == Guid.Empty)
        {
            throw new ArgumentException("A native worker frame requires an instance ID.", nameof(InstanceId));
        }

        if (SessionNonce == Guid.Empty)
        {
            throw new ArgumentException("A native worker session nonce cannot be empty when supplied.", nameof(SessionNonce));
        }

        if (Handle is not null)
        {
            Handle.Validate();
        }

        if (TestInstruction is not null && !Enum.IsDefined(TestInstruction.Value))
        {
            throw new ArgumentOutOfRangeException(nameof(TestInstruction));
        }

        if (Disposition is not null && !Enum.IsDefined(Disposition.Value))
        {
            throw new ArgumentOutOfRangeException(nameof(Disposition));
        }

        switch (Kind)
        {
            case NativeWorkerFrameKind.Hello:
                RequireNoSessionOrPayload();
                break;
            case NativeWorkerFrameKind.Welcome:
                RequireSessionWithoutPayload();
                break;
            case NativeWorkerFrameKind.Dispatch:
                RequireSession();
                if (Handle is null || TestInstruction is not null || Disposition is not null)
                {
                    throw new ArgumentException("A dispatch frame carries only an opaque executor handle.");
                }

                break;
            case NativeWorkerFrameKind.TestInstruction:
                RequireSession();
                if (Handle is not null || TestInstruction is null || Disposition is not null)
                {
                    throw new ArgumentException("A test-instruction frame carries only a bounded instruction.");
                }

                break;
            case NativeWorkerFrameKind.Receipt:
                RequireSession();
                if (Handle is null || TestInstruction is not null || Disposition is null)
                {
                    throw new ArgumentException("A receipt frame requires a handle and bounded disposition.");
                }

                break;
            case NativeWorkerFrameKind.Acknowledgement:
                RequireSession();
                if (Handle is null || TestInstruction is not null || Disposition is not null)
                {
                    throw new ArgumentException("An acknowledgement frame requires only an opaque handle.");
                }

                break;
            case NativeWorkerFrameKind.Ready:
            case NativeWorkerFrameKind.Heartbeat:
            case NativeWorkerFrameKind.Callback:
            case NativeWorkerFrameKind.StopRequested:
            case NativeWorkerFrameKind.Stopped:
                RequireSessionWithoutPayload();
                break;
            case NativeWorkerFrameKind.ProtocolRejected:
                if (Handle is not null || TestInstruction is not null || Disposition is not null)
                {
                    throw new ArgumentException("A protocol rejection frame has no payload.");
                }

                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(Kind));
        }
    }

    private void RequireNoSessionOrPayload()
    {
        if (SessionNonce is not null || Handle is not null || TestInstruction is not null || Disposition is not null)
        {
            throw new ArgumentException("A hello frame has no session or payload.");
        }
    }

    private void RequireSessionWithoutPayload()
    {
        RequireSession();
        if (Handle is not null || TestInstruction is not null || Disposition is not null)
        {
            throw new ArgumentException("This native worker frame has no payload.");
        }
    }

    private void RequireSession()
    {
        if (SessionNonce is null)
        {
            throw new ArgumentException("A native worker frame requires a session nonce.", nameof(SessionNonce));
        }
    }
}

public static class NativeWorkerFrameCodec
{
    public const int MaximumFrameBytes = 16 * 1024;

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter() }
    };

    public static string Serialize(NativeWorkerFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        frame.Validate();
        var json = JsonSerializer.Serialize(frame, SerializerOptions);
        if (System.Text.Encoding.UTF8.GetByteCount(json) > MaximumFrameBytes)
        {
            throw new ArgumentException("A native worker frame exceeds the maximum size.", nameof(frame));
        }

        return json;
    }

    public static NativeWorkerFrame Deserialize(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        if (System.Text.Encoding.UTF8.GetByteCount(json) > MaximumFrameBytes)
        {
            throw new JsonException("A native worker frame exceeds the maximum size.");
        }

        var frame = JsonSerializer.Deserialize<NativeWorkerFrame>(json, SerializerOptions)
            ?? throw new JsonException("A native worker frame is required.");
        try
        {
            frame.Validate();
        }
        catch (ArgumentException exception)
        {
            throw new JsonException("The native worker frame is invalid.", exception);
        }

        return frame;
    }
}

public static class NativeWorkerProtocol
{
    public const string SupportedVersion = "v1";

    public static void RequireVersion(string? value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (!string.Equals(value, SupportedVersion, StringComparison.Ordinal))
        {
            throw new ArgumentException("The native worker protocol version is not supported.", parameterName);
        }
    }
}
