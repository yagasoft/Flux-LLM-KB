namespace FluxKnowledge.Domain.Outlook;

/// <summary>Private reconciliation identity. It must not be projected to local or public UI surfaces.</summary>
public sealed record OutlookFolderIdentity
{
    public string StoreId { get; }
    public string FolderEntryId { get; }
    public string DisplayName { get; }

    public OutlookFolderIdentity(string storeId, string folderEntryId, string displayName)
    {
        OutlookCaptureValidation.RequireOpaqueValue(storeId, nameof(storeId), 4096);
        OutlookCaptureValidation.RequireOpaqueValue(folderEntryId, nameof(folderEntryId), 4096);
        OutlookCaptureValidation.RequireDisplayName(displayName, nameof(displayName));
        StoreId = storeId;
        FolderEntryId = folderEntryId;
        DisplayName = displayName;
    }
}

public sealed record OutlookCaptureFolder
{
    public OutlookCaptureFolderId Id { get; private init; }
    public OutlookCaptureProfileId ProfileId { get; private init; }
    public OutlookFolderIdentity Identity { get; private init; }

    public static OutlookCaptureFolder Create(OutlookCaptureProfileId profileId, OutlookFolderIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(profileId);
        ArgumentNullException.ThrowIfNull(identity);
        OutlookCaptureValidation.RequireNonEmpty(profileId.Value, nameof(profileId));
        return new OutlookCaptureFolder(OutlookCaptureFolderId.New(), profileId, identity);
    }

    private OutlookCaptureFolder(OutlookCaptureFolderId id, OutlookCaptureProfileId profileId, OutlookFolderIdentity identity)
    {
        Id = id;
        ProfileId = profileId;
        Identity = identity;
    }
}
