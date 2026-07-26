using FluxKnowledge.Domain.Common;

namespace FluxKnowledge.Domain.Pipeline;

public sealed record PipelineRecord(
    PipelineRecordId Id,
    SourceIdentityId SourceIdentityId,
    long Revision,
    string ContentHash,
    PipelineRecordId RootLineageRecordId,
    PipelineRecordId? ParentRevisionRecordId,
    PipelineStage CurrentStage,
    bool CompletionCriteriaMet,
    DateTimeOffset RegisteredAtUtc)
{
    public bool IsCompleted => CompletionCriteriaMet && CurrentStage == PipelineStage.Publish;

    public static PipelineRecord Register(
        SourceIdentity source,
        long revision,
        string contentHash,
        string? requestedBy)
    {
        ArgumentNullException.ThrowIfNull(source);
        EnsurePositiveRevision(revision);
        EnsureContentHash(contentHash);
        _ = requestedBy;

        var id = PipelineRecordId.New();
        return new PipelineRecord(
            id,
            source.Id,
            revision,
            contentHash,
            id,
            null,
            PipelineStage.Extract,
            false,
            DateTimeOffset.UtcNow);
    }

    public PipelineRecord CreateRevision(long revision, string contentHash)
    {
        EnsurePositiveRevision(revision);
        EnsureContentHash(contentHash);

        if (revision <= Revision)
        {
            throw new DomainInvariantException("A new revision must be greater than the current revision.");
        }

        if (string.Equals(ContentHash, contentHash, StringComparison.Ordinal))
        {
            throw new DomainInvariantException("A new revision must have a different content hash.");
        }

        return new PipelineRecord(
            PipelineRecordId.New(),
            SourceIdentityId,
            revision,
            contentHash,
            RootLineageRecordId,
            Id,
            PipelineStage.Extract,
            false,
            DateTimeOffset.UtcNow);
    }

    public PipelineRecord AdvanceTo(PipelineStage stage, bool completionCriteriaMet = false)
    {
        if (stage < CurrentStage)
        {
            throw new DomainInvariantException("A pipeline record cannot move backwards through stages.");
        }

        if (completionCriteriaMet && stage != PipelineStage.Publish)
        {
            throw new DomainInvariantException("Completion criteria can be met only at the publish stage.");
        }

        return this with { CurrentStage = stage, CompletionCriteriaMet = completionCriteriaMet };
    }

    private static void EnsurePositiveRevision(long revision)
    {
        if (revision <= 0)
        {
            throw new DomainInvariantException("Revision must be positive.");
        }
    }

    private static void EnsureContentHash(string contentHash)
    {
        if (string.IsNullOrWhiteSpace(contentHash))
        {
            throw new DomainInvariantException("Content hash is required.");
        }
    }
}
