using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Visibility;
using FluxKnowledge.Domain.Sources;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace FluxKnowledge.Application.Sources;

/// <summary>
/// Deterministic, syntax-only processor for checksum-verified retained C# bytes.
/// This type remains deliberately unregistered until Task 6 supplies the durable
/// schema, fenced writer and readiness gate.
/// </summary>
public sealed class RetainedCsharpCodeProcessor(
    IRetainedSourceReader retainedSourceReader,
    ILocalPrivateContentDisclosure disclosure,
    IRetainedCsharpCompilerServiceRegistrationProbe? compilerServiceRegistrationProbe = null)
{
    public const string ProcessorKind = "retained-csharp-code";
    public const string ProcessorVersion = "phase-5-retained-csharp-code-v1";
    public const string HandlerImplementationId = "retained-csharp-roslyn-syntax-v1";
    public const int MaximumClaimBatchSize = 8;
    public const int MaximumInputBytes = 4 * 1024 * 1024;
    public const int MaximumDecodedUtf16CodeUnits = 4_000_000;
    public const int MaximumSyntaxNodes = 200_000;
    public const int MaximumNestingDepth = 256;
    public const int MaximumSymbols = 20_000;
    public const int MaximumReferences = 100_000;
    public const int MaximumIdentifierUtf16CodeUnits = 1_024;
    public const int MaximumSignatureUtf16CodeUnits = 4_096;
    public const int MaximumDiagnostics = 256;
    public const int MaximumDiagnosticMessageUtf16CodeUnits = 1_024;

    private const string WithheldReason = "secret-content-withheld";
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly CSharpParseOptions ParseOptions = new(
        LanguageVersion.CSharp14,
        DocumentationMode.None,
        SourceCodeKind.Regular);
    private static readonly string[] ModifierOrder =
    [
        "public", "protected", "internal", "private", "file", "new", "static",
        "abstract", "sealed", "virtual", "override", "readonly", "required",
        "unsafe", "extern", "partial", "async", "ref", "in", "out", "scoped",
        "const", "volatile"
    ];
    private static readonly string CanonicalDescriptorWireRecord = BuildDescriptorWireRecord();
    private static readonly string CanonicalDescriptorFingerprint = Hash(CanonicalDescriptorWireRecord);
    private static readonly string CanonicalParserWireRecord = BuildParserWireRecord();
    private static readonly string CanonicalParserFingerprint = Hash(CanonicalParserWireRecord);

    public static readonly SourceCapabilityDescriptor Capability = new(
        new Guid("08dd66fb-6502-4b31-a4a5-51e8cc66f916"),
        ProcessorKind,
        ProcessorVersion,
        ExecutionClass.InProcess,
        CanonicalDescriptorFingerprint,
        SourceActivityKind.CodeParsing,
        "AcceptedUtf8Text",
        "retained:csharp-code-facts-v1");

    public static string DescriptorWireRecord => CanonicalDescriptorWireRecord;
    public static string ParserWireRecord => CanonicalParserWireRecord;
    public static string ParserFingerprint => CanonicalParserFingerprint;

    public static string ComputeDescriptorFingerprint() => CanonicalDescriptorFingerprint;

    public RetainedCsharpParserPreflight Preflight()
    {
        var version = typeof(CSharpSyntaxTree).Assembly.GetName().Version?.ToString() ?? string.Empty;
        return Preflight(
            HandlerImplementationId,
            version,
            Enum.IsDefined(LanguageVersion.CSharp14),
            compilerServiceRegistrationProbe?.HasForbiddenCompilerServicesRegistered() ?? false,
            Capability.ProcessorFingerprint);
    }

    public static RetainedCsharpParserPreflight Preflight(
        string handlerImplementationId,
        string roslynAssemblyVersion,
        bool languageVersionAvailable,
        bool forbiddenCompilerServicesRegistered,
        string descriptorFingerprint)
    {
        var available =
            string.Equals(handlerImplementationId, HandlerImplementationId, StringComparison.Ordinal) &&
            string.Equals(roslynAssemblyVersion, "5.0.0.0", StringComparison.Ordinal) &&
            languageVersionAvailable &&
            !forbiddenCompilerServicesRegistered &&
            string.Equals(descriptorFingerprint, ComputeDescriptorFingerprint(), StringComparison.Ordinal);
        return new RetainedCsharpParserPreflight(
            available,
            available ? null : "processor-parser-unavailable",
            roslynAssemblyVersion);
    }

    public async ValueTask<RetainedCsharpCodeCompletion> ProcessAsync(
        RetainedCsharpCodeClaim claim,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(claim);
        cancellationToken.ThrowIfCancellationRequested();

        RetainedSourceBytes retained;
        try
        {
            retained = await retainedSourceReader.ReadBytesAsync(claim.SourceRevisionId, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return Blocked(claim, "retained-artifact-missing");
        }
        catch (UnauthorizedAccessException)
        {
            return Blocked(claim, "retained-artifact-path-invalid");
        }
        catch (InvalidDataException)
        {
            return Blocked(claim, "retained-artifact-checksum-invalid");
        }

        cancellationToken.ThrowIfCancellationRequested();
        var actualHash = Convert.ToHexStringLower(SHA256.HashData(retained.Bytes));
        if (retained.SourceRevisionId != claim.SourceRevisionId ||
            retained.ByteLength < 0 ||
            retained.ByteLength != retained.Bytes.LongLength ||
            !string.Equals(retained.ContentSha256, actualHash, StringComparison.Ordinal) ||
            !string.Equals(retained.ContentSha256, claim.InputSha256, StringComparison.Ordinal))
        {
            return Blocked(claim, "retained-artifact-checksum-invalid");
        }

        if (!Preflight().IsAvailable)
        {
            return Blocked(claim, "processor-parser-unavailable");
        }

        if (retained.ByteLength > MaximumInputBytes)
        {
            return Blocked(claim, "csharp-code-input-too-large");
        }

        string text;
        try
        {
            var bytes = retained.Bytes.AsSpan();
            if (bytes.StartsWith(new byte[] { 0xef, 0xbb, 0xbf }))
            {
                bytes = bytes[3..];
            }
            text = StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return Blocked(claim, "csharp-code-input-not-utf8");
        }

        if (text.Length > MaximumDecodedUtf16CodeUnits)
        {
            return Blocked(claim, "csharp-code-text-limit");
        }

        cancellationToken.ThrowIfCancellationRequested();
        var tree = CSharpSyntaxTree.ParseText(text, ParseOptions, cancellationToken: cancellationToken);
        var diagnostics = tree.GetDiagnostics(cancellationToken)
            .OrderBy(value => value.Location.SourceSpan.Start)
            .ThenBy(value => value.Location.SourceSpan.Length)
            .ThenBy(value => value.Id, StringComparer.Ordinal)
            .ToArray();

        if (diagnostics.Any(value => value.Severity == DiagnosticSeverity.Error))
        {
            try
            {
                return SyntaxInvalid(claim, diagnostics.Take(MaximumDiagnostics).ToArray(), cancellationToken);
            }
            catch (SecretScanFailedException)
            {
                return Blocked(claim, "csharp-code-secret-scan-failed");
            }
        }

        var root = tree.GetRoot(cancellationToken);
        var nodeCount = 0;
        foreach (var node in root.DescendantNodesAndSelf(descendIntoTrivia: false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (++nodeCount > MaximumSyntaxNodes)
            {
                return Blocked(claim, "csharp-code-node-limit");
            }
            if (Depth(node) > MaximumNestingDepth)
            {
                return Blocked(claim, "csharp-code-depth-limit");
            }
        }

        // Bound every identifier-bearing syntactic position before collecting any
        // declaration facts or references. DescendantTokens is source ordered.
        foreach (var token in root.DescendantTokens(descendIntoTrivia: false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (token.IsKind(SyntaxKind.IdentifierToken) && token.ValueText.Length > MaximumIdentifierUtf16CodeUnits)
            {
                return Blocked(claim, "csharp-code-identifier-limit");
            }
        }

        var rawSymbols = new List<RawSymbol>();
        var declarationContexts = new Dictionary<SyntaxNode, DeclarationContext>();
        foreach (var node in root.DescendantNodesAndSelf(descendIntoTrivia: false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryDescribeDeclaration(node, out var description))
            {
                continue;
            }

            var signature = RenderSignature(node);
            if (signature.Length > MaximumSignatureUtf16CodeUnits)
            {
                return Blocked(claim, "csharp-code-signature-limit");
            }
            if (rawSymbols.Count + 1 > MaximumSymbols)
            {
                return Blocked(claim, "csharp-code-symbol-limit");
            }

            var parentRawOrdinal = NearestDeclaration(node.Parent, declarationContexts)?.RawOrdinal ?? -1;
            var qualifiedName = QualifiedName(node, description.LocalName, declarationContexts);
            var raw = new RawSymbol(
                rawSymbols.Count,
                description.KindCode,
                description.Kind!,
                description.LocalName,
                qualifiedName,
                signature,
                CanonicalModifiers(node),
                parentRawOrdinal,
                node.SpanStart,
                node.Span.Length);
            rawSymbols.Add(raw);
            declarationContexts[node] = new DeclarationContext(raw.RawOrdinal, qualifiedName);
        }

        var rawReferences = new List<RawReference>();
        var preorder = 0;
        foreach (var node in root.DescendantNodesAndSelf(descendIntoTrivia: false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var nodeReferences = DescribeReferences(node, declarationContexts, preorder++)
                .OrderBy(value => value.KindCode)
                .ThenBy(value => value.SpanStart)
                .ThenBy(value => value.SpanLength)
                .ThenBy(value => value.RawKind)
                .ThenBy(value => value.Preorder);
            foreach (var reference in nodeReferences)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (rawReferences.Count + 1 > MaximumReferences)
                {
                    return Blocked(claim, "csharp-code-reference-limit");
                }
                rawReferences.Add(reference);
            }
        }

        if (diagnostics.Length > MaximumDiagnostics)
        {
            return Blocked(claim, "csharp-code-diagnostic-limit");
        }

        try
        {
            return CompleteSuccess(
                claim,
                rawSymbols,
                rawReferences,
                diagnostics,
                text.Length,
                tree.GetText(cancellationToken).Lines.Count,
                cancellationToken);
        }
        catch (SecretScanFailedException)
        {
            return Blocked(claim, "csharp-code-secret-scan-failed");
        }
    }

    private RetainedCsharpCodeCompletion CompleteSuccess(
        RetainedCsharpCodeClaim claim,
        IReadOnlyList<RawSymbol> rawSymbols,
        IReadOnlyList<RawReference> rawReferences,
        IReadOnlyList<Diagnostic> diagnostics,
        int decodedCharacterCount,
        int lineCount,
        CancellationToken cancellationToken)
    {
        var cleanSymbols = new bool[rawSymbols.Count];
        var withheldSymbols = 0;
        for (var index = 0; index < rawSymbols.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var raw = rawSymbols[index];
            cleanSymbols[index] = ScanFact(
                cancellationToken,
                LocalDisclosureKind.Symbol,
                raw.LocalName,
                raw.QualifiedName,
                raw.RenderedSignature,
                raw.Modifiers);
            if (!cleanSymbols[index])
            {
                withheldSymbols++;
            }
        }

        var emittedSymbolOrdinals = new Dictionary<int, int>();
        for (var rawOrdinal = 0; rawOrdinal < cleanSymbols.Length; rawOrdinal++)
        {
            if (cleanSymbols[rawOrdinal])
            {
                emittedSymbolOrdinals[rawOrdinal] = emittedSymbolOrdinals.Count;
            }
        }

        var documentFingerprint = ComputeDocumentFingerprint(
            claim.SourceRevisionId,
            claim.InputSha256,
            ParserFingerprint);
        var symbols = new List<RetainedCsharpCodeSymbol>(emittedSymbolOrdinals.Count);
        foreach (var raw in rawSymbols)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!cleanSymbols[raw.RawOrdinal])
            {
                continue;
            }
            var ordinal = emittedSymbolOrdinals[raw.RawOrdinal];
            var parent = NearestEmittedParent(
                raw.ParentRawOrdinal,
                rawSymbols,
                emittedSymbolOrdinals,
                includeSelf: true);
            var fingerprint = ComputeSymbolFingerprint(
                documentFingerprint,
                ordinal,
                raw.KindCode,
                raw.LocalName,
                raw.QualifiedName,
                raw.RenderedSignature,
                raw.Modifiers,
                parent,
                raw.SpanStart,
                raw.SpanLength);
            symbols.Add(new RetainedCsharpCodeSymbol(
                ordinal,
                raw.KindCode,
                raw.Kind,
                raw.LocalName,
                raw.QualifiedName,
                raw.RenderedSignature,
                raw.Modifiers,
                parent,
                raw.SpanStart,
                raw.SpanLength,
                fingerprint));
        }

        var references = new List<RetainedCsharpCodeReference>();
        var withheldReferences = 0;
        foreach (var raw in rawReferences)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!ScanFact(cancellationToken, LocalDisclosureKind.Reference, raw.Kind, raw.TargetDisplay))
            {
                withheldReferences++;
                continue;
            }
            int? nullableSourceOrdinal = raw.SourceRawSymbolOrdinal < 0
                ? null
                : NearestEmittedParent(raw.SourceRawSymbolOrdinal, rawSymbols, emittedSymbolOrdinals, includeSelf: true);
            if (nullableSourceOrdinal < 0)
            {
                nullableSourceOrdinal = null;
            }
            var ordinal = references.Count;
            references.Add(new RetainedCsharpCodeReference(
                ordinal,
                raw.KindCode,
                raw.Kind,
                nullableSourceOrdinal,
                raw.TargetDisplay,
                raw.SpanStart,
                raw.SpanLength,
                ComputeReferenceFingerprint(
                    documentFingerprint,
                    ordinal,
                    raw.KindCode,
                    nullableSourceOrdinal,
                    raw.TargetDisplay,
                    raw.SpanStart,
                    raw.SpanLength)));
        }

        var retainedDiagnostics = new List<RetainedCsharpCodeDiagnostic>(diagnostics.Count);
        foreach (var diagnostic in diagnostics)
        {
            cancellationToken.ThrowIfCancellationRequested();
            retainedDiagnostics.Add(ToDiagnostic(
                documentFingerprint,
                retainedDiagnostics.Count,
                diagnostic,
                cancellationToken));
        }
        var withheldDiagnostics = retainedDiagnostics.Count(value => value.Withheld);
        var completionFingerprint = ComputeCompletionFingerprint(
            documentFingerprint,
            ParserFingerprint,
            symbols.Select(value => value.SymbolFingerprint),
            references.Select(value => value.ReferenceFingerprint),
            retainedDiagnostics.Select(value => value.DiagnosticFingerprint),
            withheldSymbols,
            withheldReferences,
            withheldDiagnostics,
            retainedDiagnostics.Select(value => value.DiagnosticId));
        return new RetainedCsharpCodeCompletion(
            claim.BranchId,
            claim.SourceRevisionId,
            claim.AttemptId,
            claim.InputSha256,
            claim.LeaseOwner,
            claim.LeaseGeneration,
            claim.LeaseExpiresAtUtc,
            ProcessorVersion,
            Capability.ProcessorFingerprint,
            ParserFingerprint,
            "success",
            documentFingerprint,
            completionFingerprint,
            null,
            symbols,
            references,
            retainedDiagnostics,
            [],
            withheldSymbols,
            withheldReferences,
            withheldDiagnostics,
            retainedDiagnostics.Select(value => value.DiagnosticId).ToArray())
        {
            DecodedCharacterCount = decodedCharacterCount,
            LineCount = lineCount
        };
    }

    private RetainedCsharpCodeCompletion SyntaxInvalid(
        RetainedCsharpCodeClaim claim,
        IReadOnlyList<Diagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        var blocked = new List<RetainedCsharpBlockedDiagnostic>(diagnostics.Count);
        foreach (var diagnostic in diagnostics)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var message = Bound(
                diagnostic.GetMessage(CultureInfo.InvariantCulture),
                MaximumDiagnosticMessageUtf16CodeUnits);
            var scanned = ScanValue(message, LocalDisclosureKind.Diagnostic, cancellationToken);
            var span = diagnostic.Location.SourceSpan;
            var ordinal = blocked.Count;
            blocked.Add(new RetainedCsharpBlockedDiagnostic(
                claim.BranchId,
                claim.AttemptId,
                ordinal,
                diagnostic.Id,
                (int)diagnostic.Severity,
                span.Start,
                span.Length,
                scanned.Value,
                scanned.Withheld,
                scanned.ReasonCode,
                ComputeBlockedDiagnosticFingerprint(
                    claim.SourceRevisionId,
                    claim.InputSha256,
                    ordinal,
                    diagnostic.Id,
                    (int)diagnostic.Severity,
                    span.Start,
                    span.Length,
                    scanned.Value,
                    scanned.ReasonCode)));
        }
        var withheld = blocked.Count(value => value.Withheld);
        var fingerprint = ComputeBlockedCompletionFingerprint(
            claim.SourceRevisionId,
            claim.InputSha256,
            blocked.Select(value => value.BlockedDiagnosticFingerprint),
            withheld,
            blocked.Select(value => value.DiagnosticId));
        return new RetainedCsharpCodeCompletion(
            claim.BranchId,
            claim.SourceRevisionId,
            claim.AttemptId,
            claim.InputSha256,
            claim.LeaseOwner,
            claim.LeaseGeneration,
            claim.LeaseExpiresAtUtc,
            ProcessorVersion,
            Capability.ProcessorFingerprint,
            ParserFingerprint,
            "csharp-code-syntax-invalid",
            null,
            null,
            fingerprint,
            [],
            [],
            [],
            blocked,
            0,
            0,
            withheld,
            blocked.Select(value => value.DiagnosticId).ToArray());
    }

    private static RetainedCsharpCodeCompletion Blocked(
        RetainedCsharpCodeClaim claim,
        string outcomeCode) => new(
            claim.BranchId,
            claim.SourceRevisionId,
            claim.AttemptId,
            claim.InputSha256,
            claim.LeaseOwner,
            claim.LeaseGeneration,
            claim.LeaseExpiresAtUtc,
            ProcessorVersion,
            Capability.ProcessorFingerprint,
            ParserFingerprint,
            outcomeCode,
            null,
            null,
            null,
            [],
            [],
            [],
            [],
            0,
            0,
            0,
            []);

    private RetainedCsharpCodeDiagnostic ToDiagnostic(
        string documentFingerprint,
        int ordinal,
        Diagnostic diagnostic,
        CancellationToken cancellationToken)
    {
        var message = Bound(
            diagnostic.GetMessage(CultureInfo.InvariantCulture),
            MaximumDiagnosticMessageUtf16CodeUnits);
        var scanned = ScanValue(message, LocalDisclosureKind.Diagnostic, cancellationToken);
        var span = diagnostic.Location.SourceSpan;
        return new RetainedCsharpCodeDiagnostic(
            ordinal,
            diagnostic.Id,
            (int)diagnostic.Severity,
            span.Start,
            span.Length,
            scanned.Value,
            scanned.Withheld,
            scanned.ReasonCode,
            ComputeDiagnosticFingerprint(
                documentFingerprint,
                ordinal,
                diagnostic.Id,
                (int)diagnostic.Severity,
                span.Start,
                span.Length,
                scanned.Value,
                scanned.ReasonCode));
    }

    private bool ScanFact(
        CancellationToken cancellationToken,
        LocalDisclosureKind kind,
        params string[] fields)
    {
        var withheld = false;
        foreach (var field in fields)
        {
            var result = ScanValue(field, kind, cancellationToken);
            withheld |= result.Withheld;
        }
        return !withheld;
    }

    private DisclosureResult ScanValue(
        string value,
        LocalDisclosureKind kind,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        LocalDisclosureResult result;
        try
        {
            result = disclosure.Evaluate(value, kind);
        }
        catch (Exception exception) when (exception is not OperationCanceledException and not OutOfMemoryException)
        {
            throw new SecretScanFailedException(exception);
        }
        cancellationToken.ThrowIfCancellationRequested();

        if (result.Withheld)
        {
            if (result.Value is not null || !string.Equals(result.ReasonCode, WithheldReason, StringComparison.Ordinal))
            {
                throw new SecretScanFailedException();
            }
            return new DisclosureResult(null, true, WithheldReason);
        }
        if (!string.Equals(result.Value, value, StringComparison.Ordinal) || result.ReasonCode is not null)
        {
            throw new SecretScanFailedException();
        }
        return new DisclosureResult(value, false, null);
    }

    private static IEnumerable<RawReference> DescribeReferences(
        SyntaxNode node,
        IReadOnlyDictionary<SyntaxNode, DeclarationContext> declarations,
        int preorder)
    {
        switch (node)
        {
            case UsingDirectiveSyntax usingDirective when usingDirective.Name is not null:
                yield return Reference(1, "using", usingDirective.Name, declarations, preorder);
                yield break;
            case BaseTypeSyntax baseType:
                var baseTypes = baseType.Parent?.ChildNodes().OfType<BaseTypeSyntax>().ToArray() ?? [];
                var index = Array.IndexOf(baseTypes, baseType);
                var isBaseType = baseType.Parent?.Parent is ClassDeclarationSyntax or RecordDeclarationSyntax && index == 0;
                yield return Reference(
                    isBaseType ? 2 : 3,
                    isBaseType ? "base-type" : "implemented-interface",
                    baseType.Type,
                    declarations,
                    preorder);
                yield break;
            case AttributeSyntax attribute:
                yield return Reference(4, "attribute", attribute.Name, declarations, preorder);
                yield break;
            case ObjectCreationExpressionSyntax creation:
                yield return Reference(5, "type-use", creation.Type, declarations, preorder);
                yield return Reference(6, "object-construction", creation.Type, declarations, preorder);
                yield break;
            case InvocationExpressionSyntax invocation:
                yield return Reference(7, "invocation", invocation.Expression, declarations, preorder);
                yield break;
            case TypeSyntax type when IsTypeUseRoot(type):
                yield return Reference(5, "type-use", type, declarations, preorder);
                yield break;
        }
    }

    private static RawReference Reference(
        int code,
        string kind,
        SyntaxNode displayNode,
        IReadOnlyDictionary<SyntaxNode, DeclarationContext> declarations,
        int preorder) => new(
            code,
            kind,
            NearestDeclaration(displayNode.Parent, declarations)?.RawOrdinal ?? -1,
            CanonicalSyntax(displayNode),
            displayNode.SpanStart,
            displayNode.Span.Length,
            displayNode.RawKind,
            preorder);

    private static bool IsTypeUseRoot(TypeSyntax type)
    {
        if (type.Parent is TypeSyntax)
        {
            return false;
        }
        if (type.Parent is BaseTypeSyntax or AttributeSyntax or UsingDirectiveSyntax or ObjectCreationExpressionSyntax)
        {
            return false;
        }
        return type.Parent switch
        {
            VariableDeclarationSyntax value => value.Type == type,
            ParameterSyntax value => value.Type == type,
            MethodDeclarationSyntax value => value.ReturnType == type,
            LocalFunctionStatementSyntax value => value.ReturnType == type,
            PropertyDeclarationSyntax value => value.Type == type,
            IndexerDeclarationSyntax value => value.Type == type,
            EventDeclarationSyntax value => value.Type == type,
            DelegateDeclarationSyntax value => value.ReturnType == type,
            OperatorDeclarationSyntax value => value.ReturnType == type,
            ConversionOperatorDeclarationSyntax value => value.Type == type,
            CastExpressionSyntax value => value.Type == type,
            ArrayCreationExpressionSyntax value => value.Type == type,
            StackAllocArrayCreationExpressionSyntax value => value.Type == type,
            TypeOfExpressionSyntax value => value.Type == type,
            SizeOfExpressionSyntax value => value.Type == type,
            DefaultExpressionSyntax value => value.Type == type,
            ForEachStatementSyntax value => value.Type == type,
            CatchDeclarationSyntax value => value.Type == type,
            TypeArgumentListSyntax => true,
            TypeConstraintSyntax value => value.Type == type,
            TupleElementSyntax value => value.Type == type,
            DeclarationPatternSyntax value => value.Type == type,
            RecursivePatternSyntax value => value.Type == type,
            TypePatternSyntax value => value.Type == type,
            FunctionPointerParameterSyntax value => value.Type == type,
            _ => false
        };
    }

    private static bool TryDescribeDeclaration(
        SyntaxNode node,
        out DeclarationDescription description)
    {
        description = node switch
        {
            NamespaceDeclarationSyntax value => new(1, "namespace", CanonicalSyntax(value.Name)),
            FileScopedNamespaceDeclarationSyntax value => new(1, "namespace", CanonicalSyntax(value.Name)),
            ClassDeclarationSyntax value => new(2, "class", value.Identifier.ValueText),
            StructDeclarationSyntax value => new(3, "struct", value.Identifier.ValueText),
            InterfaceDeclarationSyntax value => new(4, "interface", value.Identifier.ValueText),
            RecordDeclarationSyntax value when value.ClassOrStructKeyword.IsKind(SyntaxKind.StructKeyword) =>
                new(6, "record-struct", value.Identifier.ValueText),
            RecordDeclarationSyntax value => new(5, "record-class", value.Identifier.ValueText),
            EnumDeclarationSyntax value => new(7, "enum", value.Identifier.ValueText),
            DelegateDeclarationSyntax value => new(8, "delegate", value.Identifier.ValueText),
            ConstructorDeclarationSyntax => new(9, "constructor", ".ctor"),
            DestructorDeclarationSyntax => new(10, "destructor", ".dtor"),
            MethodDeclarationSyntax value => new(11, "method", value.Identifier.ValueText),
            OperatorDeclarationSyntax value => new(12, "operator", "operator " + value.OperatorToken.ValueText),
            ConversionOperatorDeclarationSyntax value =>
                new(13, "conversion-operator", "operator " + value.ImplicitOrExplicitKeyword.ValueText),
            PropertyDeclarationSyntax value => new(14, "property", value.Identifier.ValueText),
            IndexerDeclarationSyntax => new(15, "indexer", ".this"),
            EventDeclarationSyntax value => new(16, "event", value.Identifier.ValueText),
            VariableDeclaratorSyntax value when value.Parent?.Parent is EventFieldDeclarationSyntax =>
                new(16, "event", value.Identifier.ValueText),
            VariableDeclaratorSyntax value when value.Parent?.Parent is FieldDeclarationSyntax =>
                new(17, "field", value.Identifier.ValueText),
            EnumMemberDeclarationSyntax value => new(18, "enum-member", value.Identifier.ValueText),
            ParameterSyntax value => new(19, "parameter", value.Identifier.ValueText),
            LocalFunctionStatementSyntax value => new(20, "local-function", value.Identifier.ValueText),
            _ => default
        };
        return description.Kind is not null;
    }

    private static string QualifiedName(
        SyntaxNode node,
        string localName,
        IReadOnlyDictionary<SyntaxNode, DeclarationContext> declarations)
    {
        var parent = NearestDeclaration(node.Parent, declarations)?.QualifiedName ?? "global::";
        return node switch
        {
            BaseNamespaceDeclarationSyntax namespaceDeclaration =>
                AppendQualified(parent, CanonicalSyntax(namespaceDeclaration.Name)),
            TypeDeclarationSyntax type =>
                AppendQualified(parent, type.Identifier.ValueText + TypeParameters(type.TypeParameterList)),
            DelegateDeclarationSyntax value =>
                AppendQualified(parent, value.Identifier.ValueText + TypeParameters(value.TypeParameterList)),
            _ when ExplicitInterface(node) is { } explicitInterface =>
                AppendQualified(AppendQualified(parent, explicitInterface), localName),
            _ => AppendQualified(parent, localName)
        };
    }

    private static string? ExplicitInterface(SyntaxNode node)
    {
        ExplicitInterfaceSpecifierSyntax? specifier = node switch
        {
            MethodDeclarationSyntax value => value.ExplicitInterfaceSpecifier,
            PropertyDeclarationSyntax value => value.ExplicitInterfaceSpecifier,
            IndexerDeclarationSyntax value => value.ExplicitInterfaceSpecifier,
            EventDeclarationSyntax value => value.ExplicitInterfaceSpecifier,
            _ => null
        };
        return specifier is null ? null : CanonicalSyntax(specifier.Name);
    }

    private static string AppendQualified(string parent, string local) =>
        local.StartsWith(".", StringComparison.Ordinal)
            ? parent + local
            : parent.EndsWith("::", StringComparison.Ordinal)
                ? parent + local
                : parent + "." + local;

    private static string TypeParameters(TypeParameterListSyntax? parameters) =>
        parameters is null
            ? string.Empty
            : "[" + string.Join(',', parameters.Parameters.Select(value => value.Identifier.ValueText)) + "]";

    private static DeclarationContext? NearestDeclaration(
        SyntaxNode? node,
        IReadOnlyDictionary<SyntaxNode, DeclarationContext> declarations)
    {
        for (var current = node; current is not null; current = current.Parent)
        {
            if (declarations.TryGetValue(current, out var context))
            {
                return context;
            }
        }
        return null;
    }

    private static int NearestEmittedParent(
        int rawOrdinal,
        IReadOnlyList<RawSymbol> rawSymbols,
        IReadOnlyDictionary<int, int> emittedOrdinals,
        bool includeSelf = false)
    {
        var current = rawOrdinal;
        if (!includeSelf && current >= 0)
        {
            current = rawSymbols[current].ParentRawOrdinal;
        }
        while (current >= 0)
        {
            if (emittedOrdinals.TryGetValue(current, out var emitted))
            {
                return emitted;
            }
            current = rawSymbols[current].ParentRawOrdinal;
        }
        return -1;
    }

    private static int Depth(SyntaxNode node)
    {
        var depth = 0;
        for (var current = node.Parent; current is not null; current = current.Parent)
        {
            depth++;
        }
        return depth;
    }

    private static string RenderSignature(SyntaxNode node)
    {
        if (node is BaseNamespaceDeclarationSyntax namespaceDeclaration)
        {
            return "namespace " + CanonicalSyntax(namespaceDeclaration.Name);
        }
        if (node is VariableDeclaratorSyntax variable && variable.Parent is VariableDeclarationSyntax declaration)
        {
            var keyword = variable.Parent.Parent is EventFieldDeclarationSyntax ? "event " : string.Empty;
            return JoinPrefix(
                CanonicalModifiers(node),
                keyword + CanonicalSyntax(declaration.Type) + " " + variable.Identifier.ValueText);
        }
        if (node is EnumMemberDeclarationSyntax enumMember)
        {
            return enumMember.Identifier.ValueText;
        }

        var stripped = StripDeclaration(node);
        var value = CanonicalSyntax(stripped);
        if (value.EndsWith(';'))
        {
            value = value[..^1].TrimEnd();
        }
        return JoinPrefix(CanonicalModifiers(node), value);
    }

    private static SyntaxNode StripDeclaration(SyntaxNode node)
    {
        SyntaxNode stripped = node switch
        {
            ClassDeclarationSyntax value => value
                .WithAttributeLists(default)
                .WithModifiers(default)
                .WithMembers(default)
                .WithOpenBraceToken(default)
                .WithCloseBraceToken(default)
                .WithSemicolonToken(default),
            StructDeclarationSyntax value => value
                .WithAttributeLists(default)
                .WithModifiers(default)
                .WithMembers(default)
                .WithOpenBraceToken(default)
                .WithCloseBraceToken(default)
                .WithSemicolonToken(default),
            InterfaceDeclarationSyntax value => value
                .WithAttributeLists(default)
                .WithModifiers(default)
                .WithMembers(default)
                .WithOpenBraceToken(default)
                .WithCloseBraceToken(default)
                .WithSemicolonToken(default),
            RecordDeclarationSyntax value => value
                .WithAttributeLists(default)
                .WithModifiers(default)
                .WithMembers(default)
                .WithOpenBraceToken(default)
                .WithCloseBraceToken(default)
                .WithSemicolonToken(default),
            EnumDeclarationSyntax value => value
                .WithAttributeLists(default)
                .WithModifiers(default)
                .WithMembers(default)
                .WithOpenBraceToken(default)
                .WithCloseBraceToken(default)
                .WithSemicolonToken(default),
            DelegateDeclarationSyntax value => value
                .WithAttributeLists(default)
                .WithModifiers(default),
            ConstructorDeclarationSyntax value => value
                .WithAttributeLists(default)
                .WithModifiers(default)
                .WithInitializer(null)
                .WithBody(null)
                .WithExpressionBody(null)
                .WithSemicolonToken(default),
            DestructorDeclarationSyntax value => value
                .WithAttributeLists(default)
                .WithModifiers(default)
                .WithBody(null)
                .WithExpressionBody(null)
                .WithSemicolonToken(default),
            MethodDeclarationSyntax value => value
                .WithAttributeLists(default)
                .WithModifiers(default)
                .WithBody(null)
                .WithExpressionBody(null)
                .WithSemicolonToken(default),
            OperatorDeclarationSyntax value => value
                .WithAttributeLists(default)
                .WithModifiers(default)
                .WithBody(null)
                .WithExpressionBody(null)
                .WithSemicolonToken(default),
            ConversionOperatorDeclarationSyntax value => value
                .WithAttributeLists(default)
                .WithModifiers(default)
                .WithBody(null)
                .WithExpressionBody(null)
                .WithSemicolonToken(default),
            PropertyDeclarationSyntax value => value
                .WithAttributeLists(default)
                .WithModifiers(default)
                .WithAccessorList(null)
                .WithExpressionBody(null)
                .WithInitializer(null)
                .WithSemicolonToken(default),
            IndexerDeclarationSyntax value => value
                .WithAttributeLists(default)
                .WithModifiers(default)
                .WithAccessorList(null)
                .WithExpressionBody(null)
                .WithSemicolonToken(default),
            EventDeclarationSyntax value => value
                .WithAttributeLists(default)
                .WithModifiers(default)
                .WithAccessorList(null)
                .WithSemicolonToken(default),
            ParameterSyntax value => value
                .WithAttributeLists(default)
                .WithModifiers(default),
            LocalFunctionStatementSyntax value => value
                .WithAttributeLists(default)
                .WithModifiers(default)
                .WithBody(null)
                .WithExpressionBody(null)
                .WithSemicolonToken(default),
            _ => node
        };
        return new AttributeRemovingRewriter().Visit(stripped) ?? stripped;
    }

    private static string CanonicalModifiers(SyntaxNode node)
    {
        var modifiers = ModifierTokens(node)
            .Select(value => value.ValueText)
            .ToHashSet(StringComparer.Ordinal);
        return string.Join(' ', ModifierOrder.Where(modifiers.Contains));
    }

    private static SyntaxTokenList ModifierTokens(SyntaxNode node) => node switch
    {
        BaseTypeDeclarationSyntax value => value.Modifiers,
        DelegateDeclarationSyntax value => value.Modifiers,
        ConstructorDeclarationSyntax value => value.Modifiers,
        DestructorDeclarationSyntax value => value.Modifiers,
        MethodDeclarationSyntax value => value.Modifiers,
        OperatorDeclarationSyntax value => value.Modifiers,
        ConversionOperatorDeclarationSyntax value => value.Modifiers,
        PropertyDeclarationSyntax value => value.Modifiers,
        IndexerDeclarationSyntax value => value.Modifiers,
        EventDeclarationSyntax value => value.Modifiers,
        VariableDeclaratorSyntax value when value.Parent?.Parent is FieldDeclarationSyntax field => field.Modifiers,
        VariableDeclaratorSyntax value when value.Parent?.Parent is EventFieldDeclarationSyntax field => field.Modifiers,
        ParameterSyntax value => value.Modifiers,
        LocalFunctionStatementSyntax value => value.Modifiers,
        _ => default
    };

    private static string CanonicalSyntax(SyntaxNode node)
    {
        var trivia = node.DescendantTrivia(descendIntoTrivia: true).ToArray();
        var withoutTrivia = node.ReplaceTrivia(
            trivia,
            static (_, _) => default);
        return withoutTrivia
            .NormalizeWhitespace(indentation: string.Empty, eol: " ", elasticTrivia: false)
            .ToFullString()
            .Trim();
    }

    private static string JoinPrefix(string prefix, string value) =>
        prefix.Length == 0 ? value : prefix + " " + value;

    private static string Bound(string value, int maximum)
    {
        if (value.Length <= maximum)
        {
            return value;
        }

        var length = maximum;
        if (length > 0 &&
            char.IsHighSurrogate(value[length - 1]) &&
            char.IsLowSurrogate(value[length]))
        {
            length--;
        }
        return value[..length];
    }

    public static string DocumentWireRecord(
        SourceRevisionId sourceRevisionId,
        string sha256,
        string parserFingerprint) => Record(
            ("frame", Text("retained-csharp-document-v1")),
            ("source_revision_id", Text(sourceRevisionId.Value.ToString("N"))),
            ("retained_artifact_sha256", Text(sha256)),
            ("processor_version", Text(ProcessorVersion)),
            ("descriptor_fingerprint", Text(Capability.ProcessorFingerprint)),
            ("parser_fingerprint", Text(parserFingerprint)));

    public static string SymbolWireRecord(
        string documentFingerprint,
        int ordinal,
        int kindCode,
        string localName,
        string qualifiedName,
        string signature,
        string modifiers,
        int parent,
        int start,
        int length) => Record(
            ("frame", Text("retained-csharp-symbol-v1")),
            ("document_fingerprint", Text(documentFingerprint)),
            ("ordinal", UInt(ordinal)),
            ("declaration_kind_code", UInt(kindCode)),
            ("local_name", Text(localName)),
            ("qualified_name", Text(qualifiedName)),
            ("rendered_signature", Text(signature)),
            ("modifiers", Text(modifiers)),
            ("lexical_parent_ordinal", Int(parent)),
            ("span_start_utf16", UInt(start)),
            ("span_length_utf16", UInt(length)));

    public static string ReferenceWireRecord(
        string documentFingerprint,
        int ordinal,
        int kindCode,
        int? parent,
        string target,
        int start,
        int length) => Record(
            ("frame", Text("retained-csharp-reference-v1")),
            ("document_fingerprint", Text(documentFingerprint)),
            ("ordinal", UInt(ordinal)),
            ("relationship_kind_code", UInt(kindCode)),
            ("source_symbol_ordinal", NullableUInt(parent)),
            ("target_display", Text(target)),
            ("span_start_utf16", UInt(start)),
            ("span_length_utf16", UInt(length)));

    public static string DiagnosticWireRecord(
        string documentFingerprint,
        int ordinal,
        string id,
        int severity,
        int start,
        int length,
        string? message,
        string? withheld) => Record(
            ("frame", Text("retained-csharp-diagnostic-v1")),
            ("document_fingerprint", Text(documentFingerprint)),
            ("ordinal", UInt(ordinal)),
            ("diagnostic_id", Text(id)),
            ("severity_code", UInt(severity)),
            ("span_start_utf16", UInt(start)),
            ("span_length_utf16", UInt(length)),
            ("representation", Text(message is null ? "withheld" : "scanned")),
            ("scanned_message", NullableText(message)),
            ("withheld_reason", NullableText(withheld)));

    public static string BlockedDiagnosticWireRecord(
        SourceRevisionId revision,
        string sha256,
        int ordinal,
        string id,
        int severity,
        int start,
        int length,
        string? message,
        string? withheld) => Record(
            ("frame", Text("retained-csharp-blocked-diagnostic-v1")),
            ("source_revision_id", Text(revision.Value.ToString("N"))),
            ("retained_artifact_sha256", Text(sha256)),
            ("descriptor_fingerprint", Text(Capability.ProcessorFingerprint)),
            ("parser_fingerprint", Text(ParserFingerprint)),
            ("ordinal", UInt(ordinal)),
            ("diagnostic_id", Text(id)),
            ("severity_code", UInt(severity)),
            ("span_start_utf16", UInt(start)),
            ("span_length_utf16", UInt(length)),
            ("representation", Text(message is null ? "withheld" : "scanned")),
            ("scanned_message", NullableText(message)),
            ("withheld_reason", NullableText(withheld)));

    public static string CompletionWireRecord(
        string documentFingerprint,
        string parserFingerprint,
        IEnumerable<string> symbols,
        IEnumerable<string> references,
        IEnumerable<string> diagnostics,
        int withheldSymbols,
        int withheldReferences,
        int withheldDiagnostics,
        IEnumerable<string> codes) => Record(
            ("frame", Text("retained-csharp-completion-v1")),
            ("document_fingerprint", Text(documentFingerprint)),
            ("parser_fingerprint", Text(parserFingerprint)),
            ("symbol_fingerprints", List(symbols)),
            ("reference_fingerprints", List(references)),
            ("diagnostic_fingerprints", List(diagnostics)),
            ("withheld_symbol_count", UInt(withheldSymbols)),
            ("withheld_reference_count", UInt(withheldReferences)),
            ("withheld_diagnostic_count", UInt(withheldDiagnostics)),
            ("receipt_diagnostic_codes", List(codes)));

    public static string BlockedCompletionWireRecord(
        SourceRevisionId revision,
        string sha256,
        IEnumerable<string> diagnostics,
        int withheldDiagnostics,
        IEnumerable<string> codes) => Record(
            ("frame", Text("retained-csharp-blocked-completion-v1")),
            ("source_revision_id", Text(revision.Value.ToString("N"))),
            ("retained_artifact_sha256", Text(sha256)),
            ("descriptor_fingerprint", Text(Capability.ProcessorFingerprint)),
            ("parser_fingerprint", Text(ParserFingerprint)),
            ("outcome_code", Text("csharp-code-syntax-invalid")),
            ("blocked_diagnostic_fingerprints", List(diagnostics)),
            ("withheld_symbol_count", UInt(0)),
            ("withheld_reference_count", UInt(0)),
            ("withheld_diagnostic_count", UInt(withheldDiagnostics)),
            ("receipt_diagnostic_codes", List(codes)));

    public static string ComputeDocumentFingerprint(
        SourceRevisionId sourceRevisionId,
        string sha256,
        string parserFingerprint) => Hash(DocumentWireRecord(sourceRevisionId, sha256, parserFingerprint));

    public static string ComputeSymbolFingerprint(
        string documentFingerprint,
        int ordinal,
        int kindCode,
        string localName,
        string qualifiedName,
        string signature,
        string modifiers,
        int parent,
        int start,
        int length) => Hash(SymbolWireRecord(
            documentFingerprint, ordinal, kindCode, localName, qualifiedName, signature, modifiers, parent, start, length));

    public static string ComputeReferenceFingerprint(
        string documentFingerprint,
        int ordinal,
        int kindCode,
        int? parent,
        string target,
        int start,
        int length) => Hash(ReferenceWireRecord(
            documentFingerprint, ordinal, kindCode, parent, target, start, length));

    public static string ComputeDiagnosticFingerprint(
        string document,
        int ordinal,
        string id,
        int severity,
        int start,
        int length,
        string? message,
        string? withheld) => Hash(DiagnosticWireRecord(
            document, ordinal, id, severity, start, length, message, withheld));

    public static string ComputeBlockedDiagnosticFingerprint(
        SourceRevisionId revision,
        string sha256,
        int ordinal,
        string id,
        int severity,
        int start,
        int length,
        string? message,
        string? withheld) => Hash(BlockedDiagnosticWireRecord(
            revision, sha256, ordinal, id, severity, start, length, message, withheld));

    public static string ComputeCompletionFingerprint(
        string document,
        string parser,
        IEnumerable<string> symbols,
        IEnumerable<string> references,
        IEnumerable<string> diagnostics,
        int withheldSymbols,
        int withheldReferences,
        int withheldDiagnostics,
        IEnumerable<string> codes) => Hash(CompletionWireRecord(
            document, parser, symbols, references, diagnostics,
            withheldSymbols, withheldReferences, withheldDiagnostics, codes));

    public static string ComputeBlockedCompletionFingerprint(
        SourceRevisionId revision,
        string sha256,
        IEnumerable<string> diagnostics,
        int withheldDiagnostics,
        IEnumerable<string> codes) => Hash(BlockedCompletionWireRecord(
            revision, sha256, diagnostics, withheldDiagnostics, codes));

    private static string BuildDescriptorWireRecord() => Record(
        ("frame", Text("retained-csharp-descriptor-v1")),
        ("capability_id", Text("08dd66fb65024b31a4a551e8cc66f916")),
        ("processor_kind", Text(ProcessorKind)),
        ("processor_version", Text(ProcessorVersion)),
        ("execution_class", UInt((int)ExecutionClass.InProcess)),
        ("activity_kind", UInt((int)SourceActivityKind.CodeParsing)),
        ("accepted_classification", Text("AcceptedUtf8Text")),
        ("output_contract", Text("retained:csharp-code-facts-v1")),
        ("handler_implementation_id", Text(HandlerImplementationId)),
        ("roslyn_assembly_version", Text("5.0.0.0")),
        ("language_version", Text("CSharp14")),
        ("utf8_policy", Text("utf8-strict-optional-bom")),
        ("limit_input_bytes", UInt(MaximumInputBytes)),
        ("limit_decoded_utf16_code_units", UInt(MaximumDecodedUtf16CodeUnits)),
        ("limit_syntax_nodes", UInt(MaximumSyntaxNodes)),
        ("limit_nesting_depth", UInt(MaximumNestingDepth)),
        ("limit_symbols", UInt(MaximumSymbols)),
        ("limit_references", UInt(MaximumReferences)),
        ("limit_identifier_utf16_code_units", UInt(MaximumIdentifierUtf16CodeUnits)),
        ("limit_signature_utf16_code_units", UInt(MaximumSignatureUtf16CodeUnits)),
        ("limit_diagnostics", UInt(MaximumDiagnostics)),
        ("limit_diagnostic_message_utf16_code_units", UInt(MaximumDiagnosticMessageUtf16CodeUnits)));

    private static string BuildParserWireRecord() => Record(
        ("frame", Text("retained-csharp-parser-v1")),
        ("handler_implementation_id", Text(HandlerImplementationId)),
        ("roslyn_assembly_version", Text("5.0.0.0")),
        ("language_version", Text("CSharp14")),
        ("utf8_policy", Text("utf8-strict-optional-bom")),
        ("fact_normalisation_revision", Text("without-trivia-one-line-v1")),
        ("traversal_limit_precedence_revision", Text("source-preorder-v1")),
        ("limit_input_bytes", UInt(MaximumInputBytes)),
        ("limit_decoded_utf16_code_units", UInt(MaximumDecodedUtf16CodeUnits)),
        ("limit_syntax_nodes", UInt(MaximumSyntaxNodes)),
        ("limit_nesting_depth", UInt(MaximumNestingDepth)),
        ("limit_symbols", UInt(MaximumSymbols)),
        ("limit_references", UInt(MaximumReferences)),
        ("limit_identifier_utf16_code_units", UInt(MaximumIdentifierUtf16CodeUnits)),
        ("limit_signature_utf16_code_units", UInt(MaximumSignatureUtf16CodeUnits)),
        ("limit_diagnostics", UInt(MaximumDiagnostics)),
        ("limit_diagnostic_message_utf16_code_units", UInt(MaximumDiagnosticMessageUtf16CodeUnits)),
        ("syntax_invalid_outcome", Text("csharp-code-syntax-invalid")));

    private static string Record(params (string Name, string Value)[] values) =>
        string.Join('|', values.Select(value => Text(value.Name) + "|" + value.Value));

    private static string Text(string value) =>
        Encoding.UTF8.GetByteCount(value).ToString(CultureInfo.InvariantCulture) + ":" + value;

    private static string NullableText(string? value) => value is null ? "-;" : Text(value);

    private static string UInt(int value) => value >= 0
        ? value.ToString(CultureInfo.InvariantCulture) + ";"
        : throw new ArgumentOutOfRangeException(nameof(value));

    private static string Int(int value) => value.ToString(CultureInfo.InvariantCulture) + ";";

    private static string NullableUInt(int? value) => value is null ? "-;" : UInt(value.Value);

    private static string List(IEnumerable<string> values)
    {
        var materialised = values.ToArray();
        return UInt(materialised.Length) + string.Concat(materialised.Select(Text));
    }

    private static string Hash(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private sealed class AttributeRemovingRewriter : CSharpSyntaxRewriter
    {
        public override SyntaxNode? VisitAttributeList(AttributeListSyntax node) => null;
    }

    private sealed class SecretScanFailedException : Exception
    {
        public SecretScanFailedException()
        {
        }

        public SecretScanFailedException(Exception innerException)
            : base("The bounded C# fact secret scan failed.", innerException)
        {
        }
    }

    private readonly record struct DeclarationDescription(int KindCode, string? Kind, string LocalName);
    private readonly record struct DeclarationContext(int RawOrdinal, string QualifiedName);
    private readonly record struct RawSymbol(
        int RawOrdinal,
        int KindCode,
        string Kind,
        string LocalName,
        string QualifiedName,
        string RenderedSignature,
        string Modifiers,
        int ParentRawOrdinal,
        int SpanStart,
        int SpanLength);
    private readonly record struct RawReference(
        int KindCode,
        string Kind,
        int SourceRawSymbolOrdinal,
        string TargetDisplay,
        int SpanStart,
        int SpanLength,
        int RawKind,
        int Preorder);
    private readonly record struct DisclosureResult(string? Value, bool Withheld, string? ReasonCode);
}

