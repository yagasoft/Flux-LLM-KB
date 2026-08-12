# Phase 5 retained C# code processor design

## Decision

Phase 5 Task 5 adds the first retained-code capability: deterministic parsing of
verified retained `.cs` artifacts with the already locked
`Microsoft.CodeAnalysis.CSharp` 5.0.0 package.  It is a syntax-only Roslyn
processor: it does not load a solution, project, assembly, analyser, source
generator or reference graph; execute user code; make a network request; or
reread a source original.  Other code extensions remain their existing durable
deferred classification until a later parser-specific design is approved.

The capability descriptor is exact: identifier
`08dd66fb-6502-4b31-a4a5-51e8cc66f916`; processor kind
`retained-csharp-code`; `ProcessorVersion`
`phase-5-retained-csharp-code-v1`; `ExecutionClass.InProcess`; accepted activity
kind `SourceActivityKind.CodeParsing`; accepted classification
`AcceptedUtf8Text`; and output contract `retained:csharp-code-facts-v1`. The
handler implementation identifier is `retained-csharp-roslyn-syntax-v1`; it is
part of the parser fingerprint, not an undocumented alternate descriptor field.
The activity is the existing zero-based enum member
`SourceActivityKind.CodeParsing (5)`; the descriptor wire value is therefore
the invariant numeric field `5;`.

`ProcessorFingerprint` is the lower-case hexadecimal SHA-256 of one exact wire
record.  It has no BOM, Unicode normalisation, trimming, culture formatting or
terminal separator. A textual field is ASCII `<UTF-8-byte-count>`, `:`, then
exactly that many UTF-8 bytes. A numeric field is invariant ASCII decimal digits
(with `-` only where a signed field explicitly permits it), followed by `;`.
Each record entry is the textual wire-field name, `|`, then its typed value;
entries are joined by one further ASCII `|`. Thus the first entry begins
`5:frame|29:retained-csharp-descriptor-v1`. The fields, in this exact order, are:

| Wire name | Type | Exact value |
| --- | --- | --- |
| `frame` | text | `retained-csharp-descriptor-v1` |
| `capability_id` | text | `08dd66fb65024b31a4a551e8cc66f916` (lower-case GUID `N`) |
| `processor_kind` | text | `retained-csharp-code` |
| `processor_version` | text | `phase-5-retained-csharp-code-v1` |
| `execution_class` | number | `0` (`ExecutionClass.InProcess`) |
| `activity_kind` | number | `5` (`SourceActivityKind.CodeParsing (5)`) |
| `accepted_classification` | text | `AcceptedUtf8Text` |
| `output_contract` | text | `retained:csharp-code-facts-v1` |
| `handler_implementation_id` | text | `retained-csharp-roslyn-syntax-v1` |
| `roslyn_assembly_version` | text | `5.0.0.0` |
| `language_version` | text | `CSharp14` |
| `utf8_policy` | text | `utf8-strict-optional-bom` |
| `limit_input_bytes` | number | `4194304` |
| `limit_decoded_utf16_code_units` | number | `4000000` |
| `limit_syntax_nodes` | number | `200000` |
| `limit_nesting_depth` | number | `256` |
| `limit_symbols` | number | `20000` |
| `limit_references` | number | `100000` |
| `limit_identifier_utf16_code_units` | number | `1024` |
| `limit_signature_utf16_code_units` | number | `4096` |
| `limit_diagnostics` | number | `256` |
| `limit_diagnostic_message_utf16_code_units` | number | `1024` |

The wire names and their table order are serialized and are part of the record.
Counts and lengths are UTF-16 code units where stated, not Unicode scalar values; in
particular the diagnostic allowance is exactly 256 retained diagnostics with a
maximum scanned message of 1,024 UTF-16 code units each.  The code computes the
canonical value once and compares the complete lower-case 64-hex string at
preflight and registration; it must not concatenate display strings or use a
different descriptor-hash convention. The local hosted activation may be
configured enabled by default and processes at most eight claims per pass, but
it cannot register, promote or claim C# work until the additive schema, complete
writer and handler registration below have succeeded. It remains inert until a
local deployment is separately approved.

## Classification, preflight and input

