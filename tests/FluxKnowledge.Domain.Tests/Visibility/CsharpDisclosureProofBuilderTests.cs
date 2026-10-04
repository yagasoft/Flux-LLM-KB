using FluxKnowledge.Application.Visibility;
using FluxKnowledge.Infrastructure.SqlServer.Visibility;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace FluxKnowledge.Domain.Tests.Visibility;

public sealed class CsharpDisclosureProofBuilderTests
{
    private readonly LocalPrivateContentDisclosure disclosure = new();

    [Fact]
    public void Late_method_body_and_long_guard_are_disclosed_with_canonical_proof()
    {
        var text = "// 😀\r\n" + new string(' ', 22_000) +
            "namespace Sample { class C { public int M() {\n" +
            string.Concat(Enumerable.Repeat("var harmless = 1;\n", 300)) + "return 1;\n} } }";
        var start = text.IndexOf("{\n", StringComparison.Ordinal);
        var body = text[start..];
        Assert.True(disclosure.Evaluate(body, LocalDisclosureKind.CodeExcerpt).Withheld);
        var proof = Build(text);

        Assert.Equal(CodeDisclosureProofState.Ready, proof.State);
        Assert.False(disclosure.EvaluateCode(body, LocalDisclosureKind.CodeExcerpt,
            proof.Window(start, body.Length)).Withheld);
    }

    [Theory]
    [InlineData("class C { void M() {")]
    [InlineData("{ \"password\":\"synthetic\" }")]
    [InlineData("{ var x = 1; }")]
    public void Incomplete_or_unanchored_source_never_grants_a_structural_exception(string text)
    {
        var proof = Build(text);
        Assert.DoesNotContain(proof.Spans, span => span.Kind == CodeDisclosureSpanKind.StructuralBrace);
    }

    [Theory]
    [InlineData("\"eyJwYXNzd29yZCI6InN5bnRoZXRpYyJ9\"")]
    [InlineData("@\"{\"\"password\"\":\"\"synthetic\"\"}\"")]
    [InlineData("\"\"\"{\"password\":\"synthetic\"}\"\"\"")]
    [InlineData("$\"credential: {1} eyJwYXNzd29yZCI6InN5bnRoZXRpYyJ9\"")]
    public void Protected_literal_findings_survive_a_clipped_window(string literal)
    {
        var text = "class C { void M() { var value = " + literal + "; } void Clean() { return; } }";
        var proof = Build(text);
        var span = Assert.Single(proof.Spans, span => span.Kind == CodeDisclosureSpanKind.Protected);
        var start = span.Start + 4;
        var clipped = text.Substring(start, Math.Min(8, span.End - start));
        Assert.True(disclosure.EvaluateCode(clipped, LocalDisclosureKind.CodeExcerpt,
            proof.Window(start, clipped.Length)).Withheld);
        var cleanStart = text.IndexOf("void Clean", StringComparison.Ordinal);
        var clean = text[cleanStart..];
        Assert.False(disclosure.EvaluateCode(clean, LocalDisclosureKind.CodeExcerpt,
            proof.Window(cleanStart, clean.Length)).Withheld);
    }

    [Theory]
    [InlineData("// \"password\":\"synthetic\"")]
    [InlineData("// eyJwYXNzd29yZCI6InN5bnRoZXRpYyJ9")]
    [InlineData("#if false\n\"password\":\"synthetic\"\n#endif")]
    public void Comments_and_disabled_text_keep_credential_protection(string content)
    {
        var text = "class C { void M() {\n" + content + "\nreturn;\n} }";
        var proof = Build(text);
        var start = text.IndexOf(content, StringComparison.Ordinal);
        Assert.Contains(proof.Spans, span => span.Kind == CodeDisclosureSpanKind.Protected);
        Assert.True(disclosure.EvaluateCode(text.Substring(start + 6, 6),
            LocalDisclosureKind.CodeExcerpt, proof.Window(start + 6, 6)).Withheld);
        Assert.True(disclosure.EvaluateCode(text.Substring(start, content.Length),
            LocalDisclosureKind.CodeExcerpt, proof.Window(start, content.Length)).Withheld);
    }

    [Fact]
    public void Initialisers_and_braces_inside_literals_are_not_structural_proof()
    {
        const string text = "class C { object M() { var text = \"{ harmless }\"; return new { Value = 1 }; } }";
        var proof = Build(text);
        var literalBrace = text.IndexOf("{ harmless", StringComparison.Ordinal);
        var initializerBrace = text.IndexOf("{ Value", StringComparison.Ordinal);
        Assert.DoesNotContain(proof.Spans, span => span.Kind == CodeDisclosureSpanKind.StructuralBrace &&
            (span.Start == literalBrace || span.Start == initializerBrace));
    }

