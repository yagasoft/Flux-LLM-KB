using System.Security.Cryptography;
using System.Text;
using FluxKnowledge.Application.Ports;

namespace FluxKnowledge.Application.Search;

/// <summary>Checks passage content separately from search deduplication and citation ownership.</summary>
internal sealed class PassageIntegrityVerifier
{
    internal static PassageIntegrityVerifier Shared { get; } = new();
    private const int MaximumEntries = 512;
    private readonly object _gate = new();
    private readonly Dictionary<(Guid Artifact, long Chunk), Entry> _entries = [];
    private readonly Queue<(Guid Artifact, long Chunk)> _order = new();
    private long _computations;
    internal long Computations => Interlocked.Read(ref _computations);

    internal bool MatchesBody(EligiblePassageCandidate passage, string? expected = null) =>
        string.Equals(Compute(passage).Body, expected ?? passage.ChunkHash, StringComparison.Ordinal);

    internal bool MatchesSearchInput(EligiblePassageCandidate passage) =>
        passage.SearchInputHash.Length == 64 &&
        string.Equals(Compute(passage).Input, passage.SearchInputHash, StringComparison.Ordinal);

    private Digests Compute(EligiblePassageCandidate passage)
    {
        var key = (passage.ArtifactId, passage.ChunkId);
        lock (_gate)
            if (_entries.TryGetValue(key, out var cached) &&
                string.Equals(cached.Content, passage.Content, StringComparison.Ordinal) &&
                string.Equals(cached.Header, passage.ContextHeader, StringComparison.Ordinal)) return cached.Digests;
        Interlocked.Increment(ref _computations);
        var body = Hash(passage.Content);
        var digests = new Digests(body, passage.ContextHeader.Length == 0 ? body : Hash(passage.ContextHeader + "\n" + passage.Content));
        if (passage.Content.Length <= 2048 && passage.ContextHeader.Length <= 1024)
            lock (_gate)
            {
                if (!_entries.ContainsKey(key))
                {
                    if (_entries.Count >= MaximumEntries) _entries.Remove(_order.Dequeue());
                    _order.Enqueue(key);
                }
                _entries[key] = new(passage.Content, passage.ContextHeader, digests);
            }
        return digests;
    }

    private static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private sealed record Digests(string Body, string Input);
    private sealed record Entry(string Content, string Header, Digests Digests);
}
