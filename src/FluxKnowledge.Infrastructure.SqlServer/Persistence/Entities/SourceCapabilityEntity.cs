namespace FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;

/// <summary>SQL-visible processor registry. Native executor descriptors are persisted non-runnable.</summary>
public sealed class SourceCapabilityEntity
{
    public Guid Id { get; set; }
    public string ProcessorKind { get; set; } = string.Empty;
    public string ProcessorVersion { get; set; } = string.Empty;
    public int ExecutionClass { get; set; }
    public string AcceptedClassificationsJson { get; set; } = "[]";
    public string OutputContract { get; set; } = string.Empty;
    public string ProcessorFingerprint { get; set; } = string.Empty;
    public bool IsRunnable { get; set; }
    public string RegisteredBy { get; set; } = string.Empty;
    public DateTimeOffset RegisteredAtUtc { get; set; }
    public string? RegistrationEvidenceJson { get; set; }
    public byte[] RowVersion { get; set; } = [];
}