    [Fact]
    public void A_corrupt_span_or_missing_protected_row_cannot_enable_a_brace_exception()
    {
        var text = "class C { void M() {\n" + string.Concat(Enumerable.Repeat("var x = 1;\n", 500)) + "} }";
        var proof = Build(text);
        var window = proof.Window(0, text.Length);
        var first = window.Spans[0];
        var corrupt = window with { Spans = [first with { Start = first.Start + 1 }, ..window.Spans.Skip(1)] };
        Assert.True(disclosure.EvaluateCode(text, LocalDisclosureKind.CodeExcerpt, corrupt).Withheld);
        Assert.True(disclosure.EvaluateCode(text, LocalDisclosureKind.CodeExcerpt,
            window with { PersistedSpanCount = window.PersistedSpanCount - 1 }).Withheld);
    }

    [Fact]
    public void Moving_a_structural_offset_onto_an_actual_literal_brace_or_changing_policy_is_rejected()
    {
        const string text = "class C { string M() { return \"{ harmless }\"; } }";
        var window = Build(text).Window(0, text.Length);
        var offset = text.IndexOf("{ harmless", StringComparison.Ordinal);
        var moved = window.Spans[0] with { Start = offset, End = offset + 1 };
        var corrupt = window with { Spans = window.Spans.Skip(1).Append(moved).OrderBy(span => span.Start).ToArray() };
        Assert.False(corrupt.IsValid(text, 0));
        Assert.True(disclosure.EvaluateCode(text, LocalDisclosureKind.CodeExcerpt, corrupt).Withheld);
        Assert.True(disclosure.EvaluateCode(text, LocalDisclosureKind.CodeExcerpt,
            window with { Fingerprint = "previous-disclosure-policy" }).Withheld);
    }

    [Fact]
    public void Actual_root_scan_method_and_original_complete_line_guard_are_disclosed()
    {
        var folder = new DirectoryInfo(AppContext.BaseDirectory);
        const string relative = "src/FluxKnowledge.Infrastructure.SqlServer/Persistence/SqlSourceScanStore.cs";
        while (folder is not null && !File.Exists(Path.Combine(folder.FullName, relative))) folder = folder.Parent;
        Assert.NotNull(folder);
        var text = File.ReadAllText(Path.Combine(folder.FullName, relative)).Replace("\r\n", "\n", StringComparison.Ordinal);
        var method = CSharpSyntaxTree.ParseText(text).GetRoot().DescendantNodes()
            .OfType<MethodDeclarationSyntax>().Single(value => value.Identifier.ValueText == "OwnsRootScanAsync");
        var proof = Build(text);
        var body = text[method.Body!.Span.Start..method.Body.Span.End];
        Assert.True(disclosure.Evaluate(body, LocalDisclosureKind.CodeExcerpt).Withheld);
        Assert.False(disclosure.EvaluateCode(body, LocalDisclosureKind.CodeExcerpt,
            proof.Window(method.Body.Span.Start, body.Length)).Withheld);
        var first = 4658 - 512 - 2048;
        var last = 4658 + 568 + 512 + 2048;
        while (first > 0 && text[first - 1] != '\n') first--;
        while (last < text.Length && text[last] != '\n') last++;
        last++;
        var guard = text[first..last];
        Assert.True(proof.Window(first, guard.Length).IsValid(guard, 0));
        Assert.DoesNotContain(proof.Window(first, guard.Length).Spans,
            span => span.Kind == CodeDisclosureSpanKind.Protected);
        Assert.True(disclosure.Evaluate(guard, LocalDisclosureKind.CodeExcerpt).Withheld);
        Assert.False(disclosure.EvaluateCode(guard, LocalDisclosureKind.CodeExcerpt,
            proof.Window(first, guard.Length)).Withheld);
    }

    [Fact]
    public void Composite_interpolation_cannot_split_a_credential_key_across_text_segments()
    {
        const string literal = "$\"prefix {{ \\\"pa{\"\"}ssword\\\":\\\"synthetic\\\" }} suffix\"";
        var text = "class C { void M() { var value = " + literal + "; } }";
        var proof = Build(text);
        var start = text.IndexOf("synthetic", StringComparison.Ordinal);
        Assert.True(disclosure.EvaluateCode(text.Substring(start, 9), LocalDisclosureKind.CodeExcerpt,
            proof.Window(start, 9)).Withheld);
    }

