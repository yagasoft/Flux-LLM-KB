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
    long? ByteLength);

public sealed record SourceDeletionFileResult(bool Completed, string? ReasonCode = null);
