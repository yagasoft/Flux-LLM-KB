namespace FluxKnowledge.Application.Visibility;

/// <summary>Bounded result for a trusted-local retained-derived disclosure.</summary>
public sealed record LocalDisclosureResult(string? Value, bool Withheld, string? ReasonCode);

/// <summary>Classifies the retained-derived field being considered for trusted-local disclosure.</summary>
public enum LocalDisclosureKind
{
    RetainedDetail,
    CodeExcerpt,
    Symbol,
    Reference,
    Diagnostic,
    AuditEvidence
}
