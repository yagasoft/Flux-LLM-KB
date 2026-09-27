using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluxKnowledge.Application.Documents;
using FluxKnowledge.Application.Ports;

namespace FluxKnowledge.Application.Indexing;

/// <summary>Counts untruncated text using the selected embedding tokenizer.</summary>
public interface IPassageTokenizer
{
    string Fingerprint { get; }
    int CountTokens(string text);
}

public sealed record PassagePolicy(
    int TargetTokens = 192,
    int MaximumTokens = 256,
    int MaximumCharacters = 1024,
    int OverlapTokens = 32,
    int OverlapCharacters = 128);

/// <summary>Builds deterministic, contiguous canonical spans without query-dependent cropping.</summary>
public sealed class PassageBuilder
{
    private readonly IPassageTokenizer _tokenizer;
    private readonly PassagePolicy _policy;

    public PassageBuilder(IPassageTokenizer tokenizer, PassagePolicy? policy = null)
    {
        ArgumentNullException.ThrowIfNull(tokenizer);
        ArgumentException.ThrowIfNullOrWhiteSpace(tokenizer.Fingerprint);
        _tokenizer = tokenizer;
        _policy = policy ?? new PassagePolicy();
        if (_policy.TargetTokens < 1 || _policy.MaximumTokens < _policy.TargetTokens ||
            _policy.MaximumCharacters is < 2 or > 1024 || _policy.OverlapTokens < 0 ||
            _policy.OverlapTokens >= _policy.TargetTokens || _policy.OverlapCharacters < 0 ||
            _policy.OverlapCharacters >= _policy.MaximumCharacters)
            throw new ArgumentOutOfRangeException(nameof(policy));
        PolicyFingerprint = Hash(JsonSerializer.Serialize(new
        {
            version = "coherent-passages-v1", tokenizer = tokenizer.Fingerprint,
            policy = _policy, headerTokens = 32, headerCharacters = 256
        }));
    }

    public string PolicyFingerprint { get; }

