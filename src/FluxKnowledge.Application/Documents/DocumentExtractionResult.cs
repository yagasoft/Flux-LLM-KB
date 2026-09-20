namespace FluxKnowledge.Application.Documents;

/// <summary>Strict UTF-8 document text accepted by the normal pipeline after bounded extraction.</summary>
public sealed record DocumentExtractionResult(
    string Text,
    bool IsComplete,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<DocumentExtractedPage>? Pages = null)
{
    /// <summary>
    /// Page-level native coverage captured while parsing the retained document.  OCR may be
    /// requested only for pages explicitly marked as uncovered; this prevents duplicate text
    /// from a mixed native/scanned PDF becoming corpus content.
    /// </summary>
    public IReadOnlyList<DocumentExtractedPage> Pages { get; init; } = Pages?.ToArray() ?? [];
}

public sealed record DocumentExtractedPage(
    int PageIndex,
    string Text,
    bool RequiresOcr);