    [Fact]
    public void Parentheses_cannot_hide_an_ambiguous_literal_concatenation()
    {
        const string text = "class C { void M() { var value = (\"eyJw\") + (\"YXNzd29yZCI6InN5bnRoZXRpYyJ9\"); } }";
        var proof = Build(text);
        var start = text.IndexOf("N5bnRo", StringComparison.Ordinal);
        Assert.True(disclosure.EvaluateCode(text.Substring(start, 6), LocalDisclosureKind.CodeExcerpt,
            proof.Window(start, 6)).Withheld);
    }

    [Theory]
    [InlineData("(\"eyJwYXNz\" ?? \"\") + (\"d29yZCI6InN5bnRoZXRpYyJ9\" ?? \"\")")]
    [InlineData("(true ? \"eyJwYXNz\" : \"\") + (true ? \"d29yZCI6InN5bnRoZXRpYyJ9\" : \"\")")]
    [InlineData("(1 switch { 1 => \"eyJwYXNz\", _ => \"\" }) + (1 switch { 1 => \"d29yZCI6InN5bnRoZXRpYyJ9\", _ => \"\" })")]
    [InlineData("($\"eyJw{\"YXNz\"}\" ?? \"\") + (\"d29yZCI6InN5bnRoZXRpYyJ9\" ?? \"\")")]
    [InlineData("\"eyJwYXNz\"; value += \"d29yZCI6InN5bnRoZXRpYyJ9\"")]
    public void Selection_wrappers_cannot_hide_composite_encoded_credentials_from_clipped_reads(string expression)
    {
        var text = "class C { void M() { var value = " + expression + "; } void Clean() { return; } }";
        var proof = Build(text);
        var start = text.IndexOf("d29yZCI6InN5bnRoZXRpYyJ9", StringComparison.Ordinal);
        Assert.Equal(CodeDisclosureProofState.Ready, proof.State);
        Assert.True(disclosure.EvaluateCode(text.Substring(start, 24), LocalDisclosureKind.CodeExcerpt,
            proof.Window(start, 24)).Withheld);
        var cleanStart = text.IndexOf("void Clean", StringComparison.Ordinal);
        Assert.False(disclosure.EvaluateCode(text[cleanStart..], LocalDisclosureKind.CodeExcerpt,
            proof.Window(cleanStart, text.Length - cleanStart)).Withheld);
    }

    [Fact]
    public void Parsed_switch_statement_brace_under_a_type_is_structural_but_switch_expression_brace_is_not()
    {
        var text = "class C { void M(int value) { switch (value) { case 1:\n" +
            string.Concat(Enumerable.Repeat("var harmless = 1;\n", 300)) + "break; default: break; } } " +
            "int Other(int value) => value switch { 0 => 1, _ => 2 }; }";
        var proof = Build(text);
        Assert.False(disclosure.EvaluateCode(text, LocalDisclosureKind.CodeExcerpt, proof.Window(0, text.Length)).Withheld);
        var expressionBrace = text.IndexOf("{ 0 =>", StringComparison.Ordinal);
        Assert.DoesNotContain(proof.Spans, span => span.Kind == CodeDisclosureSpanKind.StructuralBrace && span.Start == expressionBrace);
    }

    [Theory]
    [InlineData("$\"SELECT 'eyJw{\"YXNz\"}{x}d29yZCI6InN5bnRoZXRpYyJ9'\"")]
    [InlineData("$\"SELECT eyJw{\"YXNz\"}{x}d29yZCI6InN5bnRoZXRpYyJ9\"")]
    [InlineData("$\"SELECT 1 -- eyJw{\"YXNz\"}{x}d29yZCI6InN5bnRoZXRpYyJ9\"")]
    [InlineData("$\"SELECT 1 /* eyJw{\"YXNz\"}{x}d29yZCI6InN5bnRoZXRpYyJ9 */\"")]
    public void Unknown_SQL_holes_in_quoted_composite_or_comment_tokens_remain_protected(string literal)
    {
        var text = "class C { void M() { const string x = \"\"; var value = " + literal + "; } }";
        var proof = Build(text);
        var start = text.IndexOf("d29yZCI6InN5bnRoZXRpYyJ9", StringComparison.Ordinal);
        Assert.Equal(CodeDisclosureProofState.Ready, proof.State);
        Assert.True(disclosure.EvaluateCode(text.Substring(start, 24), LocalDisclosureKind.CodeExcerpt,
            proof.Window(start, 24)).Withheld);
    }

