namespace FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;

public sealed class CanonicalCodeDisclosureProofEntity
{
    public Guid ArtifactId { get; set; }
    public string Fingerprint { get; set; } = "";
    public string CanonicalHash { get; set; } = "";
    public int CanonicalLength { get; set; }
    public int State { get; set; }
    public int SpanCount { get; set; }
    public string Checksum { get; set; } = "";
}

public sealed class CanonicalCodeDisclosureSpanEntity
{
    public Guid ArtifactId { get; set; }
    public string Fingerprint { get; set; } = "";
    public int Start { get; set; }
    public int End { get; set; }
    public int Kind { get; set; }
    public string Checksum { get; set; } = "";
}