At the current base, `SourceClassifier` sees `.cs` in `CodeExtensions` and
returns `DeferredPolicy` (`Source code ingestion is not enabled for this root`).
`SourceScanWorker` consequently makes a `DocumentParsing` deferred-policy row;
the normal accepted-text branch instead always creates a `TextExtraction` row.
The current retained promotion/replay path only examines
`DeferredUnsupported` `TextExtraction` rows.  Those three behaviours do not
form a runnable C# path and must not be papered over by registering a handler.

The implementation changes that planner as one atomic contract.  After binary
signature/control-byte rejection and the existing bounded full-buffer and
strict-UTF-8 probe, `SourceClassifier` returns `AcceptedUtf8Text` for `.cs`
only; it keeps every other `CodeExtensions` member as `DeferredPolicy`.  The
root's existing text/plain allowance still applies.  For an allowed accepted
`.cs`, `SourceScanWorker` takes an exclusive C# branch and creates only a
`CodeParsing`/`InProcess` activity with the C# processor version, descriptor
fingerprint and retained SHA-256 input fingerprint; it never creates a
`TextExtraction` activity for that revision.  If the root disallows the text
classification, it creates `CodeParsing` `DeferredPolicy` with the existing
root-policy reason, not a text activity.  Binary-like or invalid UTF-8 `.cs`
remain deferred/blocked under the existing classifier reason and never become
C# claims.  A C# activity initially uses `DeferredUnsupported`, required
capability `retained-csharp-code`, and reason `csharp-code-writer-not-ready`.

There is a schema barrier around that final activity.  Before the additive
`DescriptorFingerprint` migration and completion writer are durably ready,
`SourceScanWorker` must not attempt to persist a descriptor-bearing
`CodeParsing` row in the legacy schema.  It instead creates/reuses only an
inert `DocumentParsing`/`DeferredUnsupported` holding row with required
capability `retained-csharp-code` and reason `csharp-code-writer-not-ready`.
That holding row is deliberately outside the `TextExtraction` replay predicate
and must have no claim path.  After readiness, the serialisable replan replaces
that holding route with the exact C# activity identity below; it never lets an
accepted `.cs` fall through to `TextExtraction` during either phase.

`SourceScanWorker` must receive a C#-aware activity planner/descriptor provider
rather than reuse `RetainedTextActivityPlanner`.  That planner verifies the
exact capability ID, version, descriptor fingerprint, `CodeParsing`,
`AcceptedUtf8Text` and output contract before it promotes a row.  Its only
eligible source state is the above C# `DeferredUnsupported` row.  It may offer
the row only after the database reports the additive schema version and a
complete completion writer as ready, the registration is runnable and matches
the handler, and preflight succeeds.  Until all are true it returns an inert
no-claim result and leaves the row `DeferredUnsupported`; it must not fall back
to text extraction, a generic retained promotion, or a retry loop.

Existing `.cs` rows are replanned by a one-shot, serialisable
`RetainedCsharpActivityReplanService` only after the schema/writer readiness
gate is true.  It selects source revisions by their persisted discovered leaf
name ending in `.cs`, joins the exact revision-to-retained-artifact binding, and
re-verifies the retained artifact ID, private-root binding, byte length and
SHA-256 before creating the new activity.  It uses the identity `(SourceRevisionId,
CodeParsing, phase-5-retained-csharp-code-v1, DescriptorFingerprint,
RetainedArtifactSha256)` and a unique insert/re-read, so restart/replay returns
the same C# row.  It preserves historical `DocumentParsing`/`DeferredPolicy`
rows for audit.  A legacy *active* `TextExtraction` row for the same revision
and hash is first fenced so it has no lease, no pipeline receipt and no running
claim, then is moved to `DeferredPolicy` with
`csharp-code-superseded-text-route`; otherwise replan records
`csharp-code-legacy-text-conflict` and creates/claims nothing.  A completed
historical text receipt is not altered, but no active text claim may coexist
with C# parsing.  The replan never reads a source original and never promotes a
row until writer/schema readiness is durable.

A new activity is idempotent on source revision, activity kind, processor
version, descriptor fingerprint and input fingerprint. A later descriptor
version or fingerprint creates a separate durable activity rather than changing
an old result.

