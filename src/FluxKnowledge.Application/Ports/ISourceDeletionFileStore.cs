namespace FluxKnowledge.Application.Ports;

/// <summary>Deletes one SQL-captured, app-owned physical target without accepting arbitrary paths.</summary>
public interface ISourceDeletionFileStore
{
    ValueTask<SourceDeletionFileResult> DeleteAsync(
        SourceDeletionFileTarget target,
        CancellationToken cancellationToken);
}

public sealed record SourceDeletionFileTarget(
    Guid CleanupItemId,
    int StorageKind,
    string RelativePath,
    string? ContentSha256,
    long? ByteLength)
{
    public bool SharedReferenceExists { get; init; }

    public bool MatchesBinding(SourceDeletionFileTarget other) =>
        CleanupItemId == other.CleanupItemId && StorageKind == other.StorageKind &&
        RelativePath == other.RelativePath && ContentSha256 == other.ContentSha256 && ByteLength == other.ByteLength;
}

public sealed record SourceDeletionFileOutcome(SourceDeletionFileTarget Target, SourceDeletionFileResult Result);

public sealed record SourceDeletionFileResult(bool Completed, string? ReasonCode = null);
