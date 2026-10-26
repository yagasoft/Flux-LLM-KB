using FluxKnowledge.Application.Contracts;
using FluxKnowledge.Domain.Sources;
using System.Text.Json.Serialization;

namespace FluxKnowledge.Application.Ports;

public static class SourcePendingReasons
{
    public const long NamespaceMetadata = 1L << 32;
    public const long GitMembership = 1L << 33;
    public const long ScopedNamespace = 1L << 34;
    public const long NamespaceMask = NamespaceMetadata | ScopedNamespace;
}
public sealed record SourceScanTarget(string RelativePath, long Generation, bool IsSubtree, long ReasonFlags)
{
    public bool HasCaptureScope => ReasonFlags != SourcePendingReasons.GitMembership;
    public long? LastContentUsn { get; init; }
    public long? LastUnjournaledGeneration { get; init; }
}
public sealed record SourceIncrementalPlan(Guid ManifestId, long BaselineEpoch, bool IsBaseline, string Mode,
    JournalCursor? Cursor, long Generation, IReadOnlyList<SourceScanTarget> Targets)
{
    [JsonRequired]
    public int Version { get; init; } = 1;
    public long LeaseGeneration { get; init; }
    public long RecoveryGeneration { get; init; }
    public long NamespaceGeneration { get; init; }
    public Guid? SupersedesManifestId { get; init; }
    public bool IsReconciliation { get; init; }
    public int MetadataEntriesPerSecond { get; init; }
    public bool IsFallback => Mode == "Fallback";
    public bool IsNamespaceSweep => IsBaseline || Targets.Any(x => x.RelativePath == "." && x.IsSubtree &&
        (x.ReasonFlags & SourcePendingReasons.NamespaceMetadata) != 0);
}
public sealed record RetainedFileObservation(string Identity, long ByteLength, long LastWriteTicks, long ChangeTicks);
public interface IRetainedFileObservationLease : IAsyncDisposable
{
    RetainedFileObservation Observation { get; }
    DateTimeOffset? ChecksumVerifiedAtUtc => null;
    void ValidateCurrent();
}
public interface ISourceRetainedObservationStore
{
    ValueTask<IRetainedFileObservationLease> OpenObservationAsync(string relativePath, string hash, long byteLength,
        bool verifyChecksum, CancellationToken cancellationToken);
}
public interface ISourceIncrementalCoordinator
{
    ValueTask<SourceIncrementalPlan?> PrepareAsync(SourceRootConfiguration root, SourceScanRequest request, CancellationToken ct);
    ValueTask<SourceRevisionId?> TryReuseAsync(SourceRootConfiguration root, SourceScanRequest request, SourceIncrementalPlan plan, string relativePath, CancellationToken ct);
    ValueTask RecordCaptureAsync(SourceRootConfiguration root, SourceScanRequest request, SourceIncrementalPlan plan, SourceDiscoveredFile file, SourceRevisionId revision, ISourceFileCapture? capture, CancellationToken ct);
    ValueTask<bool> CompleteAsync(SourceRootConfiguration root, SourceScanRequest request, SourceIncrementalPlan plan,
        IReadOnlyList<SourceNamespaceObservation> directories, IReadOnlyList<SourceEnumerationEvidence> evidence,
        IReadOnlySet<SourceRevisionId> seen, GitInventoryEvidence? inventory, CancellationToken ct);
    ValueTask<int> PumpAsync(CancellationToken ct);
}