Startup preflight checks that the locally restored Roslyn assembly is exactly
version `5.0.0.0`, `LanguageVersion.CSharp14` is available, the descriptor
fingerprint matches the loaded handler, and no compiler workspace/analyser/
generator service is registered.  It performs no download, network access or
model/runtime activation.  A missing or mismatched parser leaves new work as
`DeferredUnsupported` with `processor-parser-unavailable`; it never claims it.

The processor reads only `IRetainedSourceReader` verified binary bytes.  It
therefore reuses private-root binding, no-follow, content-length and SHA-256
validation, and fails closed if the retained artifact is missing, rebound or
corrupt.  It accepts UTF-8 with an optional UTF-8 BOM, rejects invalid UTF-8,
and uses Roslyn's deterministic parse options fixed at C# 14.

## Bounded syntax facts

The limits are part of the descriptor fingerprint:

| Limit | Exact value | Result when exceeded |
| --- | ---: | --- |
| Retained input bytes | 4 MiB | `csharp-code-input-too-large` blocked |
| Decoded UTF-8 UTF-16 code units | 4,000,000 | `csharp-code-text-limit` blocked |
| Syntax-tree nodes | 200,000 | `csharp-code-node-limit` blocked |
| Syntax nesting depth | 256 | `csharp-code-depth-limit` blocked |
| Declarations/symbol facts | 20,000 | `csharp-code-symbol-limit` blocked |
| Reference facts | 100,000 | `csharp-code-reference-limit` blocked |
| Identifier length | 1,024 characters | `csharp-code-identifier-limit` blocked |
| Rendered signature length | 4,096 characters | `csharp-code-signature-limit` blocked |
| Parser diagnostics retained | 256, each 1,024 characters | `csharp-code-diagnostic-limit` blocked |

The syntax walker emits declarations for namespaces, types, delegates, methods,
constructors, destructors, properties, indexers, events, fields, enum members,
parameters and local functions.  It emits syntactic references for using
directives, base/implemented types, attributes, type uses, object construction
and invocation.  References are syntactic display facts, not semantic binding
claims: the processor never claims that an unresolved identifier denotes a
specific declaration in another file.  Facts preserve source span and lexical
parent ordinal, stable kind, exact local name, qualified name, modifiers and
signature.  No arbitrary full source copy is added to SQL; verified retained
bytes remain authoritative.

### Canonical C# fact grammar

All fact text is produced from Roslyn syntax only, after `WithoutTrivia()` and a
single-space normalisation of token separators. The normaliser never emits a
newline, comment, directive, elastic trivia or source indentation, never
normalises Unicode and never uses semantic symbols. A source span is always
`SpanStart` and `Span.Length` in zero-based UTF-16 code units of the decoded
source; it is never a UTF-8 byte offset or line/column pair. The lexical parent
ordinal is the zero-based stable declaration ordinal of the nearest emitted
declaration ancestor. A top-level declaration has no lexical parent and stores
and fingerprints numeric `-1;`; it must not use a database null, `0`, an empty
string or a culture-formatted sentinel.

Qualified names use these exact forms: a namespace is
`global::<namespace-segment>.<namespace-segment>` (the global namespace alone
is `global::`); a named type appends `.<identifier><type-parameter-list>` to its
containing namespace/type; and a member appends `.<member-local-name>` to its
containing type. Type parameter lists are `[T,U]` with no spaces. Constructors
and destructors use `.ctor` and `.dtor`; indexers use `.this`; operators use the
normalised `operator <token>` name; accessors are not separate declaration
facts. A field declaration with `int a, b;` emits two facts, one per
`VariableDeclarator`, in declarator source order, each with local name `a` or
`b`, the same normalised declared type and its own declarator span. Enum
members, parameters and local functions use the same nearest-emitted-parent
rule; parameters are ordered by parameter-list position.

The rendered signature is the canonical one-line syntax rendering of the
declaration with its body, initializer, attribute lists and semicolon removed,
then with the canonical modifier prefix. The prefix emits present modifiers in
this fixed order, separated by one space: `public`, `protected`, `internal`,
`private`, `file`, `new`, `static`, `abstract`, `sealed`, `virtual`, `override`,
`readonly`, `required`, `unsafe`, `extern`, `partial`, `async`, `ref`, `in`,
`out`, `scoped`, `const`, `volatile`. A modifier absent from syntax is omitted;
the raw source ordering is never preserved. Parameter, type-argument,
constraint, explicit-interface and return/type syntax uses the same one-line
normaliser. Thus a signature is a syntax display fact, not a compiler binding
or a source snippet.