/// <summary>Descriptor-only handler; Task 5 does not wire it into the local registry.</summary>
public sealed class RetainedCsharpCodeCapabilityHandler : ILocalSourceCapabilityHandler
{
    public SourceCapabilityDescriptor Descriptor => RetainedCsharpCodeProcessor.Capability;
}

public sealed record RetainedCsharpParserPreflight(
    bool IsAvailable,
    string? ReasonCode,
    string RoslynAssemblyVersion);

public sealed record RetainedCsharpCodeSymbol(
    int Ordinal,
    int DeclarationKindCode,
    string DeclarationKind,
    string LocalName,
    string QualifiedName,
    string RenderedSignature,
    string Modifiers,
    int LexicalParentOrdinal,
    int SpanStartUtf16,
    int SpanLengthUtf16,
    string SymbolFingerprint);

public sealed record RetainedCsharpCodeReference(
    int Ordinal,
    int RelationshipKindCode,
    string RelationshipKind,
    int? SourceSymbolOrdinal,
    string TargetDisplay,
    int SpanStartUtf16,
    int SpanLengthUtf16,
    string ReferenceFingerprint);

public sealed record RetainedCsharpCodeDiagnostic(
    int Ordinal,
    string DiagnosticId,
    int SeverityCode,
    int SpanStartUtf16,
    int SpanLengthUtf16,
    string? ScannedMessage,
    bool Withheld,
    string? WithheldReason,
    string DiagnosticFingerprint);

