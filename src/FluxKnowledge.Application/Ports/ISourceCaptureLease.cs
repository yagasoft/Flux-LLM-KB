namespace FluxKnowledge.Application.Ports;

/// <summary>
/// A verified physical capture whose actual source read handle remains held until
/// its retained reference commits. Persisted discovery JSON is not this authority.
/// </summary>
public interface ISourceCaptureLease : ISourceArtifactPublicationLease
{
    SourceDiscoveredFile Snapshot { get; }
    bool HasSingleLink { get; }
    void ValidateCurrent();
}
