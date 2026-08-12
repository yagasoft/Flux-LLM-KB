using FluxKnowledge.Domain.Sources;

namespace FluxKnowledge.Application.Sources;

/// <summary>Trusted-local, read-only C# facts for one checksum-verified retained branch.</summary>
public sealed record LocalRetainedCsharpCodeDetailProjection(
    Guid BranchId,
    SourceRevisionId SourceRevisionId,
    string LocalPath,
    string ArtifactHash,
    long ArtifactByteLength,
    string OutcomeCode,
    string CompletionFingerprint,
    string? DocumentFingerprint,
    int WithheldSymbolCount,
    int WithheldReferenceCount,
    int WithheldDiagnosticCount,
    IReadOnlyList<LocalRetainedCsharpSymbolProjection> Symbols,
    IReadOnlyList<LocalRetainedCsharpReferenceProjection> References,
    IReadOnlyList<LocalRetainedCsharpDiagnosticProjection> Diagnostics)
{
    /// <summary>Total persisted symbol facts; a continuation is present when this page is incomplete.</summary>
    public int PersistedSymbolCount { get; init; }

    /// <summary>Total persisted relationship facts; a continuation is present when this page is incomplete.</summary>
    public int PersistedReferenceCount { get; init; }

    /// <summary>Total persisted parser diagnostics; a continuation is present when this page is incomplete.</summary>
    public int PersistedDiagnosticCount { get; init; }

    /// <summary>Exclusive ordinal cursor for the next bounded symbol page, if any.</summary>
    public int? NextSymbolOrdinal { get; init; }

    /// <summary>Exclusive ordinal cursor for the next bounded relationship page, if any.</summary>
    public int? NextReferenceOrdinal { get; init; }

    /// <summary>Exclusive ordinal cursor for the next bounded parser-diagnostic page, if any.</summary>
    public int? NextDiagnosticOrdinal { get; init; }
}

/// <summary>Independent exclusive cursors for a bounded trusted-local C# fact page.</summary>
public sealed record LocalRetainedCsharpCodePageRequest(
    int? SymbolAfterOrdinal,
    int? ReferenceAfterOrdinal,
    int? DiagnosticAfterOrdinal)
{
    public static LocalRetainedCsharpCodePageRequest First { get; } = new(null, null, null);
}

/// <summary>Bounded trusted-local C# search result. It is never an export projection.</summary>
public sealed record LocalRetainedCsharpCodeSearchProjection(
    Guid BranchId,
    string LocalPath,
    string ArtifactHash,
    IReadOnlyList<LocalRetainedCsharpSymbolProjection> Symbols)
{
    /// <summary>Exclusive ordinal cursor for further matching symbol facts, if this search row is bounded.</summary>
    public int? NextSymbolOrdinal { get; init; }

    /// <summary>Relationship facts matching the same trusted-local search query.</summary>
    public IReadOnlyList<LocalRetainedCsharpReferenceProjection> References { get; init; } = [];

    /// <summary>Exclusive ordinal cursor for further matching relationship facts, if this search row is bounded.</summary>
    public int? NextReferenceOrdinal { get; init; }
}

/// <summary>
/// One versioned, query-bound opaque continuation over matching persisted C# fact rows.
/// The token contains no source text, local path, parser diagnostic or credential material.
/// </summary>
public sealed record LocalRetainedCsharpCodeSearchCursor(string Token);

/// <summary>A fixed safe failure for malformed, tampered, cross-query or stale C# search continuations.</summary>
public sealed class LocalRetainedCsharpCodeSearchCursorException()
    : ArgumentException("The retained C# search continuation is invalid.")
{
    public const string ReasonCode = "retained-csharp-code-search-cursor-invalid";
}

/// <summary>Persisted C# fact family used only to order a trusted-local search continuation.</summary>
public enum LocalRetainedCsharpCodeSearchFactKind : byte
{
    Symbol = 1,
    Reference = 2
}

/// <summary>Bounded trusted-local search request over actual persisted matching fact rows.</summary>
public sealed record LocalRetainedCsharpCodeSearchPageRequest(
    string Query,
    int Limit,
    LocalRetainedCsharpCodeSearchCursor? Cursor);

/// <summary>One bounded trusted-local C# search page and its explicit durable-fact continuation.</summary>
public sealed record LocalRetainedCsharpCodeSearchPage(
    IReadOnlyList<LocalRetainedCsharpCodeSearchProjection> Results,
    LocalRetainedCsharpCodeSearchCursor? NextCursor);

/// <summary>One persisted and secret-safe C# declaration fact.</summary>
public sealed record LocalRetainedCsharpSymbolProjection(
    int Ordinal,
    int DeclarationKindCode,
    string LocalName,
    string QualifiedName,
    string RenderedSignature,
    string Modifiers,
    int LexicalParentOrdinal,
    int SpanStartUtf16,
    int SpanLengthUtf16);

/// <summary>One persisted and secret-safe C# relationship fact.</summary>
public sealed record LocalRetainedCsharpReferenceProjection(
    int Ordinal,
    int RelationshipKindCode,
    int? SourceSymbolOrdinal,
    string TargetDisplay,
    int SpanStartUtf16,
    int SpanLengthUtf16);

/// <summary>One bounded parser diagnostic, including syntax-invalid blocked diagnostics.</summary>
public sealed record LocalRetainedCsharpDiagnosticProjection(
    int Ordinal,
    string DiagnosticId,
    int SeverityCode,
    int SpanStartUtf16,
    int SpanLengthUtf16,
    string? Message,
    bool Withheld,
    string? WithheldReason,
    bool IsBlocked);
