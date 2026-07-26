namespace FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;

public sealed class SourceIdentityEntity
{
    public Guid Id { get; set; }
    public string SourceKind { get; set; } = string.Empty;
    public string StableKey { get; set; } = string.Empty;
    public DateTimeOffset CreatedAtUtc { get; set; }
}
