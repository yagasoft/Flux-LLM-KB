namespace FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;

public sealed class SourceRootWatchStateEntity
{
    public Guid SourceRootId { get; set; }
    public DateTimeOffset FirstSignalAtUtc { get; set; }
    public DateTimeOffset LastSignalAtUtc { get; set; }
    public int SignalCount { get; set; }
    public long DebounceGeneration { get; set; }
    public DateTimeOffset DueAtUtc { get; set; }
    public string? LeaseOwner { get; set; }
    public long LeaseGeneration { get; set; }
    public DateTimeOffset? LeaseExpiresAtUtc { get; set; }
    public byte[] RowVersion { get; set; } = [];
    public SourceRootConfigurationEntity SourceRoot { get; set; } = null!;
}
