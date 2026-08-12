using System.Text;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Sources;
using FluxKnowledge.Application.Visibility;
using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Infrastructure.SqlServer.Visibility;
using Xunit;

namespace FluxKnowledge.Domain.Tests.Sources;

public sealed class RetainedCsharpCodeProcessorTests
{
    [Fact]
    public void Descriptor_and_parser_records_match_the_approved_golden_vectors()
    {
        var descriptor = RetainedCsharpCodeProcessor.Capability;

        Assert.Equal(new Guid("08dd66fb-6502-4b31-a4a5-51e8cc66f916"), descriptor.Id);
        Assert.Equal("retained-csharp-code", descriptor.ProcessorKind);
        Assert.Equal("phase-5-retained-csharp-code-v1", descriptor.ProcessorVersion);
        Assert.Equal(ExecutionClass.InProcess, descriptor.ExecutionClass);
        Assert.Equal(SourceActivityKind.CodeParsing, descriptor.AcceptedActivityKind);
        Assert.Equal("AcceptedUtf8Text", descriptor.AcceptedClassification);
        Assert.Equal("retained:csharp-code-facts-v1", descriptor.OutputContract);
        Assert.Equal("7fe4418293ef7cce98be6918c1aa393e8b19cbc33404488ec3134648661420cd", descriptor.ProcessorFingerprint);
        Assert.Equal(descriptor.ProcessorFingerprint, RetainedCsharpCodeProcessor.ComputeDescriptorFingerprint());
        Assert.Equal("5:frame|29:retained-csharp-descriptor-v1|13:capability_id|32:08dd66fb65024b31a4a551e8cc66f916|14:processor_kind|20:retained-csharp-code|17:processor_version|31:phase-5-retained-csharp-code-v1|15:execution_class|0;|13:activity_kind|5;|23:accepted_classification|16:AcceptedUtf8Text|15:output_contract|29:retained:csharp-code-facts-v1|25:handler_implementation_id|32:retained-csharp-roslyn-syntax-v1|23:roslyn_assembly_version|7:5.0.0.0|16:language_version|8:CSharp14|11:utf8_policy|24:utf8-strict-optional-bom|17:limit_input_bytes|4194304;|30:limit_decoded_utf16_code_units|4000000;|18:limit_syntax_nodes|200000;|19:limit_nesting_depth|256;|13:limit_symbols|20000;|16:limit_references|100000;|33:limit_identifier_utf16_code_units|1024;|32:limit_signature_utf16_code_units|4096;|17:limit_diagnostics|256;|41:limit_diagnostic_message_utf16_code_units|1024;", RetainedCsharpCodeProcessor.DescriptorWireRecord);
        Assert.Equal("5:frame|25:retained-csharp-parser-v1|25:handler_implementation_id|32:retained-csharp-roslyn-syntax-v1|23:roslyn_assembly_version|7:5.0.0.0|16:language_version|8:CSharp14|11:utf8_policy|24:utf8-strict-optional-bom|27:fact_normalisation_revision|26:without-trivia-one-line-v1|35:traversal_limit_precedence_revision|18:source-preorder-v1|17:limit_input_bytes|4194304;|30:limit_decoded_utf16_code_units|4000000;|18:limit_syntax_nodes|200000;|19:limit_nesting_depth|256;|13:limit_symbols|20000;|16:limit_references|100000;|33:limit_identifier_utf16_code_units|1024;|32:limit_signature_utf16_code_units|4096;|17:limit_diagnostics|256;|41:limit_diagnostic_message_utf16_code_units|1024;|22:syntax_invalid_outcome|26:csharp-code-syntax-invalid", RetainedCsharpCodeProcessor.ParserWireRecord);
        Assert.Equal("b824392bda99068d236ce8b7bf1d079d806c66d2ba892193a42b316f1f15f1fe", RetainedCsharpCodeProcessor.ParserFingerprint);
    }

