using System.Text.Json;
using FluxKnowledge.Application.Gpu;
using Xunit;

namespace FluxKnowledge.Domain.Tests.Gpu;

public sealed class NativeWorkerContractTests
{
    [Theory]
    [InlineData("00000000-0000-0000-0000-000000000000", "executor-a", 42, "2026-08-10T09:00:00+00:00", "v1")]
    [InlineData("11111111-1111-1111-1111-111111111111", "executor-a ", 42, "2026-08-10T09:00:00+00:00", "v1")]
    [InlineData("11111111-1111-1111-1111-111111111111", "executor-a", 0, "2026-08-10T09:00:00+00:00", "v1")]
    [InlineData("11111111-1111-1111-1111-111111111111", "executor-a", 42, "0001-01-01T00:00:00+00:00", "v1")]
    [InlineData("11111111-1111-1111-1111-111111111111", "executor-a", 42, "2026-08-10T09:00:00+02:00", "v1")]
    public void Instance_handle_rejects_invalid_attestation_data(
        string instanceId,
        string executorKey,
        int processId,
        string processStartedAtUtc,
        string protocolVersion)
    {
        Assert.ThrowsAny<ArgumentException>(() => NativeWorkerInstanceHandle.Create(
            Guid.Parse(instanceId),
            executorKey,
            processId,
            DateTimeOffset.Parse(processStartedAtUtc),
            protocolVersion));
    }

    [Fact]
    public void Frame_kind_and_test_instruction_enums_are_closed()
    {
        Assert.Equal(
            ["Hello", "Welcome", "Ready", "Heartbeat", "Dispatch", "TestInstruction", "Acknowledgement", "Receipt", "Callback", "StopRequested", "Stopped", "ProtocolRejected"],
            Enum.GetNames<NativeWorkerFrameKind>());
        Assert.Equal(
            ["AcknowledgeAndHold", "ReceiptAndComplete", "ExitBeforeAcknowledgement", "Unresponsive"],
            Enum.GetNames<NativeWorkerTestInstruction>());
    }

    [Theory]
    [InlineData("{\"kind\":99,\"protocolVersion\":\"v1\",\"instanceId\":\"11111111-1111-1111-1111-111111111111\"}")]
    [InlineData("{\"kind\":\"Hello\",\"protocolVersion\":\"v1\",\"instanceId\":\"11111111-1111-1111-1111-111111111111\",\"detail\":\"raw exception text\"}")]
    public void Codec_rejects_unknown_frame_kinds_and_raw_detail(string json)
    {
        Assert.Throws<JsonException>(() => NativeWorkerFrameCodec.Deserialize(json));
    }

    [Fact]
    public void Frame_rejects_a_protocol_mismatch_before_it_can_reach_a_receiver()
    {
        var frame = new NativeWorkerFrame(
            NativeWorkerFrameKind.Ready,
            "v2",
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            Guid.Parse("22222222-2222-2222-2222-222222222222"));

        Assert.Throws<ArgumentException>(() => frame.ValidateFor(
            "v1",
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            Guid.Parse("22222222-2222-2222-2222-222222222222")));
    }

    [Theory]
    [MemberData(nameof(PostWelcomeFramesWithoutTheExpectedNonce))]
    public void Post_welcome_phase_rejects_hello_welcome_and_protocol_rejection_without_the_exact_nonce(
        NativeWorkerFrame frame)
    {
        Assert.Throws<ArgumentException>(() => frame.ValidateFor(
            "v1",
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            Guid.Parse("22222222-2222-2222-2222-222222222222")));
    }

    [Fact]
    public void Protocol_rejects_an_unsupported_version()
    {
        Assert.Throws<ArgumentException>(() => NativeWorkerProtocol.RequireVersion("v2", "protocolVersion"));
    }

    [Fact]
    public void Codec_accepts_an_exactly_16KiB_valid_frame()
    {
        const string frame = "{\"kind\":\"Hello\",\"protocolVersion\":\"v1\",\"instanceId\":\"11111111-1111-1111-1111-111111111111\"}";
        var paddedFrame = frame.PadRight(NativeWorkerFrameCodec.MaximumFrameBytes);

        Assert.Equal(NativeWorkerFrameCodec.MaximumFrameBytes, System.Text.Encoding.UTF8.GetByteCount(paddedFrame));
        Assert.Equal(NativeWorkerFrameKind.Hello, NativeWorkerFrameCodec.Deserialize(paddedFrame).Kind);
    }

    [Fact]
    public void Codec_rejects_a_frame_larger_than_16KiB()
    {
        const string frame = "{\"kind\":\"Hello\",\"protocolVersion\":\"v1\",\"instanceId\":\"11111111-1111-1111-1111-111111111111\"}";
        var oversizedFrame = frame.PadRight(NativeWorkerFrameCodec.MaximumFrameBytes + 1);

        Assert.Throws<JsonException>(() => NativeWorkerFrameCodec.Deserialize(oversizedFrame));
    }

    [Fact]
    public void Lifecycle_evidence_enum_contains_only_sanitised_classes()
    {
        Assert.Equal(
            ["LaunchRequested", "LaunchFailed", "Connected", "Ready", "HeartbeatObserved", "GracefulStopRequested", "GracefulStopConfirmed", "IdentityMismatch", "Unresponsive", "Exited", "Lost", "TerminationRequested", "TerminationConfirmed", "TerminationFailed"],
            Enum.GetNames<NativeWorkerLifecycleClass>());
    }

    [Fact]
    public void Recovery_candidate_rejects_attestation_or_active_handle_for_a_different_instance_or_executor()
    {
        var instanceId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var attested = NativeWorkerInstanceHandle.Create(instanceId, "executor-a", 42, DateTimeOffset.Parse("2026-08-10T10:00:00+00:00"), "v1");
        var mismatchedHandle = new GpuExecutorBatchHandle(Guid.NewGuid(), "slot-a", "executor-b", 1, Guid.NewGuid());
        var candidate = new NativeWorkerRecoveryCandidate(instanceId, NativeWorkerLifecycleClass.Connected, attested, mismatchedHandle);

        Assert.Throws<ArgumentException>(() => candidate.Validate("executor-a"));
    }

    public static IEnumerable<object[]> PostWelcomeFramesWithoutTheExpectedNonce()
    {
        var instanceId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        yield return [new NativeWorkerFrame(NativeWorkerFrameKind.Hello, "v1", instanceId)];
        yield return [new NativeWorkerFrame(NativeWorkerFrameKind.Welcome, "v1", instanceId, Guid.Parse("33333333-3333-3333-3333-333333333333"))];
        yield return [new NativeWorkerFrame(NativeWorkerFrameKind.ProtocolRejected, "v1", instanceId)];
        yield return [new NativeWorkerFrame(NativeWorkerFrameKind.ProtocolRejected, "v1", instanceId, Guid.Parse("33333333-3333-3333-3333-333333333333"))];
    }
}
