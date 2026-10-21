namespace FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;

public sealed class IndexRecoveryAuditEntity
{
    public int Id { get; set; } = 1;
    public bool IsValid { get; set; }
    public int ClassifierVersion { get; set; }
    public Guid DatabaseIncarnation { get; set; }
    public string RecoveryRoot { get; set; } = "";
    public string IndexRoot { get; set; } = "";
    public string RootFingerprint { get; set; } = "";
    public string InventoryChecksum { get; set; } = "";
    public DateTimeOffset? SealedAtUtc { get; set; }
    public string? FailurePath { get; set; }
    public byte[] RowVersion { get; set; } = [];
}

public sealed class IndexRecoveryPinEntity
{
    public string Id { get; set; } = "";
    public string Packet { get; set; } = "";
    public Guid? GenerationId { get; set; }
    public string? IndexPath { get; set; }
}

/// <summary>Unfinished filesystem writer; SQL-session loss never implies its process exited.</summary>
public sealed class IndexRecoveryWriterEntity
{
    public Guid Id { get; set; }
    public string RecoveryRoot { get; set; } = "";
    public int OwnerProcessId { get; set; }
    public DateTimeOffset OwnerStartedAtUtc { get; set; }
    public string OwnerMachineFingerprint { get; set; } = "";
    // False for writers that may outlive their parent through external processes.
    public bool OwnerExitSufficient { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
}