public sealed record RetainedCsharpBlockedDiagnostic(
    Guid BranchId,
    Guid AttemptId,
    int Ordinal,
    string DiagnosticId,
    int SeverityCode,
    int SpanStartUtf16,
    int SpanLengthUtf16,
    string? ScannedMessage,
    bool Withheld,
    string? WithheldReason,
    string BlockedDiagnosticFingerprint);

public sealed record RetainedCsharpCodeCompletion(
    Guid BranchId,
    SourceRevisionId SourceRevisionId,
    Guid AttemptId,
    string RetainedArtifactSha256,
    string LeaseOwner,
    long LeaseGeneration,
    DateTimeOffset LeaseExpiresAtUtc,
    string ProcessorVersion,
    string DescriptorFingerprint,
    string ParserFingerprint,
    string OutcomeCode,
    string? DocumentFingerprint,
    string? CompletionFingerprint,
    string? BlockedCompletionFingerprint,
    IReadOnlyList<RetainedCsharpCodeSymbol> Symbols,
    IReadOnlyList<RetainedCsharpCodeReference> References,
    IReadOnlyList<RetainedCsharpCodeDiagnostic> Diagnostics,
    IReadOnlyList<RetainedCsharpBlockedDiagnostic> BlockedDiagnostics,
    int WithheldSymbolCount,
    int WithheldReferenceCount,
    int WithheldDiagnosticCount,
    IReadOnlyList<string> ReceiptDiagnosticCodes)
{
    public int DecodedCharacterCount { get; init; }
    public int LineCount { get; init; }
    public string? WithheldSymbolReason => WithheldSymbolCount == 0 ? null : "secret-content-withheld";
    public string? WithheldReferenceReason => WithheldReferenceCount == 0 ? null : "secret-content-withheld";
    public string? WithheldDiagnosticReason => WithheldDiagnosticCount == 0 ? null : "secret-content-withheld";
}
