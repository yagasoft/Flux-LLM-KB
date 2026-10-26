using FluxKnowledge.Domain.Common;

namespace FluxKnowledge.Domain.Sources;

/// <summary>Bounded, caller-synchronised coalescing without rebuilding the path set for every event.</summary>
public sealed class SourceWatchHintAccumulator
{
    private readonly SourceRootId _root;
    private readonly int _maximumPaths;
    private readonly Dictionary<string, SourceWatchPathHint> _paths = new(StringComparer.OrdinalIgnoreCase);
    private DateTimeOffset? _first;
    private DateTimeOffset? _last;
    private SourceWatchSignalKind _kind;
    private bool _overflow;
    private bool _incomplete;
    private bool _repositoryControl;

    public SourceWatchHintAccumulator(SourceRootId root, int maximumPaths = 512)
    {
        ArgumentNullException.ThrowIfNull(root);
        if (maximumPaths is < 1 or > 10000) throw new ArgumentOutOfRangeException(nameof(maximumPaths));
        _root = root; _maximumPaths = maximumPaths;
    }

    public void Add(SourceWatchSignal signal)
    {
        ArgumentNullException.ThrowIfNull(signal);
        if (signal.RootId != _root) throw new DomainInvariantException("Watch signal root must match its hint batch.");
        var first = signal.FirstObservedAtUtc is { } earlier && earlier < signal.ObservedAtUtc ? earlier : signal.ObservedAtUtc;
        if (_first is null || first < _first) _first = first;
        if (_last is null || signal.ObservedAtUtc >= _last) { _last = signal.ObservedAtUtc; _kind = signal.Kind; }
        _overflow |= signal.Kind == SourceWatchSignalKind.Overflow;
        _repositoryControl |= signal.RepositoryControlChanged;
        _incomplete |= signal.PathsIncomplete || signal.Paths is null ||
            signal.Paths.Count == 0 && !signal.RepositoryControlChanged || signal.Kind == SourceWatchSignalKind.Overflow;
        if (signal.Paths is null) return;
        if (signal.Paths.Count > _maximumPaths) InvalidatePaths();
        foreach (var hint in signal.Paths.Take(_maximumPaths))
        {
            var path = hint is null ? null : Normalise(hint.RelativePath);
            if (path is null) { InvalidatePaths(); continue; }
            if (_paths.TryGetValue(path, out var prior))
                _paths[path] = new(path, prior.IsSubtree || hint!.IsSubtree || path == ".");
            else if (_paths.Count < _maximumPaths) _paths.Add(path, new(path, hint!.IsSubtree || path == "."));
            else InvalidatePaths();
        }
    }

    public SourceWatchSignal Snapshot()
    {
        if (_last is null) throw new InvalidOperationException("A watcher hint batch has no observations.");
        return new(_root, _overflow ? SourceWatchSignalKind.Overflow : _kind, _last.Value,
            _paths.Values.OrderBy(x => x.RelativePath, StringComparer.OrdinalIgnoreCase).ToArray(),
            _incomplete, _repositoryControl, _first);
    }

    private void InvalidatePaths() { _overflow = true; _incomplete = true; }

    private static string? Normalise(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 2048 || value[0] is '/' or '\\' ||
            value.IndexOfAny([':', '\0', '*', '?', '"', '<', '>', '|']) >= 0) return null;
        var parts = value.Replace('\\', '/').Split('/');
        if (parts.Any(x => x.Length == 0 || x == "..")) return null;
        var cleaned = parts.Where(x => x != ".").ToArray();
        return cleaned.Length == 0 ? "." : string.Join(Path.DirectorySeparatorChar, cleaned);
    }
}
