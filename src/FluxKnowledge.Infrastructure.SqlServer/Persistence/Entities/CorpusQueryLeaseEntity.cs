namespace FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;

public sealed class CorpusQueryLeaseEntity
{
    public Guid Id { get; set; }
    public Guid GenerationId { get; set; }
    public Guid CorpusEpoch { get; set; }
    public long CorpusVersion { get; set; }
    public string ModelFingerprint { get; set; } = string.Empty;
    public int Dimensions { get; set; }
    public Guid OwnerInstanceId { get; set; }
    public int OwnerProcessId { get; set; }
    public DateTimeOffset OwnerStartedAtUtc { get; set; }
    public string OwnerMachineFingerprint { get; set; } = string.Empty;
    public int SqlSessionId { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
}
