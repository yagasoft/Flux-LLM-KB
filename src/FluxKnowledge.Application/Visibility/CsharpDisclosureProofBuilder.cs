using System.Text;
using System.Globalization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace FluxKnowledge.Application.Visibility;

public sealed class CsharpDisclosureProofBuilder(ILocalPrivateContentDisclosure disclosure)
{
    public CodeDisclosureProof Build(Guid artifactId, string text, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var hash = CodeDisclosureIntegrity.Hash(text);
        CodeDisclosureProof Result(CodeDisclosureProofState state, IReadOnlyList<CodeDisclosureSpan> spans) =>
            new(artifactId, hash, text.Length, CodeDisclosureIntegrity.Fingerprint, state, spans,
                CodeDisclosureIntegrity.ProofChecksum(artifactId, hash, CodeDisclosureIntegrity.Fingerprint,
                    state, text.Length, spans));
        if (text.Length > 4_000_000 || Encoding.UTF8.GetByteCount(text) > 4 * 1024 * 1024)
            return Result(CodeDisclosureProofState.Unsupported, []);
        var tree = CSharpSyntaxTree.ParseText(text, new CSharpParseOptions(LanguageVersion.CSharp14),
            cancellationToken: cancellationToken);
        if (tree.GetDiagnostics(cancellationToken).Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error))
            return Result(CodeDisclosureProofState.Invalid, []);

        var structural = new HashSet<int>();
        var protectedRanges = new List<(int Start, int End)>();
        var pending = new Stack<(SyntaxNodeOrToken Value, int Depth)>();
        pending.Push((tree.GetRoot(cancellationToken), 0));
        var nodes = 0;
        while (pending.TryPop(out var item))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (item.Depth > 256) return Result(CodeDisclosureProofState.Unsupported, []);
            if (item.Value.IsNode)
            {
                if (++nodes > 200_000) return Result(CodeDisclosureProofState.Unsupported, []);
                var node = item.Value.AsNode()!;
                if (node is InterpolatedStringExpressionSyntax interpolated)
                {
                    var composite = Composite(node);
                    if (composite != node || UnsafeInterpolation(interpolated))
                        protectedRanges.Add((composite.SpanStart, composite.Span.End));
                }
                foreach (var child in node.ChildNodesAndTokens()) pending.Push((child, item.Depth + 1));
                continue;
            }