The declaration-kind numeric code is immutable and is part of every symbol
fingerprint: `1=namespace`, `2=class`, `3=struct`, `4=interface`,
`5=record-class`, `6=record-struct`, `7=enum`, `8=delegate`,
`9=constructor`, `10=destructor`, `11=method`, `12=operator`,
`13=conversion-operator`, `14=property`, `15=indexer`, `16=event`,
`17=field`, `18=enum-member`, `19=parameter`, `20=local-function`.
`declaration_kind` is the lower-case ASCII label paired with that code only for
local display; the stable wire field is the numeric `declaration_kind_code`.
The qualified-name form is therefore complete for every emitted kind: namespace
and named-type forms are as above; delegate/type members append their local
name; constructors/destructors/indexers/operators use the fixed local names
above; enum members, parameters and local functions append their identifier to
their lexical containing declaration. Parameters and local functions never use
semantic overload resolution, metadata names, backticks or an inferred return
type. Explicit-interface members append the normalised explicit-interface text,
then `.` and the local member name.  A global-namespace member starts `global::`.

Reference facts have an ordinal independent of symbol ordinals. Their immutable
relationship-kind numeric codes and lower-case display labels are:
`1=using`, `2=base-type`, `3=implemented-interface`, `4=attribute`,
`5=type-use`, `6=object-construction`, `7=invocation`. They are emitted in this
exact relationship-code order within a node; ties use span start, span length and Roslyn
raw-kind integer, then source preorder. `target_display` is the one-line
normalised syntax of the referenced name/type/expression, without trivia. The
optional source-symbol ordinal is the nearest emitted declaration ordinal that
lexically contains the reference; when absent it uses the universal nullable
encoding below, not a numeric zero or SQL null. This same ordering
is the persisted `(DocumentId, Ordinal)` order and the order used by the
completion fingerprint.

Traversal and limit outcomes are deterministic. After retained-binding/checksum
validation, the processor checks input bytes, strict UTF-8/BOM decoding and
decoded-character limit, parses with the fixed options, then evaluates syntax
errors, then walks `DescendantNodesAndSelf()` in Roslyn source-order/pre-order.
For each node it checks node count before depth; the first over-limit node wins
(`csharp-code-node-limit` before `csharp-code-depth-limit` when both would be
crossed at that node). It produces declaration facts before reference facts for
the same node, in the declaration/reference-kind order listed above, and uses
source-span start, span length, Roslyn raw-kind integer and lexical-parent
ordinal as deterministic tie-breakers. It checks identifier and rendered
signature limits before the corresponding fact is emitted; it counts symbols
before references, and the first over-limit fact wins. The complete precedence
order is retained integrity, input bytes, UTF-8, text length, syntax-invalid,
node count, depth, identifier, signature, symbol count, reference count,
diagnostic count, secret-scan failure. A terminal outcome commits no partial
document or fact set.

### Canonical fingerprint wire records

Every fingerprint is lower-case SHA-256 hexadecimal of one record encoded as
UTF-8. There is no BOM, normalisation, trimming, culture formatting or terminal
separator. A record is the ordered concatenation of `field-name|field-value`
entries, with a single ASCII `|` between entries. `field-name` is itself a
UTF-8-length-prefixed text value; all names below are exact lower-case ASCII.
`text` is ASCII decimal UTF-8
byte length without leading zeros (except `0`), ASCII `:`, then exactly that many
bytes. `uint` is ASCII decimal digits without a sign or leading zeros (except
`0`), followed by `;`; `int` is the same or `-` followed by a non-zero digit and
digits, followed by `;` (`-0` is forbidden). `nullable<T>` is `-;` when null and
otherwise the encoding of `T`; it is never an empty text or a display sentinel.
`list<text>` is `uint-count;` followed immediately by that many text values in
ordinal order; an empty list is `0;`. `list<hex64>` uses the same list encoding,
where each item is a 64-byte lower-case ASCII text value. The field schema fixes
the type, so no run-time type tag is emitted. The descriptor's existing table
uses this same grammar; its first entry is exactly `5:frame|29:retained-csharp-descriptor-v1`.

