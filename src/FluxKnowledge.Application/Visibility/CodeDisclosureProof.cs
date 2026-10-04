using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace FluxKnowledge.Application.Visibility;

public enum CodeDisclosureProofState { Ready, Unsupported, Invalid }
public enum CodeDisclosureSpanKind { StructuralBrace, Protected }

public sealed record CodeDisclosureSpan(int Start, int End, CodeDisclosureSpanKind Kind, string Checksum);

/// <summary>Application-owned derived evidence for the exact canonical UTF-16 representation.</summary>
public sealed record CodeDisclosureProof(
    Guid ArtifactId, string CanonicalHash, int CanonicalLength, string Fingerprint,
    CodeDisclosureProofState State, IReadOnlyList<CodeDisclosureSpan> Spans, string Checksum)
{
    public CodeDisclosureWindow Window(int start, int length) =>
        new(ArtifactId, CanonicalHash, CanonicalLength, Fingerprint, State, Spans.Count,
            Spans.Count, start, length, Spans.Where(span => span.Start < (long)start + length && span.End > start).ToArray());
}

/// <summary>Internal bounded projection; never accepted from a transport request or source metadata.</summary>
public sealed record CodeDisclosureWindow(
    Guid ArtifactId, string CanonicalHash, int CanonicalLength, string Fingerprint,
    CodeDisclosureProofState State, int SpanCount, int PersistedSpanCount,
    int Start, int Length, IReadOnlyList<CodeDisclosureSpan> Spans)
{
    public CodeDisclosureWindow Slice(int start, int length) => this with
    {
        Start = start, Length = length,
        Spans = Spans.Where(span => span.Start < (long)start + length && span.End > start).ToArray()
    };

    public bool IsValid(string value, int headerLength)
    {
        if (ArtifactId == Guid.Empty || CanonicalHash.Length != 64 ||
            Fingerprint != CodeDisclosureIntegrity.Fingerprint || State != CodeDisclosureProofState.Ready ||
            SpanCount < 0 || PersistedSpanCount != SpanCount || SpanCount < Spans.Count ||
            Start < 0 || Length < 0 || (long)Start + Length > CanonicalLength ||
            headerLength < 0 || headerLength > value.Length || value.Length - headerLength != Length)
            return false;
        CodeDisclosureSpan? previous = null;
        foreach (var span in Spans)
        {
            if (span.Start < 0 || span.End <= span.Start || span.End > CanonicalLength ||
                span.Start >= (long)Start + Length || span.End <= Start ||
                !Enum.IsDefined(span.Kind) ||
                previous is not null && Compare(previous, span) >= 0 ||
                span.Checksum != CodeDisclosureIntegrity.SpanChecksum(ArtifactId, CanonicalHash, Fingerprint,
                    span.Start, span.End, span.Kind))
                return false;
            if (span.Kind == CodeDisclosureSpanKind.StructuralBrace &&
                (span.End != span.Start + 1 || value[headerLength + span.Start - Start] != '{'))
                return false;
            previous = span;
        }
        return true;
    }

    public static int Compare(CodeDisclosureSpan left, CodeDisclosureSpan right)
    {
        var start = left.Start.CompareTo(right.Start);
        if (start != 0) return start;
        var end = left.End.CompareTo(right.End);
        return end != 0 ? end : left.Kind.CompareTo(right.Kind);
    }
}

public static class CodeDisclosureIntegrity
{
    public const string Fingerprint = "canonical-csharp14-roslyn5-disclosure-v1";
    public static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    public static string SpanChecksum(Guid artifact, string hash, string fingerprint,
        int start, int end, CodeDisclosureSpanKind kind) => Hash(string.Create(CultureInfo.InvariantCulture,
            $"{artifact:D}\n{hash}\n{fingerprint}\n{start}\n{end}\n{(int)kind}"));
    public static string ProofChecksum(Guid artifact, string hash, string fingerprint,
        CodeDisclosureProofState state, int length, IReadOnlyList<CodeDisclosureSpan> spans) =>
        Hash(string.Create(CultureInfo.InvariantCulture,
            $"{artifact:D}\n{hash}\n{fingerprint}\n{(int)state}\n{length}\n{spans.Count}\n") +
            string.Join('\n', spans.Select(span => span.Checksum)));
}
