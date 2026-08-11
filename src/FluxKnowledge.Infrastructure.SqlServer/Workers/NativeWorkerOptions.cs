using FluxKnowledge.Application.Gpu;

namespace FluxKnowledge.Infrastructure.SqlServer.Workers;

/// <summary>
/// Private configuration for the deterministic native-worker supervisor.
/// Production composition remains inert until explicitly enabled.
/// </summary>
public sealed class NativeWorkerOptions
{
    public const string ConfigurationSectionName = "NativeWorker";

    public bool Enabled { get; init; }
    public string? ExecutorKey { get; init; }
    public string? ExecutablePath { get; init; }
    public string ProtocolVersion { get; init; } = NativeWorkerProtocol.SupportedVersion;
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan HeartbeatTimeout { get; init; } = TimeSpan.FromMinutes(1);
    public TimeSpan IdleStopTimeout { get; init; } = TimeSpan.FromMinutes(1);
    public bool AllowForcedTerminationForControlledTests { get; init; }
    public NativeWorkerTestInstruction? TestInstruction { get; init; }
    internal string? PostReadyReadSignalName { get; init; }

    public void Validate()
    {
        if (!Enabled)
        {
            return;
        }

        GpuSchedulerOpaqueKeyValidator.RequireCanonical(
            ExecutorKey,
            nameof(ExecutorKey),
            GpuSchedulerOpaqueKeyValidator.MaximumExecutorFenceKeyLength);
        if (string.IsNullOrWhiteSpace(ExecutablePath) || !Path.IsPathFullyQualified(ExecutablePath) || !File.Exists(ExecutablePath))
        {
            throw new ArgumentException("An existing absolute deterministic worker executable path is required when supervision is enabled.", nameof(ExecutablePath));
        }

        NativeWorkerProtocol.RequireVersion(ProtocolVersion, nameof(ProtocolVersion));
        if (TestInstruction is not null && !Enum.IsDefined(TestInstruction.Value))
        {
            throw new ArgumentOutOfRangeException(nameof(TestInstruction));
        }
        ValidatePositiveBoundedInterval(ConnectTimeout, nameof(ConnectTimeout));
        ValidatePositiveBoundedInterval(HeartbeatTimeout, nameof(HeartbeatTimeout));
        ValidatePositiveBoundedInterval(IdleStopTimeout, nameof(IdleStopTimeout));
    }

    private static void ValidatePositiveBoundedInterval(TimeSpan value, string parameterName)
    {
        if (value <= TimeSpan.Zero || value > TimeSpan.FromHours(1))
        {
            throw new ArgumentOutOfRangeException(parameterName, "Worker supervision intervals must be positive and at most one hour.");
        }
    }
}
