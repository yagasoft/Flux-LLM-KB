namespace FluxKnowledge.Application.Gpu;

/// <summary>Process incarnation that owns private in-memory search work; never a query payload.</summary>
public sealed record GpuInteractiveOwnerIdentity(int ProcessId, DateTimeOffset StartedAtUtc, string MachineFingerprint)
{
    public void Validate()
    {
        if (ProcessId <= 0 || StartedAtUtc == default || StartedAtUtc.Offset != TimeSpan.Zero ||
            MachineFingerprint is null || MachineFingerprint.Length != 64 ||
            MachineFingerprint.Any(c => c is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
            throw new ArgumentException("interactive-process-identity-invalid");
    }
}

public enum GpuInteractiveOwnerObservation { Unknown, Alive, Exited }

public interface IGpuInteractiveOwnerProbe
{
    GpuInteractiveOwnerIdentity Current { get; }
    GpuInteractiveOwnerObservation Observe(GpuInteractiveOwnerIdentity owner);
}
