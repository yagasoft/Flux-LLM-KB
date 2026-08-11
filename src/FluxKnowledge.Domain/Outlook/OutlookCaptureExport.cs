using FluxKnowledge.Domain.Common;

namespace FluxKnowledge.Domain.Outlook;

/// <summary>Private export identity. Its EntryId is never a UI projection value.</summary>
public sealed record OutlookExportIdentity
{
    public Guid ProfileId { get; }
    public Guid FolderId { get; }
    public string EntryId { get; }
    public string SourceFingerprint { get; }

    public OutlookExportIdentity(Guid profileId, Guid folderId, string entryId, string sourceFingerprint)
    {
        OutlookCaptureValidation.RequireNonEmpty(profileId, nameof(profileId));
        OutlookCaptureValidation.RequireNonEmpty(folderId, nameof(folderId));
        OutlookCaptureValidation.RequireOpaqueValue(entryId, nameof(entryId), 4096);
        OutlookCaptureValidation.RequireCanonicalSha256(sourceFingerprint, nameof(sourceFingerprint));
        ProfileId = profileId;
        FolderId = folderId;
        EntryId = entryId;
        SourceFingerprint = sourceFingerprint;
    }
}

public sealed record OutlookCaptureExport
{
    public OutlookCaptureExportId Id { get; private init; }
    public OutlookExportIdentity Identity { get; private init; }
    public OutlookExportState State { get; private init; }

    public static OutlookCaptureExport Create(OutlookExportIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        return new OutlookCaptureExport(OutlookCaptureExportId.New(), identity, OutlookExportState.Inflight);
    }

    /// <summary>Prevents a stable Outlook item identity being silently rebound to a different source fingerprint.</summary>
    public void EnsureMatches(OutlookExportIdentity observed)
    {
        ArgumentNullException.ThrowIfNull(observed);
        if (Identity.ProfileId != observed.ProfileId || Identity.FolderId != observed.FolderId ||
            !string.Equals(Identity.EntryId, observed.EntryId, StringComparison.Ordinal))
        {
            throw new DomainInvariantException("Outlook export identity scope does not match the existing export.");
        }

        if (!string.Equals(Identity.SourceFingerprint, observed.SourceFingerprint, StringComparison.Ordinal))
        {
            throw new DomainInvariantException("Outlook export identity cannot be rebound to a different source fingerprint.");
        }
    }

    private OutlookCaptureExport(OutlookCaptureExportId id, OutlookExportIdentity identity, OutlookExportState state)
    {
        Id = id;
        Identity = identity;
        State = state;
    }
}
