namespace FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;

/// <summary>One native allocation. Released rows retain the process and physical release proof.</summary>
public sealed class GpuModelResidencyEntity
{
    public Guid ResidencyId { get; set; }
    public string SlotKey { get; set; } = string.Empty;
    public string RuntimeKey { get; set; } = string.Empty;
    public string SettingsFingerprint { get; set; } = string.Empty;
    public string ModelKey { get; set; } = string.Empty;
    public int OwnerProcessId { get; set; }
    public DateTimeOffset OwnerStartedAtUtc { get; set; }
    public string MachineFingerprint { get; set; } = string.Empty;
    public Guid? ChildJobId { get; set; }
    public int? ChildProcessId { get; set; }
    public DateTimeOffset? ChildStartedAtUtc { get; set; }
    public int State { get; set; }
    public long Version { get; set; }
    public Guid? BorrowedDispatchId { get; set; }
    public Guid? BorrowedExecutionInstanceId { get; set; }
    public DateTimeOffset? IdleUntilUtc { get; set; }
    public DateTimeOffset? ReleasedAtUtc { get; set; }
    public int? ReleaseKind { get; set; }
}