The following exact schemas are the complete fingerprint inputs. Fields appear
in the table order, every value is encoded by its listed type, and no omitted or
future display field participates.

| Record / resulting fingerprint | Ordered fields (`name:type`) |
| --- | --- |
| Parser / `ParserFingerprint` | `frame:text` (`retained-csharp-parser-v1`), `handler_implementation_id:text`, `roslyn_assembly_version:text`, `language_version:text`, `utf8_policy:text`, `fact_normalisation_revision:text` (`without-trivia-one-line-v1`), `traversal_limit_precedence_revision:text` (`source-preorder-v1`), `limit_input_bytes:uint`, `limit_decoded_utf16_code_units:uint`, `limit_syntax_nodes:uint`, `limit_nesting_depth:uint`, `limit_symbols:uint`, `limit_references:uint`, `limit_identifier_utf16_code_units:uint`, `limit_signature_utf16_code_units:uint`, `limit_diagnostics:uint`, `limit_diagnostic_message_utf16_code_units:uint`, `syntax_invalid_outcome:text` (`csharp-code-syntax-invalid`) |
| Document / `DocumentFingerprint` | `frame:text` (`retained-csharp-document-v1`), `source_revision_id:text` (lower-case GUID `N`), `retained_artifact_sha256:text` (64 lower-case ASCII hex), `processor_version:text`, `descriptor_fingerprint:text` (64 lower-case ASCII hex), `parser_fingerprint:text` (64 lower-case ASCII hex) |
| Symbol / `SymbolFingerprint` | `frame:text` (`retained-csharp-symbol-v1`), `document_fingerprint:text`, `ordinal:uint`, `declaration_kind_code:uint`, `local_name:text`, `qualified_name:text`, `rendered_signature:text`, `modifiers:text` (the fixed single-space display string, or `0:`), `lexical_parent_ordinal:int` (`-1` only for a top-level declaration), `span_start_utf16:uint`, `span_length_utf16:uint` |
| Reference / `ReferenceFingerprint` | `frame:text` (`retained-csharp-reference-v1`), `document_fingerprint:text`, `ordinal:uint`, `relationship_kind_code:uint`, `source_symbol_ordinal:nullable<uint>`, `target_display:text`, `span_start_utf16:uint`, `span_length_utf16:uint` |
| Diagnostic / `DiagnosticFingerprint` | `frame:text` (`retained-csharp-diagnostic-v1`), `document_fingerprint:text`, `ordinal:uint`, `diagnostic_id:text`, `severity_code:uint` (`0=hidden`, `1=info`, `2=warning`, `3=error`), `span_start_utf16:uint`, `span_length_utf16:uint`, `representation:text` (`scanned` or `withheld`), `scanned_message:nullable<text>`, `withheld_reason:nullable<text>` |
| Completion / `CompletionFingerprint` | `frame:text` (`retained-csharp-completion-v1`), `document_fingerprint:text`, `parser_fingerprint:text`, `symbol_fingerprints:list<hex64>`, `reference_fingerprints:list<hex64>`, `diagnostic_fingerprints:list<hex64>`, `withheld_symbol_count:uint`, `withheld_reference_count:uint`, `withheld_diagnostic_count:uint`, `receipt_diagnostic_codes:list<text>` |
| Blocked diagnostic / `BlockedDiagnosticFingerprint` | `frame:text` (`retained-csharp-blocked-diagnostic-v1`), `source_revision_id:text` (lower-case GUID `N`), `retained_artifact_sha256:text`, `descriptor_fingerprint:text`, `parser_fingerprint:text`, `ordinal:uint`, `diagnostic_id:text`, `severity_code:uint`, `span_start_utf16:uint`, `span_length_utf16:uint`, `representation:text`, `scanned_message:nullable<text>`, `withheld_reason:nullable<text>` |
| Blocked completion / `BlockedCompletionFingerprint` | `frame:text` (`retained-csharp-blocked-completion-v1`), `source_revision_id:text`, `retained_artifact_sha256:text`, `descriptor_fingerprint:text`, `parser_fingerprint:text`, `outcome_code:text` (`csharp-code-syntax-invalid`), `blocked_diagnostic_fingerprints:list<hex64>`, `withheld_symbol_count:uint` (`0`), `withheld_reference_count:uint` (`0`), `withheld_diagnostic_count:uint`, `receipt_diagnostic_codes:list<text>` |

