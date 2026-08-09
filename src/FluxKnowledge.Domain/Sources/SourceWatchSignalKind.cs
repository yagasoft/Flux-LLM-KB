namespace FluxKnowledge.Domain.Sources;

public enum SourceWatchSignalKind
{
    Created,
    Changed,
    Deleted,
    Renamed,
    Overflow
}
