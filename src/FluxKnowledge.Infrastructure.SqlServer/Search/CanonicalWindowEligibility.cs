using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;

namespace FluxKnowledge.Infrastructure.SqlServer.Search;

/// <summary>Window construction eligibility only; it never grants disclosure or publication authority.</summary>
internal static class CanonicalWindowEligibility
{
    internal const string RuleVersion = SqlDisclosureWindowAvailability.PolicyId;

    internal static bool Evaluate(string canonical, long offset, int length, string content)
    {
        if (offset < 0 || offset > canonical.Length || length is < 1 or > 2048 ||
            length > canonical.Length-offset || content.Length != length ||
            !canonical.AsSpan((int)offset,length).SequenceEqual(content.AsSpan())) return false;
        var origin = Math.Max(0,(int)offset-4096);
        var text = canonical.Substring(origin,Math.Min(16384,canonical.Length-origin));
        return EvaluateWindow(text,origin,canonical.Length,offset,length,content);
    }

    internal static bool EvaluateWindow(string text, int origin, long canonicalLength, long offset, int length, string content)
    {
        if (canonicalLength is < 0 or > int.MaxValue || offset < 0 || offset > canonicalLength ||
            length is < 1 or > 2048 || length > canonicalLength-offset || content.Length != length ||
            origin != Math.Max(0,offset-4096) || text.Length != Math.Min(16384,canonicalLength-origin) ||
            offset-origin < 0 || offset-origin+length > text.Length ||
            !text.AsSpan((int)(offset-origin),length).SequenceEqual(content.AsSpan())) return false;
        var start = (int)offset;
        var end = start+length;
        if (start > origin && char.IsLowSurrogate(text[start-origin])) start++;
        if (end < origin+text.Length && end > start && char.IsHighSurrogate(text[end-origin-1])) end--;
        return SqlCorpusRetrievalReader.BuildDisclosureWindow(text,origin,start,end,
            origin == 0,origin+text.Length == canonicalLength,out _) is not null;
    }

    internal static TextChunkWindowEligibilityEntity Bind(TextChunkEntity chunk, ArtifactEntity artifact,
        DateTimeOffset now, string ruleVersion = RuleVersion) => new()
    {
        TextChunk = chunk, TextChunkId = chunk.Id, ArtifactId = artifact.Id,
        CanonicalSourceRevision = artifact.SourceRevision, CanonicalContentHash = artifact.ContentHash,
        CanonicalLength = artifact.SearchText.Length, ChunkSourceRevision = chunk.SourceRevision,
        ChunkContentHash = chunk.ContentHash, StartOffset = chunk.StartOffset, Length = chunk.Length,
        RuleVersion = ruleVersion, Available = Evaluate(artifact.SearchText,chunk.StartOffset,chunk.Length,chunk.Content),
        CalculatedAtUtc = now
    };
}