The success record schemas apply only to persisted success facts. A withheld
symbol or reference has no fact row and therefore no symbol/reference fingerprint; it
increments the corresponding completion/document/receipt count. A diagnostic
always has a deterministic ordinal and fingerprint: `scanned_message` is null
for `withheld`, `withheld_reason` is null for `scanned`, and the non-null value
is respectively a secret-filtered message of at most 1,024 UTF-16 code units or
the fixed `secret-content-withheld`. `receipt_diagnostic_codes` contains the
ordered retained `diagnostic_id` values, including withheld diagnostics, in
diagnostic ordinal order and is bounded by 256. These rules supply independent
golden-vector inputs for parser, document, symbol, reference, diagnostic and
completion fingerprints. A syntax-invalid diagnostic cannot use a document
fingerprint: it uses the separate blocked-diagnostic record, and the blocked
completion record is the receipt's `CompletionFingerprint` for that outcome.
Paths, source originals, random IDs, clock values and transient lease timestamps
are excluded.

Roslyn error diagnostics are deterministic parser failures. A syntax error is
`csharp-code-syntax-invalid` blocked and creates no code document, symbol,
reference, document diagnostic or success completion receipt. It instead uses
the branch/attempt-owned blocked-diagnostic representation defined below, so the
bounded, secret-filtered local summary survives replay/recovery without claiming
that a `DocumentId` FK can exist. Cancellation, supersession, expired leases and
stale fences use the existing lifecycle outcomes and commit no partial facts.
Unexpected IO/SQL transient failures are retryable; retained-binding/integrity
failures retain the existing fixed retained-artifact outcomes.

The closed Operator Action hard-denial set gains exactly
`csharp-code-input-too-large`, `csharp-code-text-limit`,
`csharp-code-input-not-utf8`, `csharp-code-node-limit`,
`csharp-code-depth-limit`, `csharp-code-symbol-limit`,
`csharp-code-reference-limit`, `csharp-code-identifier-limit`,
`csharp-code-signature-limit`, `csharp-code-diagnostic-limit` and
`csharp-code-syntax-invalid`. Existing retained-integrity,
`processor-parser-unavailable`, provenance and fence codes remain hard denials.
Task 5 creates no C# override or retry capability and does not widen Operator
Actions beyond its existing OOXML action surface.

## Durable output and atomicity

An additive SQL migration introduces:

1. `SourceActivities.DescriptorFingerprint`, a non-null 64-hex field. The
   migration assigns existing activities the immutable legacy sentinel
   `b0fe7acd8ced58bf9215c12938f5bbc75b722323f3553f2705959467029a4fb5`
   (SHA-256 of `legacy-source-activity-descriptor-v1`), drops the current unique
   `(SourceRevisionId, ActivityKind, ProcessorVersion, InputFingerprint)` index
   and creates the unique `(SourceRevisionId, ActivityKind, ProcessorVersion,
   DescriptorFingerprint, InputFingerprint)` index. The domain draft and
   registration/store APIs carry the descriptor fingerprint and generate the
   canonical idempotency key with its length-prefixed value; the old unique key
   is never reused for C# activities.
2. `SourceProcessorCodeDocuments`, one immutable completion row per processor
   branch, keyed by `SourceProcessorBranchId`.  It stores source revision id,
   retained-artifact SHA-256, descriptor fingerprint, parser fingerprint,
   handler id, lease generation, decoded character/line counts, symbol/reference
   counts, diagnostics count, `WithheldSymbolCount`, `WithheldReferenceCount`,
   `WithheldDiagnosticCount`, `ReceiptDiagnosticCodeCount` and deterministic
   completion fingerprint. All count columns are non-negative; diagnostic and
   receipt-code counts are constrained to `0..256`. The three withheld counts
   are the exact count values in the completion fingerprint, never inferred
   from missing child rows.
3. `SourceProcessorCodeSymbols`, keyed by document plus stable ordinal, with a
   unique `(DocumentId, SymbolFingerprint)` alternate key.  It stores local
   kind/name/qualified-name/signature/modifiers, parent ordinal and source span.
4. `SourceProcessorCodeReferences`, keyed by document plus stable ordinal, with
   a unique `(DocumentId, ReferenceFingerprint)` alternate key.  It stores local
   relationship kind, source-symbol ordinal when present, target display text
   and source span.