    [Fact]
    public void Header_offsets_do_not_turn_metadata_braces_into_code()
    {
        const string text = "class C { void M() { return; } }";
        var proof = Build(text);
        const string header = "{\"password\":\"synthetic\"}\n";
        Assert.True(disclosure.EvaluateCode(header + text, LocalDisclosureKind.CodeExcerpt,
            proof.Window(0, text.Length), header.Length).Withheld);
    }

    [Fact]
    public void Completed_candidate_with_long_safe_suffix_requires_valid_code_proof()
    {
        var text = "class C { object M() { var item = new { Value = 1 };\n" +
            string.Concat(Enumerable.Repeat("var harmless = 1;\n", 300)) + "return item; } }";
        var proof = Build(text);
        var start = text.IndexOf("return item", StringComparison.Ordinal);
        Assert.True(disclosure.Evaluate(text, LocalDisclosureKind.CodeExcerpt).Withheld);
        Assert.False(disclosure.EvaluateCodeGuard(text, LocalDisclosureKind.CodeExcerpt,
            proof.Window(0, text.Length), start, 12).Withheld);
        Assert.True(disclosure.EvaluateCodeGuard(text, LocalDisclosureKind.CodeExcerpt,
            proof.Window(0, text.Length) with { Fingerprint = "stale" }, start, 12).Withheld);
    }

    [Theory]
    [InlineData("// \"password\":\"synthetic\"")]
    [InlineData("// \\\"client_secret\\\":\\\"synthetic\\\"")]
    [InlineData("var value = \"eyJwYXNzd29yZCI6InN5bnRoZXRpYyJ9\";")]
    [InlineData("var value = (\"eyJw\") + (\"YXNzd29yZCI6InN5bnRoZXRpYyJ9\");")]
    public void Closed_candidate_does_not_allow_distant_credential_output(string credential)
    {
        var text = "class C { void M() { var item = new { Value = 1 };\n" +
            string.Concat(Enumerable.Repeat("var harmless = 1;\n", 300)) + credential + "\n} }";
        var start = text.IndexOf(credential, StringComparison.Ordinal);
        var proof = Build(text);
        Assert.True(disclosure.EvaluateCodeGuard(text, LocalDisclosureKind.CodeExcerpt,
            proof.Window(0, text.Length), start + credential.Length / 2, 4).Withheld);
    }

    [Theory]
    [InlineData("// \"password\":\"synthetic\"")]
    [InlineData("// \\\"client_secret\\\":\\\"synthetic\\\"")]
    public void Raw_and_escaped_credential_evidence_outside_output_still_refuses_the_full_guard(string credential)
    {
        var text = "class C { void M() { var item = new { Value = 1 };\n" +
            string.Concat(Enumerable.Repeat("var harmless = 1;\n", 300)) + credential + "\nreturn; } }";
        var start = text.IndexOf("return;", StringComparison.Ordinal);
        Assert.True(disclosure.EvaluateCodeGuard(text, LocalDisclosureKind.CodeExcerpt,
            Build(text).Window(0, text.Length), start, 7).Withheld);
    }

    [Theory]
    [InlineData(-1, 1)]
    [InlineData(0, -1)]
    [InlineData(0, 100)]
    [InlineData(int.MaxValue, int.MaxValue)]
    public void Guard_output_interval_must_be_wholly_inside_the_verified_window(int start, int length)
    {
        const string text = "class C { void M() { return; } }";
        Assert.True(disclosure.EvaluateCodeGuard(text, LocalDisclosureKind.CodeExcerpt,
            Build(text).Window(0, text.Length), start, length).Withheld);
    }

    [Theory]
    [InlineData("[unfinished")]
    [InlineData("{unfinished")]
    public void Unfinished_candidate_with_long_suffix_remains_withheld_in_a_code_guard(string candidate)
    {
        var text = "class C { void M() { var value = \"" + candidate + "\";\n" +
            string.Concat(Enumerable.Repeat("var harmless = 1;\n", 300)) + "return; } }";
        var start = text.IndexOf("return;", StringComparison.Ordinal);
        Assert.True(disclosure.EvaluateCodeGuard(text, LocalDisclosureKind.CodeExcerpt,
            Build(text).Window(0, text.Length), start, 7).Withheld);
    }

    [Fact]
    public void Oversized_or_cancelled_input_does_not_publish_partial_proof()
    {
        Assert.Equal(CodeDisclosureProofState.Unsupported, Build(new string(' ', 4_000_001)).State);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() =>
            new CsharpDisclosureProofBuilder(disclosure).Build(Guid.NewGuid(), "class C {}", cancellation.Token));
    }

    private CodeDisclosureProof Build(string text) =>
        new CsharpDisclosureProofBuilder(disclosure).Build(Guid.NewGuid(), text, CancellationToken.None);
}