    public IReadOnlyList<CanonicalTextChunk> BuildDocument(string text, string? metadataJson)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (metadataJson is null) return Build(text);
        var provenance = DocumentOcrProvenance.Parse(metadataJson);
        var boundaries = new List<int>();
        long previousPageEnd = 0;
        foreach (var page in provenance.Pages.OrderBy(p => p.StartOffset))
        {
            var pageEnd = (long)page.StartOffset + page.Length;
            if (page.StartOffset < previousPageEnd || pageEnd > text.Length)
                throw new InvalidOperationException("passage-provenance-offset-invalid");
            previousPageEnd = pageEnd;
            long previousBlockEnd = page.StartOffset;
            foreach (var block in page.Blocks.OrderBy(b => b.StartOffset))
            {
                var blockEnd = (long)block.StartOffset + block.Length;
                if (block.StartOffset < previousBlockEnd || blockEnd > pageEnd)
                    throw new InvalidOperationException("passage-provenance-offset-invalid");
                previousBlockEnd = blockEnd;
                if (page.Method == "visio" || block.Method == "visio") boundaries.Add(block.StartOffset);
            }
            if (page.Method == "visio") boundaries.Add(page.StartOffset);
        }
        // PDF pages may share a contiguous passage; unrelated retained Visio shapes may not.
        return Build(text, boundaries);
    }

    public IReadOnlyList<CanonicalTextChunk> Build(
        string text, IReadOnlyList<int>? hardBoundaries = null, string contextHeader = "")
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(contextHeader);
        var segmentEnds = (hardBoundaries ?? []).Append(text.Length).Distinct().Order().ToArray();
        if (segmentEnds.Any(end => end < 0 || end > text.Length) || !AreElementBoundaries(text, segmentEnds))
            throw new ArgumentOutOfRangeException(nameof(hardBoundaries));
        var header = BoundHeader(contextHeader);
        var result = new List<CanonicalTextChunk>();
        var start = 0;
        var coveredEnd = 0;
        foreach (var segmentEnd in segmentEnds)
        {
            while (start < segmentEnd)
            {
                var ends = ElementEnds(text, start, segmentEnd, _policy.MaximumCharacters);
                if (ends.Count == 0) throw new InvalidOperationException("passage-text-element-too-large");
                var maximumEnd = FitTokens(text, start, ends, _policy.MaximumTokens);
                if (maximumEnd <= start) throw new InvalidOperationException("passage-token-too-large");
                if (maximumEnd <= coveredEnd)
                {
                    // An overlap consuming this entire input budget is optional;
                    // advancing canonical coverage is mandatory.
                    start = coveredEnd;
                    continue;
                }
                var targetEnd = FitTokens(text, start, ends.Where(end => end <= maximumEnd).ToArray(),
                    _policy.TargetTokens);
                var validEnds = ends.Where(end => end <= maximumEnd).ToArray();
                var end = PreferredEnd(text, start, validEnds.Where(end => end > coveredEnd).ToArray(), targetEnd);
                var content = text.Substring(start, end - start);
                result.Add(new CanonicalTextChunk(0, result.Count, start, content.Length, content,
                    Hash(content), PolicyFingerprint, header));
                coveredEnd = end;
                start = end == segmentEnd ? end : OverlapStart(text, start, end, validEnds);
            }
        }
        return result;
    }

    private int PreferredEnd(string text, int start, IReadOnlyList<int> ends, int targetEnd)
    {
        foreach (var kind in new[] { 0, 1, 2 })
        {
            var before = 0;
            var after = 0;
            foreach (var end in ends)
            {
                var isBoundary = kind switch
                {
                    0 => end == text.Length || end >= 2 && text[end - 1] == '\n' &&
                        (text[end - 2] == '\n' || end >= 3 && text[end - 2] == '\r' && text[end - 3] == '\n'),
                    1 => SentenceEnd(text, start, end),
                    _ => char.IsWhiteSpace(text[end - 1])
                };
                if (!isBoundary || Count(text.Substring(start, end - start)) > _policy.MaximumTokens) continue;
                if (end <= targetEnd) before = end;
                else if (after == 0) after = end;
            }
            if (before > start) return before;
            if (after > start) return after;
        }
        return ends[^1];
    }

    private int OverlapStart(string text, int start, int end, IReadOnlyList<int> ends)
    {
        if (_policy.OverlapTokens == 0 || _policy.OverlapCharacters == 0 || !SentenceEnd(text, start, end))
            return end;
        var sentenceStart = end;
        foreach (var candidate in ends.Reverse())
        {
            if (candidate >= end || !SentenceEnd(text, start, candidate)) continue;
            if (end - candidate > _policy.OverlapCharacters ||
                Count(text.Substring(candidate, end - candidate)) > _policy.OverlapTokens) break;
            sentenceStart = candidate;
        }
        return sentenceStart;
    }

    private string BoundHeader(string header)
    {
        var ends = ElementEnds(header, 0, header.Length, 256);
        var end = FitTokens(header, 0, ends, 32);
        return header[..end].TrimEnd();
    }

    private int FitTokens(string text, int start, IReadOnlyList<int> ends, int budget)
    {
        if (ends.Count == 0) return start;
        if (Count(text.Substring(start, ends[^1] - start)) <= budget) return ends[^1];
        var low = 0;
        var high = ends.Count - 1;
        var best = start;
        // BPE prefix counts need not be monotonic. This finds a bounded valid prefix,
        // not a promise of the longest one; every accepted boundary is checked.
        while (low <= high)
        {
            var middle = low + (high - low) / 2;
            if (Count(text.Substring(start, ends[middle] - start)) <= budget)
            {
                best = ends[middle];
                low = middle + 1;
            }
            else high = middle - 1;
        }
        return best;
    }

    private int Count(string text)
    {
        var count = _tokenizer.CountTokens(text);
        return count >= 0 ? count : throw new InvalidOperationException("passage-tokenizer-invalid");
    }

    private static List<int> ElementEnds(string text, int start, int segmentEnd, int maximumCharacters)
    {
        var result = new List<int>();
        var end = start;
        while (end < segmentEnd)
        {
            end += StringInfo.GetNextTextElementLength(text.AsSpan(end));
            if (end > segmentEnd || end - start > maximumCharacters) break;
            result.Add(end);
        }
        return result;
    }

    private static bool AreElementBoundaries(string text, IReadOnlyList<int> boundaries)
    {
        var position = 0;
        foreach (var boundary in boundaries)
        {
            while (position < boundary) position += StringInfo.GetNextTextElementLength(text.AsSpan(position));
            if (position != boundary) return false;
        }
        return true;
    }

    private static bool SentenceEnd(string text, int start, int end)
    {
        // One boundary after all separator whitespace, not one per whitespace character.
        if (end < text.Length && char.IsWhiteSpace(text[end])) return false;
        var punctuation = end - 1;
        while (punctuation >= start && char.IsWhiteSpace(text[punctuation])) punctuation--;
        return punctuation >= start && text[punctuation] is '.' or '!' or '?' &&
            (end == text.Length || char.IsWhiteSpace(text[end]) || char.IsWhiteSpace(text[end - 1]));
    }

    private static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}