    [Fact]
    public void Every_fact_and_completion_wire_record_matches_a_literal_golden_vector()
    {
        const string document = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        const string parser = "abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789";
        var revision = RetainedCsharpCodeTestData.FixedSourceRevisionId;

        Assert.Equal("5:frame|27:retained-csharp-document-v1|18:source_revision_id|32:11111111222233334444555555555555|24:retained_artifact_sha256|64:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa|17:processor_version|31:phase-5-retained-csharp-code-v1|22:descriptor_fingerprint|64:7fe4418293ef7cce98be6918c1aa393e8b19cbc33404488ec3134648661420cd|18:parser_fingerprint|64:abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789", RetainedCsharpCodeProcessor.DocumentWireRecord(revision, new string('a', 64), parser));
        Assert.Equal("5:frame|25:retained-csharp-symbol-v1|20:document_fingerprint|64:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef|7:ordinal|2;|21:declaration_kind_code|11;|10:local_name|1:M|14:qualified_name|13:global::N.C.M|18:rendered_signature|15:public void M()|9:modifiers|6:public|22:lexical_parent_ordinal|1;|16:span_start_utf16|12;|17:span_length_utf16|8;", RetainedCsharpCodeProcessor.SymbolWireRecord(document, 2, 11, "M", "global::N.C.M", "public void M()", "public", 1, 12, 8));
        Assert.Equal("5:frame|28:retained-csharp-reference-v1|20:document_fingerprint|64:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef|7:ordinal|0;|22:relationship_kind_code|1;|21:source_symbol_ordinal|-;|14:target_display|6:System|16:span_start_utf16|0;|17:span_length_utf16|6;", RetainedCsharpCodeProcessor.ReferenceWireRecord(document, 0, 1, null, "System", 0, 6));
        Assert.Equal("5:frame|29:retained-csharp-diagnostic-v1|20:document_fingerprint|64:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef|7:ordinal|0;|13:diagnostic_id|6:CS0001|13:severity_code|2;|16:span_start_utf16|3;|17:span_length_utf16|4;|14:representation|7:scanned|15:scanned_message|7:warning|15:withheld_reason|-;", RetainedCsharpCodeProcessor.DiagnosticWireRecord(document, 0, "CS0001", 2, 3, 4, "warning", null));
        Assert.Equal("5:frame|37:retained-csharp-blocked-diagnostic-v1|18:source_revision_id|32:11111111222233334444555555555555|24:retained_artifact_sha256|64:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa|22:descriptor_fingerprint|64:7fe4418293ef7cce98be6918c1aa393e8b19cbc33404488ec3134648661420cd|18:parser_fingerprint|64:b824392bda99068d236ce8b7bf1d079d806c66d2ba892193a42b316f1f15f1fe|7:ordinal|0;|13:diagnostic_id|6:CS1513|13:severity_code|3;|16:span_start_utf16|20;|17:span_length_utf16|0;|14:representation|8:withheld|15:scanned_message|-;|15:withheld_reason|23:secret-content-withheld", RetainedCsharpCodeProcessor.BlockedDiagnosticWireRecord(revision, new string('a', 64), 0, "CS1513", 3, 20, 0, null, "secret-content-withheld"));
        Assert.Equal("5:frame|29:retained-csharp-completion-v1|20:document_fingerprint|64:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef|18:parser_fingerprint|64:abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789|19:symbol_fingerprints|1;64:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa|22:reference_fingerprints|0;|23:diagnostic_fingerprints|0;|21:withheld_symbol_count|1;|24:withheld_reference_count|2;|25:withheld_diagnostic_count|3;|24:receipt_diagnostic_codes|1;6:CS0001", RetainedCsharpCodeProcessor.CompletionWireRecord(document, parser, [new string('a', 64)], [], [], 1, 2, 3, ["CS0001"]));
        Assert.Equal("5:frame|37:retained-csharp-blocked-completion-v1|18:source_revision_id|32:11111111222233334444555555555555|24:retained_artifact_sha256|64:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa|22:descriptor_fingerprint|64:7fe4418293ef7cce98be6918c1aa393e8b19cbc33404488ec3134648661420cd|18:parser_fingerprint|64:b824392bda99068d236ce8b7bf1d079d806c66d2ba892193a42b316f1f15f1fe|12:outcome_code|26:csharp-code-syntax-invalid|31:blocked_diagnostic_fingerprints|1;64:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb|21:withheld_symbol_count|0;|24:withheld_reference_count|0;|25:withheld_diagnostic_count|1;|24:receipt_diagnostic_codes|1;6:CS1513", RetainedCsharpCodeProcessor.BlockedCompletionWireRecord(revision, new string('a', 64), [new string('b', 64)], 1, ["CS1513"]));

        Assert.Equal("bd7f4158f827af8701edff39ed01a5b2d161c75f5f60a06e5e50b3d2c43d8a09",
            RetainedCsharpCodeProcessor.ComputeDocumentFingerprint(revision, new string('a', 64), parser));
        Assert.Equal("aea8991242015a1c36024316d73e6fb1486adcaed3bc88a38e8114049d2abc78",
            RetainedCsharpCodeProcessor.ComputeSymbolFingerprint(document, 2, 11, "M", "global::N.C.M", "public void M()", "public", 1, 12, 8));
        Assert.Equal("944a3f9414863b5b320f5b43b573b378e5de9c6f8b8f303fc702d7cf1c658284",
            RetainedCsharpCodeProcessor.ComputeReferenceFingerprint(document, 0, 1, null, "System", 0, 6));
        Assert.Equal("d7bd2ff3f039c1bda801d3a59522f2b1e7704a1e38e630ff996e859d1db1d6aa",
            RetainedCsharpCodeProcessor.ComputeDiagnosticFingerprint(document, 0, "CS0001", 2, 3, 4, "warning", null));
        Assert.Equal("6d998af1214d1af4b8f053eca041b66c6207f70149a20f2df3af32c49eae43cf",
            RetainedCsharpCodeProcessor.ComputeBlockedDiagnosticFingerprint(revision, new string('a', 64), 0, "CS1513", 3, 20, 0, null, "secret-content-withheld"));
        Assert.Equal("758f7cbc1af4eaa5c82830a11fa2752f2bab6bb833a58023f7b7da4c01ba8df6",
            RetainedCsharpCodeProcessor.ComputeCompletionFingerprint(document, parser, [new string('a', 64)], [], [], 1, 2, 3, ["CS0001"]));
        Assert.Equal("9736b8d27a7dbbcd15156fc3193cafb9e394c66f9eb3cd438763d714a57141aa",
            RetainedCsharpCodeProcessor.ComputeBlockedCompletionFingerprint(revision, new string('a', 64), [new string('b', 64)], 1, ["CS1513"]));

        var unicodeWire = RetainedCsharpCodeProcessor.SymbolWireRecord(
            document, 0, 2, "é", "global::é", "class é", "", -1, 0, 7);
        Assert.Contains(
            "|10:local_name|2:é|14:qualified_name|10:global::é|18:rendered_signature|8:class é|",
            unicodeWire,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Processor_output_matches_the_end_to_end_success_golden_vector()
    {
        var (processor, claim, _) = Create("class C { }");

        var completion = await processor.ProcessAsync(claim, CancellationToken.None);

        Assert.Equal("81ba13294b7a8c5fc2ed62707ab11a2e7da71c784c459b4c535fea33fadade4e", completion.DocumentFingerprint);
        Assert.Equal("44bfadce904649d18d54151701a58ea7fa9533d1261ecb5504f545639c45d4d7", Assert.Single(completion.Symbols).SymbolFingerprint);
        Assert.Equal("3a69851e5df1ec85d049c127c22239f549e441dd832e12c8223a1ee6d64fef2d", completion.CompletionFingerprint);
    }

    [Fact]
    public async Task Canonical_grammar_removes_comments_preserves_literal_whitespace_and_emits_complete_lexical_names()
    {
        const string source = """
            namespace Demo;
            static /* removed */ public partial class Box<T>
            {
                static public readonly int first = 1, second = 2;
                public string M<U>(string value = "a  b") where U : class
                {
                    int Local(int number = 1) => number;
                    return value;
                }
                public enum Shade { Red = 1 }
            }
            """;
        var (processor, claim, _) = Create(source);

        var completion = await processor.ProcessAsync(claim, CancellationToken.None);

        Assert.Equal("success", completion.OutcomeCode);
        Assert.Collection(completion.Symbols,
            symbol => AssertSymbol(symbol, 1, "Demo", "global::Demo", "namespace Demo", "", -1),
            symbol => AssertSymbol(symbol, 2, "Box", "global::Demo.Box[T]", "public static partial class Box<T>", "public static partial", 0),
            symbol => AssertSymbol(symbol, 17, "first", "global::Demo.Box[T].first", "public static readonly int first", "public static readonly", 1),
            symbol => AssertSymbol(symbol, 17, "second", "global::Demo.Box[T].second", "public static readonly int second", "public static readonly", 1),
            symbol => AssertSymbol(symbol, 11, "M", "global::Demo.Box[T].M", "public string M<U>(string value = \"a  b\") where U : class", "public", 1),
            symbol => AssertSymbol(symbol, 19, "value", "global::Demo.Box[T].M.value", "string value = \"a  b\"", "", 4),
            symbol => AssertSymbol(symbol, 20, "Local", "global::Demo.Box[T].M.Local", "int Local(int number = 1)", "", 4),
            symbol => AssertSymbol(symbol, 19, "number", "global::Demo.Box[T].M.Local.number", "int number = 1", "", 6),
            symbol => AssertSymbol(symbol, 7, "Shade", "global::Demo.Box[T].Shade", "public enum Shade", "public", 1),
            symbol => AssertSymbol(symbol, 18, "Red", "global::Demo.Box[T].Shade.Red", "Red", "", 8));
        Assert.DoesNotContain(completion.Symbols, symbol => symbol.RenderedSignature.Contains("removed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Canonical_grammar_removes_internal_signature_comments_without_changing_string_token_text()
    {
        var (processor, claim, _) = Create(
            "class C { public string M(/* removed */ string value = \"/* keep */\") => value; }");

        var completion = await processor.ProcessAsync(claim, CancellationToken.None);

        var method = Assert.Single(
            completion.Symbols,
            value => value.DeclarationKindCode == 11);
        Assert.Equal(
            "public string M(string value = \"/* keep */\")",
            method.RenderedSignature);
    }

    [Fact]
    public async Task Qualified_names_cover_global_nested_explicit_interface_and_fixed_member_forms()
    {
        const string source = """
            interface IFace { void Run(int p); }
            class Outer<T>
            {
                class Inner<U> : IFace
                {
                    Inner() { }
                    ~Inner() { }
                    int this[int index] => index;
                    void IFace.Run(int p) { }
                    public static Inner<U> operator +(Inner<U> left, Inner<U> right) => left;
                }
            }
            """;
        var (processor, claim, _) = Create(source);

        var completion = await processor.ProcessAsync(claim, CancellationToken.None);

        Assert.Contains(completion.Symbols, value => value.QualifiedName == "global::IFace");
        Assert.Contains(completion.Symbols, value => value.QualifiedName == "global::Outer[T].Inner[U]");
        Assert.Contains(completion.Symbols, value => value.QualifiedName == "global::Outer[T].Inner[U].ctor");
        Assert.Contains(completion.Symbols, value => value.QualifiedName == "global::Outer[T].Inner[U].dtor");
        Assert.Contains(completion.Symbols, value => value.QualifiedName == "global::Outer[T].Inner[U].this");
        Assert.Contains(completion.Symbols, value => value.QualifiedName == "global::Outer[T].Inner[U].IFace.Run");
        Assert.Contains(completion.Symbols, value => value.QualifiedName == "global::Outer[T].Inner[U].operator +");
        Assert.Contains(completion.Symbols, value => value.QualifiedName == "global::Outer[T].Inner[U].IFace.Run.p");
    }

    [Fact]
    public async Task Conversion_operator_qualified_name_uses_the_canonical_operator_token()
    {
        var (processor, claim, _) = Create(
            "class C { public static implicit operator int(C value) => 0; }");

        var completion = await processor.ProcessAsync(claim, CancellationToken.None);

        var conversion = Assert.Single(
            completion.Symbols,
            value => value.DeclarationKindCode == 13);
        Assert.Equal("operator implicit", conversion.LocalName);
        Assert.Equal("global::C.operator implicit", conversion.QualifiedName);
    }

    [Fact]
    public async Task Constructor_signature_removes_the_constructor_initializer()
    {
        var (processor, claim, _) = Create(
            "class B { protected B(int value) { } } class C : B { C() : base(1) { } }");

        var completion = await processor.ProcessAsync(claim, CancellationToken.None);

        var constructor = Assert.Single(
            completion.Symbols,
            value => value.QualifiedName == "global::C.ctor");
        Assert.Equal("C()", constructor.RenderedSignature);
    }

    [Fact]
    public async Task References_follow_source_preorder_and_only_relationships_from_one_node_use_code_order()
    {
        const string source = "using Alpha; class C : Base, IFace { Result M(Input p) { var x = new Created(); Later(); return default!; } }";
        var (processor, claim, _) = Create(source);

        var completion = await processor.ProcessAsync(claim, CancellationToken.None);

        Assert.Equal("using", completion.References[0].RelationshipKind);
        Assert.Equal("Alpha", completion.References[0].TargetDisplay);
        var baseIndex = completion.References.ToList().FindIndex(value => value.TargetDisplay == "Base");
        var interfaceIndex = completion.References.ToList().FindIndex(value => value.TargetDisplay == "IFace");
        var constructionIndex = completion.References.ToList().FindIndex(value => value.RelationshipKind == "object-construction" && value.TargetDisplay == "Created");
        var invocationIndex = completion.References.ToList().FindIndex(value => value.RelationshipKind == "invocation" && value.TargetDisplay == "Later");
        Assert.True(baseIndex > 0);
        Assert.True(interfaceIndex > baseIndex);
        Assert.True(constructionIndex > interfaceIndex);
        Assert.True(invocationIndex > constructionIndex);
        Assert.Equal(Enumerable.Range(0, completion.References.Count), completion.References.Select(value => value.Ordinal));
    }

    [Fact]
    public async Task Type_use_references_are_emitted_only_from_syntactic_type_positions()
    {
        var (processor, claim, _) = Create("class C { Result M(Input value) { Later(); return default!; } }");

        var completion = await processor.ProcessAsync(claim, CancellationToken.None);

        Assert.Contains(completion.References, value => value.RelationshipKind == "type-use" && value.TargetDisplay == "Result");
        Assert.Contains(completion.References, value => value.RelationshipKind == "type-use" && value.TargetDisplay == "Input");
        Assert.Contains(completion.References, value => value.RelationshipKind == "invocation" && value.TargetDisplay == "Later");
        Assert.DoesNotContain(completion.References, value => value.RelationshipKind == "type-use" && value.TargetDisplay == "Later");
    }

    [Fact]
    public async Task Utf8_bom_and_utf16_spans_are_deterministic_and_top_level_reference_uses_null_parent()
    {
        const string source = "using Alias = System.Text; class Café { string Name; }";
        var bytes = RetainedCsharpCodeTestData.Utf8(source, bom: true);
        var claim = RetainedCsharpCodeTestData.Claim(bytes);
        var reader = new RetainedCsharpCodeTestData.Reader(RetainedCsharpCodeTestData.Retained(claim.SourceRevisionId, bytes));
        var processor = new RetainedCsharpCodeProcessor(reader, new RetainedCsharpCodeTestData.Disclosure());

        var completion = await processor.ProcessAsync(claim, CancellationToken.None);

        Assert.Equal("success", completion.OutcomeCode);
        var @class = Assert.Single(completion.Symbols, value => value.DeclarationKind == "class");
        Assert.Equal(source.IndexOf("class", StringComparison.Ordinal), @class.SpanStartUtf16);
        var usingReference = Assert.Single(completion.References, value => value.RelationshipKind == "using");
        Assert.Null(usingReference.SourceSymbolOrdinal);
        Assert.Contains("source_symbol_ordinal|-;", RetainedCsharpCodeProcessor.ReferenceWireRecord(
            completion.DocumentFingerprint!, usingReference.Ordinal, usingReference.RelationshipKindCode,
            usingReference.SourceSymbolOrdinal, usingReference.TargetDisplay, usingReference.SpanStartUtf16,
            usingReference.SpanLengthUtf16), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Processor_reads_only_verified_retained_bytes_and_checks_integrity_before_input_size()
    {
        var oversized = new byte[RetainedCsharpCodeProcessor.MaximumInputBytes + 1];
        Array.Fill(oversized, (byte)' ');
        var claim = RetainedCsharpCodeTestData.Claim(oversized, inputSha256: new string('0', 64));
        var reader = new RetainedCsharpCodeTestData.Reader(RetainedCsharpCodeTestData.Retained(claim.SourceRevisionId, oversized));
        var processor = new RetainedCsharpCodeProcessor(reader, new RetainedCsharpCodeTestData.Disclosure());

        var completion = await processor.ProcessAsync(claim, CancellationToken.None);

        Assert.Equal("retained-artifact-checksum-invalid", completion.OutcomeCode);
        Assert.Equal(1, reader.ReadCount);
        Assert.Empty(completion.Symbols);
        Assert.Null(completion.DocumentFingerprint);
    }

    [Fact]
    public async Task Verified_oversized_input_is_blocked_after_integrity_validation()
    {
        var oversized = new byte[RetainedCsharpCodeProcessor.MaximumInputBytes + 1];
        Array.Fill(oversized, (byte)' ');
        var claim = RetainedCsharpCodeTestData.Claim(oversized);
        var reader = new RetainedCsharpCodeTestData.Reader(RetainedCsharpCodeTestData.Retained(claim.SourceRevisionId, oversized));

        var completion = await new RetainedCsharpCodeProcessor(reader, new RetainedCsharpCodeTestData.Disclosure())
            .ProcessAsync(claim, CancellationToken.None);

        Assert.Equal("csharp-code-input-too-large", completion.OutcomeCode);
    }

    [Theory]
    [InlineData(typeof(FileNotFoundException), "retained-artifact-missing")]
    [InlineData(typeof(InvalidDataException), "retained-artifact-checksum-invalid")]
    [InlineData(typeof(UnauthorizedAccessException), "retained-artifact-path-invalid")]
    public async Task Retained_reader_failures_map_to_fixed_integrity_outcomes(Type exceptionType, string outcome)
    {
        var bytes = "class C { }"u8.ToArray();
        var claim = RetainedCsharpCodeTestData.Claim(bytes);
        var exception = (Exception)Activator.CreateInstance(exceptionType, "synthetic")!;
        var processor = new RetainedCsharpCodeProcessor(
            new RetainedCsharpCodeTestData.ThrowingReader(exception),
            new RetainedCsharpCodeTestData.Disclosure());

        var completion = await processor.ProcessAsync(claim, CancellationToken.None);

        Assert.Equal(outcome, completion.OutcomeCode);
        Assert.Null(completion.DocumentFingerprint);
    }

    [Fact]
    public async Task Syntax_invalid_precedes_diagnostic_count_and_emits_attempt_owned_bounded_diagnostics()
    {
        var source = string.Join('\n', Enumerable.Repeat("class C {", RetainedCsharpCodeProcessor.MaximumDiagnostics + 20));
        var (processor, claim, _) = Create(source);

        var completion = await processor.ProcessAsync(claim, CancellationToken.None);

        Assert.Equal("csharp-code-syntax-invalid", completion.OutcomeCode);
        Assert.Null(completion.DocumentFingerprint);
        Assert.Null(completion.CompletionFingerprint);
        Assert.NotNull(completion.BlockedCompletionFingerprint);
        Assert.InRange(completion.BlockedDiagnostics.Count, 1, RetainedCsharpCodeProcessor.MaximumDiagnostics);
        Assert.All(completion.BlockedDiagnostics, diagnostic => Assert.Equal(claim.AttemptId, diagnostic.AttemptId));
    }

    [Fact]
    public async Task Symbol_and_reference_secrets_are_absent_while_diagnostic_secret_is_withheld()
    {
        const string source = "#warning secret-content-sentinel\nclass C { string M(string value = \"secret-content-sentinel\") { \"secret-content-sentinel\".ToString(); CleanTarget(); return value; } }";
        var bytes = Encoding.UTF8.GetBytes(source);
        var claim = RetainedCsharpCodeTestData.Claim(bytes);
        var reader = new RetainedCsharpCodeTestData.Reader(RetainedCsharpCodeTestData.Retained(claim.SourceRevisionId, bytes));
        var processor = new RetainedCsharpCodeProcessor(reader, new LocalPrivateContentDisclosure());

        var completion = await processor.ProcessAsync(claim, CancellationToken.None);

        Assert.Equal("success", completion.OutcomeCode);
        Assert.DoesNotContain(completion.Symbols, value => value.LocalName == "M" || value.LocalName == "value");
        Assert.DoesNotContain(completion.References, value => value.TargetDisplay.Contains("secret-content-sentinel", StringComparison.Ordinal));
        Assert.True(completion.WithheldSymbolCount >= 1);
        Assert.True(completion.WithheldReferenceCount >= 1);
        Assert.Equal("secret-content-withheld", completion.WithheldSymbolReason);
        Assert.Equal("secret-content-withheld", completion.WithheldReferenceReason);
        var diagnostic = Assert.Single(completion.Diagnostics, value => value.DiagnosticId == "CS1030");
        Assert.True(diagnostic.Withheld);
        Assert.Null(diagnostic.ScannedMessage);
        Assert.Equal("secret-content-withheld", diagnostic.WithheldReason);
        Assert.All(completion.Symbols, value => Assert.Matches("^[0-9a-f]{64}$", value.SymbolFingerprint));
        Assert.All(completion.References, value => Assert.Matches("^[0-9a-f]{64}$", value.ReferenceFingerprint));
    }

    [Fact]
    public async Task Every_persisted_source_derived_symbol_and_reference_field_is_scanned()
    {
        const string source = "public static class Sample { public static void Run() { Target(); } }";
        var disclosure = new RetainedCsharpCodeTestData.Disclosure();
        var (processor, claim, _) = Create(source, disclosure);

        await processor.ProcessAsync(claim, CancellationToken.None);

        Assert.Contains(("Sample", LocalDisclosureKind.Symbol), disclosure.Calls);
        Assert.Contains(("global::Sample", LocalDisclosureKind.Symbol), disclosure.Calls);
        Assert.Contains(("public static class Sample", LocalDisclosureKind.Symbol), disclosure.Calls);
        Assert.Contains(("public static", LocalDisclosureKind.Symbol), disclosure.Calls);
        Assert.Contains(("invocation", LocalDisclosureKind.Reference), disclosure.Calls);
        Assert.Contains(("Target", LocalDisclosureKind.Reference), disclosure.Calls);
    }

    [Fact]
    public async Task Secret_scan_failure_blocks_the_entire_completion_without_generated_fact_fingerprints()
    {
        var bytes = "class C { void M() { Target(); } }"u8.ToArray();
        var claim = RetainedCsharpCodeTestData.Claim(bytes);
        var reader = new RetainedCsharpCodeTestData.Reader(RetainedCsharpCodeTestData.Retained(claim.SourceRevisionId, bytes));
        var processor = new RetainedCsharpCodeProcessor(reader, new RetainedCsharpCodeTestData.FailingDisclosure());

        var completion = await processor.ProcessAsync(claim, CancellationToken.None);

        Assert.Equal("csharp-code-secret-scan-failed", completion.OutcomeCode);
        Assert.Null(completion.DocumentFingerprint);
        Assert.Null(completion.CompletionFingerprint);
        Assert.Null(completion.BlockedCompletionFingerprint);
        Assert.Empty(completion.Symbols);
        Assert.Empty(completion.References);
        Assert.Empty(completion.Diagnostics);
    }

    [Fact]
    public async Task A_withheld_fact_still_scans_every_source_derived_field_and_later_scan_failure_blocks()
    {
        var disclosure = new RetainedCsharpCodeTestData.Disclosure(evaluate: (value, _) => value switch
        {
            "C" => new LocalDisclosureResult(null, true, "secret-content-withheld"),
            "global::C" => throw new InvalidOperationException("synthetic later-field failure"),
            _ => new LocalDisclosureResult(value, false, null)
        });
        var (processor, claim, _) = Create("class C { }", disclosure);

        var completion = await processor.ProcessAsync(claim, CancellationToken.None);

        Assert.Equal("csharp-code-secret-scan-failed", completion.OutcomeCode);
        Assert.Empty(completion.Symbols);
        Assert.Null(completion.DocumentFingerprint);
        Assert.Contains(("C", LocalDisclosureKind.Symbol), disclosure.Calls);
        Assert.Contains(("global::C", LocalDisclosureKind.Symbol), disclosure.Calls);
    }

    [Fact]
    public async Task Cancellation_during_reference_traversal_is_observed_without_a_completion()
    {
        using var cancellation = new CancellationTokenSource();
        var referenceScans = 0;
        var disclosure = new RetainedCsharpCodeTestData.Disclosure(afterEvaluate: (_, kind) =>
        {
            if (kind == LocalDisclosureKind.Reference && ++referenceScans == 2) cancellation.Cancel();
        });
        var (processor, claim, _) = Create("class C { void M() { First(); Second(); Third(); } }", disclosure);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await processor.ProcessAsync(claim, cancellation.Token));
    }

    [Fact]
    public async Task Cancellation_during_the_final_reference_scan_is_observed_without_a_completion()
    {
        using var cancellation = new CancellationTokenSource();
        var disclosure = new RetainedCsharpCodeTestData.Disclosure(afterEvaluate: (value, kind) =>
        {
            if (kind == LocalDisclosureKind.Reference && value == "Target") cancellation.Cancel();
        });
        var (processor, claim, _) = Create("class C { void M() { Target(); } }", disclosure);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await processor.ProcessAsync(claim, cancellation.Token));
    }

    [Fact]
    public void Preflight_rejects_handler_version_absence_descriptor_and_forbidden_service_mismatches()
    {
        Assert.True(new RetainedCsharpCodeProcessor(null!, null!).Preflight().IsAvailable);
        Assert.False(RetainedCsharpCodeProcessor.Preflight("other-handler", "5.0.0.0", true, false,
            RetainedCsharpCodeProcessor.Capability.ProcessorFingerprint).IsAvailable);
        Assert.False(RetainedCsharpCodeProcessor.Preflight(RetainedCsharpCodeProcessor.HandlerImplementationId, "5.0.1.0", true, false,
            RetainedCsharpCodeProcessor.Capability.ProcessorFingerprint).IsAvailable);
        Assert.False(RetainedCsharpCodeProcessor.Preflight(RetainedCsharpCodeProcessor.HandlerImplementationId, "", true, false,
            RetainedCsharpCodeProcessor.Capability.ProcessorFingerprint).IsAvailable);
        Assert.False(RetainedCsharpCodeProcessor.Preflight(RetainedCsharpCodeProcessor.HandlerImplementationId, "5.0.0.0", true, true,
            RetainedCsharpCodeProcessor.Capability.ProcessorFingerprint).IsAvailable);
        Assert.False(RetainedCsharpCodeProcessor.Preflight(RetainedCsharpCodeProcessor.HandlerImplementationId, "5.0.0.0", true, false,
            new string('0', 64)).IsAvailable);
    }

    [Fact]
    public async Task Hosted_csharp_activation_requests_at_most_eight_claims_per_pass()
    {
        var branches = new ClaimCapBranchStore();
        var reader = new RetainedCsharpCodeTestData.Reader(
            RetainedCsharpCodeTestData.Retained(
                RetainedCsharpCodeTestData.FixedSourceRevisionId,
                Encoding.UTF8.GetBytes("class ClaimCap { }")));
        var processor = new RetainedCsharpCodeProcessor(reader, new LocalPrivateContentDisclosure());
        var activation = new RetainedProcessorActivationService(
            new SourceCapabilityService(
                new RunnableCapabilityStore(),
                new LocalSourceCapabilityHandlerRegistry([new RetainedCsharpCodeCapabilityHandler()])),
            branches,
            reader,
            new ZipArchiveRetainedProcessor(null!),
            new RetainedProcessorOptions
            {
                CsharpCodeEnabled = true,
                AutomaticReplayBatchSize = 16
            },
            TimeProvider.System,
            csharpProcessor: processor);

        var result = await activation.RunOnceAsync(CancellationToken.None);

        Assert.True(result.Enabled);
        Assert.Equal(8, branches.CsharpClaimMaximumCount);
    }

    [Fact]
    public void Shared_retained_processor_default_batch_remains_sixteen()
    {
        Assert.Equal(16, new RetainedProcessorOptions().AutomaticReplayBatchSize);
    }

    [Fact]
    public async Task Claim_identity_is_copied_to_success_and_blocked_completions()
    {
        var success = Create("class C { }");
        var successCompletion = await success.Processor.ProcessAsync(success.Claim, CancellationToken.None);
        var blocked = Create("class C {");
        var blockedCompletion = await blocked.Processor.ProcessAsync(blocked.Claim, CancellationToken.None);

        Assert.Equal(success.Claim.BranchId, successCompletion.BranchId);
        Assert.Equal(success.Claim.AttemptId, successCompletion.AttemptId);
        Assert.Equal(success.Claim.LeaseGeneration, successCompletion.LeaseGeneration);
        Assert.Equal(blocked.Claim.AttemptId, blockedCompletion.AttemptId);
        Assert.All(blockedCompletion.BlockedDiagnostics, value => Assert.Equal(blocked.Claim.AttemptId, value.AttemptId));
    }

    private sealed class RunnableCapabilityStore : ISourceCapabilityStore
    {
        public ValueTask<RegisteredSourceCapability> RegisterAsync(
            RegisteredSourceCapability capability,
            CancellationToken cancellationToken) => ValueTask.FromResult(capability);

        public ValueTask<RegisteredSourceCapability?> FindAsync(
            Guid capabilityId,
            CancellationToken cancellationToken) => ValueTask.FromResult<RegisteredSourceCapability?>(null);
    }

    private sealed class ClaimCapBranchStore : IRetainedProcessorBranchStore
    {
        public int CsharpClaimMaximumCount { get; private set; }

        public ValueTask<bool> IsRetainedCsharpCodeWriterReadyAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(true);

        public ValueTask<IReadOnlyList<RetainedCsharpCodeClaim>> ClaimCsharpCodeAsync(
            string leaseOwner,
            int maximumCount,
            string processorFingerprint,
            CancellationToken cancellationToken)
        {
            CsharpClaimMaximumCount = maximumCount;
            return ValueTask.FromResult<IReadOnlyList<RetainedCsharpCodeClaim>>([]);
        }

        public ValueTask<IReadOnlyList<RetainedProcessorPromotionCandidate>> ReadPromotionCandidatesAsync(
            int maximumCount,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlyList<RetainedProcessorPromotionCandidate>>([]);

        public ValueTask<bool> PromoteAsync(
            RetainedProcessorPromotionCandidate candidate,
            SourceCapabilityDescriptor capability,
            CancellationToken cancellationToken) => throw new Xunit.Sdk.XunitException("No promotion candidate was returned.");

        public ValueTask<bool> BlockPromotionAsync(
            RetainedProcessorPromotionCandidate candidate,
            string outcomeCode,
            CancellationToken cancellationToken) => throw new Xunit.Sdk.XunitException("No promotion candidate was returned.");

        public ValueTask<IReadOnlyList<RetainedProcessorClaim>> ClaimAsync(
            string leaseOwner,
            int maximumCount,
            CancellationToken cancellationToken) => throw new Xunit.Sdk.XunitException("Generic claims must not consume C# work.");

        public ValueTask<bool> CommitAsync(
            RetainedProcessorClaim claim,
            RetainedProcessorCompletion completion,
            CancellationToken cancellationToken) => throw new Xunit.Sdk.XunitException("No generic claim was returned.");

        public ValueTask<bool> RetryAsync(
            RetainedProcessorClaim claim,
            string outcomeCode,
            CancellationToken cancellationToken) => throw new Xunit.Sdk.XunitException("No claim was returned.");

        public ValueTask<bool> FailAsync(
            RetainedProcessorClaim claim,
            RetainedProcessorFailure failure,
            CancellationToken cancellationToken) => throw new Xunit.Sdk.XunitException("No claim was returned.");
    }

    [Fact]
    public async Task Declaration_and_reference_kind_codes_are_pinned_for_every_supported_fact_family()
    {
        const string source = """
            using System;
            namespace N
            {
                [Attr] class C : Base, IFace
                {
                    C() { }
                    ~C() { }
                    void M(int p) { int Local(int q) => q; new Created(); Call(); }
                    public static C operator +(C left, C right) => left;
                    public static implicit operator int(C value) => 0;
                    int Property { get; }
                    int this[int index] => index;
                    event Action Event { add { } remove { } }
                    event Action EventField;
                    int Field;
                    enum Nested { Member }
                }
                struct S { }
                interface I { }
                record R;
                record struct RS;
                enum E { A }
                delegate void D(int value);
            }
            """;
        var (processor, claim, _) = Create(source);

        var completion = await processor.ProcessAsync(claim, CancellationToken.None);

        var kinds = completion.Symbols
            .GroupBy(value => value.DeclarationKind)
            .ToDictionary(group => group.Key, group => group.First().DeclarationKindCode, StringComparer.Ordinal);
        Assert.Equal(Enumerable.Range(1, 20), kinds.Values.Order());
        Assert.Equal(1, kinds["namespace"]);
        Assert.Equal(5, kinds["record-class"]);
        Assert.Equal(6, kinds["record-struct"]);
        Assert.Equal(12, kinds["operator"]);
        Assert.Equal(13, kinds["conversion-operator"]);
        Assert.Equal(16, kinds["event"]);
        Assert.Equal(19, kinds["parameter"]);
        Assert.Equal(20, kinds["local-function"]);

        var relationships = completion.References
            .GroupBy(value => value.RelationshipKind)
            .ToDictionary(group => group.Key, group => group.First().RelationshipKindCode, StringComparer.Ordinal);
        Assert.Equal(Enumerable.Range(1, 7), relationships.Values.Order());
        Assert.Equal(1, relationships["using"]);
        Assert.Equal(2, relationships["base-type"]);
        Assert.Equal(3, relationships["implemented-interface"]);
        Assert.Equal(4, relationships["attribute"]);
        Assert.Equal(5, relationships["type-use"]);
        Assert.Equal(6, relationships["object-construction"]);
        Assert.Equal(7, relationships["invocation"]);
    }

    [Fact]
    public async Task Invalid_utf8_is_blocked_before_text_and_parser_work()
    {
        var bytes = new byte[] { 0xc3, 0x28 };
        var claim = RetainedCsharpCodeTestData.Claim(bytes);
        var reader = new RetainedCsharpCodeTestData.Reader(RetainedCsharpCodeTestData.Retained(claim.SourceRevisionId, bytes));
        var disclosure = new RetainedCsharpCodeTestData.Disclosure();

        var completion = await new RetainedCsharpCodeProcessor(reader, disclosure)
            .ProcessAsync(claim, CancellationToken.None);

        Assert.Equal("csharp-code-input-not-utf8", completion.OutcomeCode);
        Assert.Empty(disclosure.Calls);
    }

    [Fact]
    public async Task Decoded_utf16_limit_is_enforced_after_strict_utf8()
    {
        var bytes = Enumerable.Repeat((byte)' ', RetainedCsharpCodeProcessor.MaximumDecodedUtf16CodeUnits + 1).ToArray();
        var claim = RetainedCsharpCodeTestData.Claim(bytes);
        var reader = new RetainedCsharpCodeTestData.Reader(RetainedCsharpCodeTestData.Retained(claim.SourceRevisionId, bytes));

        var completion = await new RetainedCsharpCodeProcessor(reader, new RetainedCsharpCodeTestData.Disclosure())
            .ProcessAsync(claim, CancellationToken.None);

        Assert.Equal("csharp-code-text-limit", completion.OutcomeCode);
    }

    [Fact]
    public async Task Node_limit_precedes_depth_when_the_first_overlimit_node_exceeds_both()
    {
        var source = "class C { void M() { " +
            new string(';', RetainedCsharpCodeProcessor.MaximumSyntaxNodes) +
            new string('{', RetainedCsharpCodeProcessor.MaximumNestingDepth + 1) +
            new string('}', RetainedCsharpCodeProcessor.MaximumNestingDepth + 1) +
            " } }";
        var (processor, claim, _) = Create(source);

        var completion = await processor.ProcessAsync(claim, CancellationToken.None);

        Assert.Equal("csharp-code-node-limit", completion.OutcomeCode);
    }

    [Fact]
    public async Task Depth_limit_is_enforced_when_node_count_is_within_bounds()
    {
        var source = "class C { void M() { " +
            new string('{', RetainedCsharpCodeProcessor.MaximumNestingDepth + 1) +
            new string('}', RetainedCsharpCodeProcessor.MaximumNestingDepth + 1) +
            " } }";
        var (processor, claim, _) = Create(source);

        var completion = await processor.ProcessAsync(claim, CancellationToken.None);

        Assert.Equal("csharp-code-depth-limit", completion.OutcomeCode);
    }

    [Fact]
    public async Task Identifier_limit_precedes_signature_limit_on_the_same_declaration()
    {
        var identifier = new string('A', RetainedCsharpCodeProcessor.MaximumIdentifierUtf16CodeUnits + 1);
        var (processor, claim, _) = Create("class " + identifier + " { }");

        var completion = await processor.ProcessAsync(claim, CancellationToken.None);

        Assert.Equal("csharp-code-identifier-limit", completion.OutcomeCode);
    }

    [Fact]
    public async Task Identifier_limit_scans_every_roslyn_identifier_token_before_fact_collection()
    {
        var identifier = new string('A', RetainedCsharpCodeProcessor.MaximumIdentifierUtf16CodeUnits + 1);
        var source = "class C { " +
            "System.Collections.Generic.List<" + identifier + "> field; " +
            "void M() { " + identifier + "<int>(); } " +
            "}";
        var (processor, claim, _) = Create(source);

        var completion = await processor.ProcessAsync(claim, CancellationToken.None);

        Assert.Equal("csharp-code-identifier-limit", completion.OutcomeCode);
        Assert.Null(completion.DocumentFingerprint);
        Assert.Null(completion.CompletionFingerprint);
        Assert.Empty(completion.Symbols);
        Assert.Empty(completion.References);
    }

    [Fact]
    public async Task Node_limit_precedes_identifier_token_scan_even_when_the_long_identifier_comes_first()
    {
        var identifier = new string('A', RetainedCsharpCodeProcessor.MaximumIdentifierUtf16CodeUnits + 1);
        var source = "class C { void M() { " + identifier + "(); " +
            new string(';', RetainedCsharpCodeProcessor.MaximumSyntaxNodes) +
            " } }";
        var (processor, claim, _) = Create(source);

        var completion = await processor.ProcessAsync(claim, CancellationToken.None);

        Assert.Equal("csharp-code-node-limit", completion.OutcomeCode);
        Assert.Empty(completion.Symbols);
        Assert.Empty(completion.References);
    }

    [Fact]
    public async Task Depth_limit_precedes_identifier_token_scan_even_when_the_long_identifier_comes_first()
    {
        var identifier = new string('A', RetainedCsharpCodeProcessor.MaximumIdentifierUtf16CodeUnits + 1);
        var source = "class C { void M() { " + identifier + "(); " +
            new string('{', RetainedCsharpCodeProcessor.MaximumNestingDepth + 1) +
            new string('}', RetainedCsharpCodeProcessor.MaximumNestingDepth + 1) +
            " } }";
        var (processor, claim, _) = Create(source);

        var completion = await processor.ProcessAsync(claim, CancellationToken.None);

        Assert.Equal("csharp-code-depth-limit", completion.OutcomeCode);
        Assert.Empty(completion.Symbols);
        Assert.Empty(completion.References);
    }

    [Fact]
    public async Task Signature_limit_preserves_default_expression_before_blocking()
    {
        var literal = new string('x', RetainedCsharpCodeProcessor.MaximumSignatureUtf16CodeUnits + 1);
        var (processor, claim, _) = Create("class C { string M(string value = \"" + literal + "\") => value; }");

        var completion = await processor.ProcessAsync(claim, CancellationToken.None);

        Assert.Equal("csharp-code-signature-limit", completion.OutcomeCode);
    }

    [Fact]
    public async Task Symbol_limit_is_enforced_before_reference_collection()
    {
        var fields = string.Join(' ', Enumerable.Range(0, RetainedCsharpCodeProcessor.MaximumSymbols)
            .Select(value => "int F" + value.ToString(System.Globalization.CultureInfo.InvariantCulture) + ";"));
        var (processor, claim, _) = Create("class C { " + fields + " }");

        var completion = await processor.ProcessAsync(claim, CancellationToken.None);

        Assert.Equal("csharp-code-symbol-limit", completion.OutcomeCode);
    }

    [Fact]
    public async Task Reference_limit_is_reachable_and_precedes_diagnostic_count()
    {
        var arguments = string.Join(',', Enumerable.Repeat("new C()", (RetainedCsharpCodeProcessor.MaximumReferences / 2) + 1));
        var warnings = string.Join('\n', Enumerable.Repeat("#warning bounded-warning", RetainedCsharpCodeProcessor.MaximumDiagnostics + 1));
        var source = warnings + "\nclass C { object[] Values = new object[] { " + arguments + " }; }";
        var (processor, claim, _) = Create(source);

        var completion = await processor.ProcessAsync(claim, CancellationToken.None);

        Assert.Equal("csharp-code-reference-limit", completion.OutcomeCode);
    }

    [Fact]
    public async Task Diagnostic_limit_precedes_secret_scan_failure()
    {
        var warnings = string.Join('\n', Enumerable.Repeat("#warning bounded-warning", RetainedCsharpCodeProcessor.MaximumDiagnostics + 1));
        var bytes = Encoding.UTF8.GetBytes(warnings + "\nclass C { }");
        var claim = RetainedCsharpCodeTestData.Claim(bytes);
        var reader = new RetainedCsharpCodeTestData.Reader(RetainedCsharpCodeTestData.Retained(claim.SourceRevisionId, bytes));

        var completion = await new RetainedCsharpCodeProcessor(reader, new RetainedCsharpCodeTestData.FailingDisclosure())
            .ProcessAsync(claim, CancellationToken.None);

        Assert.Equal("csharp-code-diagnostic-limit", completion.OutcomeCode);
    }

    [Fact]
    public async Task Diagnostic_messages_are_bounded_to_the_descriptor_utf16_limit_before_scanning()
    {
        var detail = new string('x', RetainedCsharpCodeProcessor.MaximumDiagnosticMessageUtf16CodeUnits + 500);
        var (processor, claim, _) = Create("#warning " + detail + "\nclass C { }");

        var completion = await processor.ProcessAsync(claim, CancellationToken.None);

        var diagnostic = Assert.Single(completion.Diagnostics, value => value.DiagnosticId == "CS1030");
        Assert.Equal(RetainedCsharpCodeProcessor.MaximumDiagnosticMessageUtf16CodeUnits, diagnostic.ScannedMessage!.Length);
    }

    [Fact]
    public async Task Diagnostic_message_bound_never_splits_a_utf16_surrogate_pair()
    {
        var padding = new string('x', RetainedCsharpCodeProcessor.MaximumDiagnosticMessageUtf16CodeUnits - 12);
        var (processor, claim, _) = Create("#warning " + padding + "😀tail\nclass C { }");

        var completion = await processor.ProcessAsync(claim, CancellationToken.None);

        var message = Assert.Single(completion.Diagnostics, value => value.DiagnosticId == "CS1030").ScannedMessage!;
        var encoded = new UnicodeEncoding(bigEndian: false, byteOrderMark: false, throwOnInvalidBytes: true)
            .GetBytes(message);
        Assert.NotEmpty(encoded);
    }

    private static (RetainedCsharpCodeProcessor Processor, RetainedCsharpCodeClaim Claim, RetainedCsharpCodeTestData.Reader Reader) Create(
        string source,
        RetainedCsharpCodeTestData.Disclosure? disclosure = null)
    {
        var bytes = Encoding.UTF8.GetBytes(source);
        var claim = RetainedCsharpCodeTestData.Claim(bytes);
        var reader = new RetainedCsharpCodeTestData.Reader(RetainedCsharpCodeTestData.Retained(claim.SourceRevisionId, bytes));
        return (new RetainedCsharpCodeProcessor(reader, disclosure ?? new RetainedCsharpCodeTestData.Disclosure()), claim, reader);
    }

    private static void AssertSymbol(
        RetainedCsharpCodeSymbol symbol,
        int kind,
        string local,
        string qualified,
        string signature,
        string modifiers,
        int parent)
    {
        Assert.Equal(kind, symbol.DeclarationKindCode);
        Assert.Equal(local, symbol.LocalName);
        Assert.Equal(qualified, symbol.QualifiedName);
        Assert.Equal(signature, symbol.RenderedSignature);
        Assert.Equal(modifiers, symbol.Modifiers);
        Assert.Equal(parent, symbol.LexicalParentOrdinal);
    }
}