            var token = item.Value.AsToken();
            if (token.IsKind(SyntaxKind.OpenBraceToken) && IsStructural(token))
                structural.Add(token.SpanStart);
            if (token.Parent is LiteralExpressionSyntax literalExpression &&
                (literalExpression.IsKind(SyntaxKind.StringLiteralExpression) ||
                 literalExpression.IsKind(SyntaxKind.Utf8StringLiteralExpression)))
            {
                var protectedNode = Composite(literalExpression);
                if (protectedNode != literalExpression || Unsafe(token.ValueText))
                    protectedRanges.Add((protectedNode.SpanStart, protectedNode.Span.End));
            }
            foreach (var trivia in token.LeadingTrivia.Concat(token.TrailingTrivia))
            {
                if ((trivia.IsKind(SyntaxKind.SingleLineCommentTrivia) ||
                     trivia.IsKind(SyntaxKind.MultiLineCommentTrivia) ||
                     trivia.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia) ||
                     trivia.IsKind(SyntaxKind.MultiLineDocumentationCommentTrivia) ||
                     trivia.IsKind(SyntaxKind.DisabledTextTrivia)) && Unsafe(trivia.ToFullString()))
                    protectedRanges.Add((trivia.FullSpan.Start, trivia.FullSpan.End));
            }
        }

        var merged = new List<(int Start, int End)>();
        foreach (var range in protectedRanges.OrderBy(range => range.Start).ThenBy(range => range.End))
        {
            if (merged.Count > 0 && range.Start <= merged[^1].End)
                merged[^1] = (merged[^1].Start, Math.Max(merged[^1].End, range.End));
            else merged.Add(range);
        }
        var spans = structural.Select(offset => Span(offset, offset + 1, CodeDisclosureSpanKind.StructuralBrace))
            .Concat(merged.Select(range => Span(range.Start, range.End, CodeDisclosureSpanKind.Protected)))
            .OrderBy(span => span.Start).ThenBy(span => span.End).ThenBy(span => span.Kind).ToArray();
        return Result(CodeDisclosureProofState.Ready, spans);

        CodeDisclosureSpan Span(int start, int end, CodeDisclosureSpanKind kind) => new(start, end, kind,
            CodeDisclosureIntegrity.SpanChecksum(artifactId, hash, CodeDisclosureIntegrity.Fingerprint, start, end, kind));

        bool UnsafeInterpolation(InterpolatedStringExpressionSyntax value)
        {
            var decoded = new StringBuilder();
            var staticText = new StringBuilder();
            var unknownParameters = new List<int>();
            foreach (var content in value.Contents)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (decoded.Length > 16 * 1024 || staticText.Length > 16 * 1024) return true;
                if (content is InterpolatedStringTextSyntax literal)
                {
                    decoded.Append(literal.TextToken.ValueText);
                    staticText.Append(literal.TextToken.ValueText);
                }
                else if (content is InterpolationSyntax { AlignmentClause: null, FormatClause: null } hole)
                {
                    if (hole.Expression is LiteralExpressionSyntax constant)
                    {
                        var constantText = Convert.ToString(constant.Token.Value, CultureInfo.InvariantCulture);
                        decoded.Append(constantText);
                        staticText.Append(constantText);
                    }
                    else if (IsParameter(hole.Expression))
                    {
                        unknownParameters.Add(decoded.Length);
                        decoded.Append('?');
                    }
                    else return true;
                }
                else return true;
            }
            // Source references are never executed. Allow only standalone SQL value
            // parameters outside quoted/comment contexts; a SQL prefix alone
            // cannot establish that a hole is outside an encoded or composite token.
            // Scan joined known text too, so holes cannot split credential keys.
            var joined = decoded.ToString();
            var plain = staticText.ToString();
            var sql = plain.TrimStart();
            var isSql = new[] { "SELECT ", "UPDATE ", "INSERT ", "DELETE ", "MERGE ", "WITH " }
                .Any(prefix => sql.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
            return unknownParameters.Count > 0 && (!isSql || !AreSqlValueParameters(joined, unknownParameters)) ||
                Unsafe(joined) || unknownParameters.Count > 0 && Unsafe(plain);
        }

        bool Unsafe(string value)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (disclosure.EvaluateDecodedText(value).Withheld) return true;
            // Literal/trivia text can contain an encoded token beside ordinary prose.
            // Reuse the bounded detector for each token; never evaluate expressions.
            var start = -1;
            for (var index = 0; index <= value.Length; index++)
            {
                if ((index & 255) == 0) cancellationToken.ThrowIfCancellationRequested();
                var encoded = index < value.Length &&
                    (char.IsLetterOrDigit(value[index]) || value[index] is '+' or '/' or '-' or '_' or '=');
                if (encoded && start < 0) start = index;
                if (encoded || start < 0) continue;
                if (index - start >= 12 && disclosure.Evaluate(value[start..index], LocalDisclosureKind.CodeExcerpt).Withheld)
                    return true;
                start = -1;
            }
            return false;
        }
    }

    private static bool IsParameter(ExpressionSyntax expression) => expression switch
    {
        IdentifierNameSyntax => true,
        MemberAccessExpressionSyntax { Name: IdentifierNameSyntax } access => IsParameter(access.Expression),
        _ => false
    };

    private static SyntaxNode Composite(SyntaxNode value)
    {
        var current = value;
        var composite = value;
        while (current.Parent is { } parent)
        {
            // Selection wrappers can carry a literal to an outer concatenation.
            // Traverse them without deciding which value runs; protect the whole
            // composition rather than scanning its independently harmless pieces.
            if (parent is ParenthesizedExpressionSyntax or CastExpressionSyntax or
                ConditionalExpressionSyntax or SwitchExpressionSyntax or SwitchExpressionArmSyntax ||
                parent.IsKind(SyntaxKind.CoalesceExpression) ||
                parent.IsKind(SyntaxKind.SuppressNullableWarningExpression)) current = parent;
            else if (parent.IsKind(SyntaxKind.AddExpression) || parent.IsKind(SyntaxKind.AddAssignmentExpression))
                composite = current = parent;
            else break;
        }
        return composite;
    }

    private static bool AreSqlValueParameters(string text, IReadOnlyList<int> positions)
    {
        // Recognise only quote/comment boundaries and standalone value positions.
        // No SQL execution, dialect parser or inferred runtime value is involved.
        var quote = '\0';
        var lineComment = false;
        var blockDepth = 0;
        var nextParameter = 0;
        for (var index = 0; index < text.Length; index++)
        {
            if (nextParameter < positions.Count && index == positions[nextParameter])
            {
                if (quote != '\0' || lineComment || blockDepth > 0) return false;
                var preceding = index - 1;
                while (preceding >= 0 && char.IsWhiteSpace(text[preceding])) preceding--;
                if (preceding < 0 || text[preceding] is not ('=' or '<' or '>' or '(' or ',')) return false;
                var following = index + 1;
                if (following < text.Length && !char.IsWhiteSpace(text[following]) &&
                    text[following] is not (')' or ',' or ';')) return false;
                nextParameter++;
            }
            var character = text[index];
            var next = index + 1 < text.Length ? text[index + 1] : '\0';
            if (lineComment)
            {
                if (character is '\r' or '\n') lineComment = false;
            }
            else if (quote != '\0')
            {
                if (character != quote) continue;
                if (next == quote) index++;
                else quote = '\0';
            }
            else if (character == '/' && next == '*') { blockDepth++; index++; }
            else if (blockDepth > 0)
            {
                if (character == '*' && next == '/') { blockDepth--; index++; }
            }
            else if (character == '-' && next == '-') { lineComment = true; index++; }
            else if (character is '\'' or '"' or '[') quote = character == '[' ? ']' : character;
        }
        return nextParameter == positions.Count && quote == '\0' && blockDepth == 0;
    }

    private static bool IsStructural(SyntaxToken token)
    {
        if (token.Parent is NamespaceDeclarationSyntax or BaseTypeDeclarationSyntax or AccessorListSyntax)
            return true;
        if (token.Parent is SwitchStatementSyntax statement)
            return statement.Ancestors().Any(ancestor => ancestor is BaseTypeDeclarationSyntax) &&
                !statement.Ancestors().Any(ancestor => ancestor is GlobalStatementSyntax);
        if (token.Parent is not BlockSyntax block ||
            !block.Ancestors().Any(ancestor => ancestor is BaseTypeDeclarationSyntax) ||
            block.Ancestors().Any(ancestor => ancestor is GlobalStatementSyntax))
            return false;
        return block.Parent is BaseMethodDeclarationSyntax or AccessorDeclarationSyntax or
            LocalFunctionStatementSyntax or BlockSyntax or IfStatementSyntax or ElseClauseSyntax or
            ForStatementSyntax or ForEachStatementSyntax or ForEachVariableStatementSyntax or
            WhileStatementSyntax or DoStatementSyntax or UsingStatementSyntax or LockStatementSyntax or
            FixedStatementSyntax or CheckedStatementSyntax or UnsafeStatementSyntax or TryStatementSyntax or
            CatchClauseSyntax or FinallyClauseSyntax or LambdaExpressionSyntax or AnonymousMethodExpressionSyntax;
    }
}
