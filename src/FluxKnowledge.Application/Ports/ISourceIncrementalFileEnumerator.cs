using FluxKnowledge.Domain.Sources;

namespace FluxKnowledge.Application.Ports;

public sealed record SourceEnumerationScope(IReadOnlyList<string>? Targets,
    Func<string, CancellationToken, ValueTask<bool>>? CaptureNeeded)
{
    public int MetadataEntriesPerSecond { get; init; }
}
public sealed record SourceNamespaceObservation(string RelativePath, string FileId, string ParentId, bool IsAncestor);
public sealed record SourceFileObservation(string Identity, string FileId, string ParentId, long ByteLength,
    long LastWriteTicks, long ChangeTicks, uint Attributes, uint LinkCount);

public interface ISourceObservationLease : IDisposable
{
    SourceFileObservation Observation { get; }
    void ValidateCurrent();
}
public interface ISourceObservationReader
{
    ISourceObservationLease Open(SourceRootConfiguration root, string relativePath);
    IReadOnlyList<SourceNamespaceObservation> ReadRootAncestors(SourceRootConfiguration root);
    ISourceRootObservationLease OpenRoot(SourceRootConfiguration root);
    ISourceAbsenceLease? OpenAbsence(SourceRootConfiguration root, string relativePath);
}
public interface ISourceAbsenceLease : IDisposable { void ValidateCurrent(); }
public interface ISourceRootObservationLease : IDisposable
{
    IReadOnlyList<SourceNamespaceObservation> Ancestors { get; }
    void ValidateCurrent();
}
public interface IIncrementalSourceFileEnumerator : ICapturingSourceFileEnumerator
{
    IDisposable UseScope(SourceEnumerationScope scope);
    IReadOnlyList<SourceNamespaceObservation> LastDirectories { get; }
    int LastEnumeratedEntries { get; }
}
