using System.Collections.Concurrent;
using FluxKnowledge.Application.Ports;

namespace FluxKnowledge.Application.Sources;

/// <summary>Every process and every new root configuration begins with admission closed.</summary>
public sealed class SourceWorkAdmission : ISourceWorkAdmission
{
    private sealed record State(long Revision, bool Prepared, bool Admitted);
    private readonly ConcurrentDictionary<Guid, State> _roots = new();
    public static ISourceWorkAdmission Closed { get; } = new ClosedAdmission();
    public bool IsPrepared(Guid rootId, long revision) => _roots.TryGetValue(rootId, out var state) && state.Revision == revision && state.Prepared;
    public bool IsAdmitted(Guid rootId, long revision) => _roots.TryGetValue(rootId, out var state) && state.Revision == revision && state.Admitted;
    public void MarkPrepared(Guid rootId, long revision) => _roots[rootId] = new(revision, true, false);
    public void Admit(Guid rootId, long revision) => _roots[rootId] = new(revision, true, true);
    public IReadOnlyCollection<SourceWorkAdmittedRoot> ReadAdmittedRoots() => _roots.Where(x => x.Value.Admitted)
        .Select(x => new SourceWorkAdmittedRoot(x.Key, x.Value.Revision)).ToArray();
    private sealed class ClosedAdmission : ISourceWorkAdmission
    {
        public IReadOnlyCollection<SourceWorkAdmittedRoot> ReadAdmittedRoots() => [];
    }
}