5. `SourceProcessorCodeDiagnostics`, a bounded immutable child table keyed by
   `(DocumentId, Ordinal)` where `Ordinal` is zero-based Roslyn diagnostic
   source order (span start, span length, diagnostic ID as deterministic ties).
   It has a restrictive `DocumentId` FK, `DiagnosticId` `nvarchar(64)`,
   `Severity` `tinyint` (`0=Hidden`, `1=Info`, `2=Warning`, `3=Error`),
   non-negative `SpanStartUtf16` and `SpanLengthUtf16`,
   `Representation` (`scanned` or `withheld`), nullable
   `ScannedMessage nvarchar(1024)`, and nullable
   `WithheldReason nvarchar(64)`.  Checks require severity to be a defined
   Roslyn severity numeric value `0..3`, the spans to be non-negative,
   `ScannedMessage` to contain at most 1,024 UTF-16 code units, and exactly one
   of a scanned message or withheld reason according to the representation.
   `withheld` stores only `secret-content-withheld`; no unscanned text, source
   fragment or raw diagnostic detail is retained.  The primary/composite unique
   key is `(DocumentId, Ordinal)`, the alternate key is
   `(DocumentId, DiagnosticFingerprint)`, and the migration creates
   `IX_SourceProcessorCodeDiagnostics_DocumentId_Severity_Ordinal` for ordered
   local-detail reads.  `SourceProcessorCodeDocuments.DiagnosticsCount` is the
   total of both representations and is constrained to `0..256`.
6. `SourceProcessorCodeCompletionReceipts`, one immutable receipt per branch,
   keyed by `SourceProcessorBranchId`, is the receipt-first replay authority.
   A successful receipt has a restrictive non-null `DocumentId` FK and persists
   every document immutable identity/fingerprint plus `WithheldSymbolCount`,
   `WithheldReferenceCount`, `WithheldDiagnosticCount`,
   `ReceiptDiagnosticCodeCount` and the exact ordered diagnostic-code list.
   The list is stored as its canonical wire value (`ReceiptDiagnosticCodesWire`)
   and its count is independently checked against the wire count and the
   document count. A blocked syntax-invalid receipt has `DocumentId NULL`,
   persists the same zero symbol/reference and withheld-symbol/reference counts,
   `BlockedDiagnosticsCount`, its withheld-diagnostic count, the
   `CompletionFingerprint` whose value is the framed
   `BlockedCompletionFingerprint`, and the ordered blocked diagnostic-code wire
   list. The schema check permits a null
   document only for `OutcomeCode='csharp-code-syntax-invalid'`; a successful
   receipt must have a document. This is deliberately not a document FK model
   for a syntax-invalid result.
7. `SourceProcessorCodeBlockedDiagnostics` is the additive, bounded local
   diagnostic table for syntax-invalid completion. Its key is
   `(SourceProcessorBranchId, SourceProcessorAttemptId, Ordinal)`, where
   `Ordinal` is zero-based parser diagnostic order (span start, span length,
   diagnostic ID). It has a restrictive `SourceProcessorBranchId` FK and a
   restrictive composite `(SourceProcessorBranchId, SourceProcessorAttemptId)`
   FK to the attempt's unique branch ownership key; it has **no `DocumentId`
   column or document FK**. Each row persists `DiagnosticId nvarchar(64)`,
   defined `Severity tinyint` (`0..3`), non-negative UTF-16 span start/length,
   `Representation`, nullable `ScannedMessage nvarchar(1024)`, nullable
   `WithheldReason nvarchar(64)` and `BlockedDiagnosticFingerprint`. It uses the same
   exactly-one scanned-message/withheld-reason, 1,024 UTF-16-unit and fixed
   `secret-content-withheld` checks as document diagnostics, a unique
   `(SourceProcessorBranchId, SourceProcessorAttemptId, BlockedDiagnosticFingerprint)`
   alternate key, and an ordered local-detail index ending in `Ordinal`. A
   check bounds the ordinal/count to `0..255`; `BlockedDiagnosticsCount`,
   receipt counts and code-wire
   count must equal its rows. No syntax-invalid branch may have a code document,
   symbol, reference or document-diagnostic row.

