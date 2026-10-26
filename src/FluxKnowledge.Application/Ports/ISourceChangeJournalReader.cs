namespace FluxKnowledge.Application.Ports;

public sealed record JournalCursor(string VolumeIdentity, ulong JournalId, long NextUsn);
public sealed record JournalCapability(string VolumeIdentity, ulong JournalId, long FirstUsn, long NextUsn,
    long LowestValidUsn, long MaximumSize, long AllocationDelta);
public sealed record JournalChange(long Usn, string FileId, string ParentId, string Name, uint Reason, bool IsDirectory, string? ParentPath = null)
{
    public bool ContentChanged => (Reason & 0x007c0377) != 0;
}
public sealed record JournalRecords(long NextUsn, IReadOnlyList<JournalChange> Changes);
public sealed record JournalPage(JournalCursor Start, JournalCursor Next, IReadOnlyList<JournalChange> Changes);

public interface ISourceChangeJournalReader
{
    ValueTask<JournalCapability> QueryAsync(string volumeIdentity, CancellationToken cancellationToken);
    ValueTask<JournalPage> ReadAsync(JournalCursor cursor, int maximumBytes, CancellationToken cancellationToken);
}

public sealed class SourceJournalGapException(string message, Exception? inner = null) : IOException(message, inner);

public sealed class SourceJournalOptions
{
    public bool Enabled { get; set; }
    public string? PipeName { get; set; }
    public string[] ApprovedVolumes { get; set; } = [];
    public int PollSeconds { get; set; } = 2;
    public int MetadataEntriesPerSecond { get; set; } = 64;
    public bool SealingEnabled { get; set; }
    public SourceJournalSealQualification[] SealQualifications { get; set; } = [];
}

public sealed record SourceJournalSealQualification(string RootIdentityFingerprint, string EnvironmentFingerprint,
    string Scope, string QualificationId)
{
    public string? ProviderName { get; init; }
    public string? ProviderVersion { get; init; }
}
