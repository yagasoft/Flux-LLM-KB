namespace FluxKnowledge.Application.Gpu;

public enum GpuModelResidencyState { Borrowed, Idle, Evicting, Uncertain, Released }

/// <summary>Native phase and artifact identity are explicit even when a scheduler profile covers several models.</summary>
public sealed record GpuResidentModelKey(string RuntimeKey, string SettingsFingerprint, string ModelKey)
{
    public void Validate()
    {
        GpuSchedulerOpaqueKeyValidator.RequireCanonical(RuntimeKey, nameof(RuntimeKey), 256);
        GpuSchedulerOpaqueKeyValidator.RequireCanonical(SettingsFingerprint, nameof(SettingsFingerprint), 256);
        GpuSchedulerOpaqueKeyValidator.RequireCanonical(ModelKey, nameof(ModelKey), 256);
    }
}

public sealed record GpuResidentHelperIdentity(Guid JobId, int ProcessId, DateTimeOffset StartedAtUtc)
{
    public void Validate()
    {
        if (JobId == Guid.Empty || ProcessId <= 0 || StartedAtUtc == default || StartedAtUtc.Offset != TimeSpan.Zero)
            throw new ArgumentException("gpu-resident-helper-identity-invalid");
    }
}

/// <summary>Exact identity of a native allocation. Version advances on every checkout and eviction reservation.</summary>
public sealed record GpuResidentModelFence(Guid ResidencyId, string SlotKey, long Version,
    GpuInteractiveOwnerIdentity Owner, GpuResidentHelperIdentity? Helper)
{
    public void Validate()
    {
        if (ResidencyId == Guid.Empty || Version <= 0) throw new ArgumentException("gpu-resident-fence-invalid");
        GpuSchedulerOpaqueKeyValidator.RequireCanonical(SlotKey, nameof(SlotKey), 256);
        Owner.Validate();
        Helper?.Validate();
        if (Helper is not null && Helper.JobId != ResidencyId)
            throw new ArgumentException("gpu-resident-helper-generation-mismatch");
    }
}

public enum GpuResidentEvictionReason { Expired, IncompatibleRequest, Maintenance, Shutdown }

public enum GpuResidentReleaseKind { NativeSessionDisposed, ContainedJobEmpty, HostingProcessExited }

/// <summary>A model owner may retain exactly one in-process allocation beyond a successfully settled dispatch.</summary>
public interface IGpuProcessModelResidency
{
    bool Supports(GpuOwnedWorkContext context);
    ValueTask<T> GetModelAsync<T>(GpuOwnedWorkContext context, GpuResidentModelKey model,
        Func<ValueTask<T>> open) where T : class, IGpuProcessResidentModel;
    ValueTask<bool> CompleteAsync(GpuOwnedWorkContext context, Guid operationId, Guid taskId, CancellationToken cancellationToken);
    ValueTask<bool> ReleaseAsync(GpuOwnedWorkContext context, CancellationToken cancellationToken);
    ValueTask<bool> RecoverExitedAsync(GpuExecutorBatchHandle handle, GpuInteractiveOwnerIdentity owner,
        CancellationToken cancellationToken);
}

public interface IGpuProcessResidentModel : IDisposable
{
    bool IsIdle { get; }
    bool PhysicalReleaseConfirmed { get; }
}