All FKs to branch/document/attempt are restrictive. Completion uses one serialisable
transaction and its first operation is an immutable receipt lookup by branch.
Before it validates the caller's current owner/generation/lease, it returns the
original receipt only when branch ID, source revision, activity kind
`CodeParsing` (numeric `5`), processor version, descriptor fingerprint, parser
fingerprint, retained SHA-256, handler ID, outcome and the outcome's completion
fingerprint are exact matches. A success additionally requires the document
fingerprint and every ordered symbol/reference/document-diagnostic fingerprint;
a syntax-invalid outcome instead requires every ordered blocked-diagnostic
fingerprint. Both require all three withheld counts and the exact ordered receipt
diagnostic-code wire value. This makes a delivery replay safe after its
original lease has expired. If any immutable receipt already exists for that
branch but any listed value differs, it returns the fixed
`csharp-code-completion-conflict` result and writes nothing; it never reports a
lease error, replaces facts or creates a second receipt.

Only when no receipt exists does the transaction validate the database-current
claim: same branch ID, source revision, `CodeParsing`, processor version,
descriptor fingerprint, parser fingerprint, retained SHA-256, worker owner,
lease generation and an unexpired lease. The document/facts/diagnostics,
attempt/branch receipt, bounded local completion evidence and SourceActivity
completion are inserted only through a single conditional active insert/update.
For syntax-invalid, the same transaction inserts only the blocked receipt and
the branch/attempt-owned blocked diagnostic rows, then records the terminal
blocked activity; it does not insert a document or any success child. On a
restart or unique race it replays that original blocked receipt only when the
same ordered diagnostic fingerprints, three counts and diagnostic-code wire
value match. Its conditional predicate contains exactly the same fence values;
there is no recovery path that creates a document for syntax-invalid input. The
success or blocked path is inserted only through a conditional active write
whose predicate contains exactly those values. A zero-row conditional write is
the existing stale/superseded/expired result and writes nothing. A unique race
re-reads the receipt under the same serialisable transaction and applies the
exact-replay-or-conflict rule above. Thus a stale or superseded claim writes no
partial document, fact, diagnostic, receipt or evidence.

## Local output and secret handling

The companion [private-PC policy](2026-08-16-private-pc-local-visibility-policy-design.md)
authorises raw local code facts.  The C# local read model may return exact local
path, artifact hash, code symbols, signatures, relationships, source spans and
bounded parser diagnostics through the local Sources/detail, search, REST, CLI,
MCP, audit and diagnostics surfaces.  A requested raw code excerpt is read only
from the verified retained artifact and must pass the local secret filter.  It
returns `secret-content-withheld` for an identified secret rather than returning
the value. Before persistence, each symbol/signature, reference display text and
diagnostic follows the companion policy's per-fact scan/withhold/block table:
a detected secret withholds only that fact and records bounded withholding
evidence, while a scan failure blocks the entire atomic completion. The browser
never receives a source-original locator.

External/public/export/shared DTOs exclude those fields and never contain raw
code, a local path, artifact locator, parser diagnostic or secret.  Tests use
synthetic source and secret sentinels; no test fixture or document contains a
real private value.

## Required evidence

Before the slice is approved, fresh tests must prove: C# classification and
unsupported-language non-promotion; startup preflight success/mismatch/absence;
UTF-8/BOM and every limit/outcome; retained-only success after the source
original disappears; integrity/binding failure; deterministic facts and
completion fingerprint; cancellation, lease loss, supersession, duplicate,
concurrent claim and restart recovery; exact migration/designer/snapshot and
generated-database upgrade; hard-denial coverage; local UI/REST/CLI/MCP/search
detail plus external/export exclusion and secret sentinel withholding; and
synthetic browser validation where the detail UI changes.

The evidence includes descriptor/processor/parser/document/symbol/reference/
diagnostic/completion plus blocked-diagnostic/blocked-completion fingerprint
golden vectors, activity-identity migration from the
legacy sentinel, and deterministic precedence tests for every simultaneous-limit
pair that can arise on one traversal node/fact.

Generated/disposable SQL and synthetic browser infrastructure are standing
authorised and must be configured/run for this work.  This design does not
authorise a non-disposable database, deployment, push, merge, Outlook/profile
activation, source-original reread, cloud parser, network call, model download
or live validation.
