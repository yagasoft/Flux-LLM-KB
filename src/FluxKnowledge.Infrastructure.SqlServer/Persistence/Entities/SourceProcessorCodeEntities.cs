namespace FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;

public sealed class SourceProcessorCodeDocumentEntity
{
    public Guid SourceProcessorBranchId { get; set; }
    public Guid SourceRevisionId { get; set; }
    public string RetainedArtifactSha256 { get; set; } = string.Empty;
    public string DescriptorFingerprint { get; set; } = string.Empty;
    public string ParserFingerprint { get; set; } = string.Empty;
    public string HandlerImplementationId { get; set; } = string.Empty;
    public long LeaseGeneration { get; set; }
    public int DecodedCharacterCount { get; set; }
    public int LineCount { get; set; }
    public int SymbolCount { get; set; }
    public int ReferenceCount { get; set; }
    public int DiagnosticsCount { get; set; }
    public int WithheldSymbolCount { get; set; }
    public int WithheldReferenceCount { get; set; }
    public int WithheldDiagnosticCount { get; set; }
    public int ReceiptDiagnosticCodeCount { get; set; }
    public string DocumentFingerprint { get; set; } = string.Empty;
    public string CompletionFingerprint { get; set; } = string.Empty;
}

public sealed class SourceProcessorCodeSymbolEntity
{
    public Guid DocumentId { get; set; }
    public int Ordinal { get; set; }
    public int DeclarationKindCode { get; set; }
    public string LocalName { get; set; } = string.Empty;
    public string QualifiedName { get; set; } = string.Empty;
    public string RenderedSignature { get; set; } = string.Empty;
    public string Modifiers { get; set; } = string.Empty;
    public int LexicalParentOrdinal { get; set; }
    public int SpanStartUtf16 { get; set; }
    public int SpanLengthUtf16 { get; set; }
    public string SymbolFingerprint { get; set; } = string.Empty;
}

public sealed class SourceProcessorCodeReferenceEntity
{
    public Guid DocumentId { get; set; }
    public int Ordinal { get; set; }
    public int RelationshipKindCode { get; set; }
    public int? SourceSymbolOrdinal { get; set; }
    public string TargetDisplay { get; set; } = string.Empty;
    public int SpanStartUtf16 { get; set; }
    public int SpanLengthUtf16 { get; set; }
    public string ReferenceFingerprint { get; set; } = string.Empty;
}

public sealed class SourceProcessorCodeDiagnosticEntity
{
    public Guid DocumentId { get; set; }
    public int Ordinal { get; set; }
    public string DiagnosticId { get; set; } = string.Empty;
    public byte Severity { get; set; }
    public int SpanStartUtf16 { get; set; }
    public int SpanLengthUtf16 { get; set; }
    public string Representation { get; set; } = string.Empty;
    public string? ScannedMessage { get; set; }
    public string? WithheldReason { get; set; }
    public string DiagnosticFingerprint { get; set; } = string.Empty;
}

public sealed class SourceProcessorCodeCompletionReceiptEntity
{
    public Guid SourceProcessorBranchId { get; set; }
    public Guid SourceProcessorAttemptId { get; set; }
    public Guid SourceRevisionId { get; set; }
    public int ActivityKind { get; set; }
    public string ProcessorVersion { get; set; } = string.Empty;
    public string DescriptorFingerprint { get; set; } = string.Empty;
    public string ParserFingerprint { get; set; } = string.Empty;
    public string RetainedArtifactSha256 { get; set; } = string.Empty;
    public string HandlerImplementationId { get; set; } = string.Empty;
    public string OutcomeCode { get; set; } = string.Empty;
    public Guid? DocumentId { get; set; }
    public string? DocumentFingerprint { get; set; }
    public string CompletionFingerprint { get; set; } = string.Empty;
    public int WithheldSymbolCount { get; set; }
    public int WithheldReferenceCount { get; set; }
    public int WithheldDiagnosticCount { get; set; }
    public int BlockedDiagnosticsCount { get; set; }
    public int ReceiptDiagnosticCodeCount { get; set; }
    public string ReceiptDiagnosticCodesWire { get; set; } = string.Empty;
    public DateTimeOffset CreatedAtUtc { get; set; }
}

public sealed class SourceProcessorCodeBlockedDiagnosticEntity
{
    public Guid SourceProcessorBranchId { get; set; }
    public Guid SourceProcessorAttemptId { get; set; }
    public int Ordinal { get; set; }
    public string DiagnosticId { get; set; } = string.Empty;
    public byte Severity { get; set; }
    public int SpanStartUtf16 { get; set; }
    public int SpanLengthUtf16 { get; set; }
    public string Representation { get; set; } = string.Empty;
    public string? ScannedMessage { get; set; }
    public string? WithheldReason { get; set; }
    public string BlockedDiagnosticFingerprint { get; set; } = string.Empty;
}
