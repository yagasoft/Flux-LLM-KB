namespace FluxKnowledge.Application.Operations;

/// <summary>Immutable file evidence for the one-shot merged payload.</summary>
public sealed record NativeGoLivePayloadFile(string RelativePath, long Length);

/// <summary>Content-addressed manifest that binds a closeout capability to one merged payload.</summary>
public sealed record NativeGoLivePayloadManifest(
    string Sha256,
    int FileCount,
    long TotalBytes,
    IReadOnlyList<NativeGoLivePayloadFile> Files);
