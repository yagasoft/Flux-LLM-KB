# Repository retrieval reliability implementation plan

Date: 3 October 2026. Updated 4 October 2026: release `a248811a` completed mandatory
integration and authorised incremental deployment, including the additive migration
and automatic proof backfill. Live acceptance exposed scoped SQL latency, whole-file
disclosure refusal and earlier terminal Embed checkpoints. The focused corrections
and recovery action were integrated and deployed as `4f9393ef`. Current exact
method and documentation reads pass through MCP/REST/CLI. The subsequent serial
page-plan correction and complete semantic live acceptance remain pending. Read the
[design](repository-retrieval-reliability.md) before execution.
Use one implementation owner. Apply focused execution guidance proportionately;
independent review is required for the security/migration/concurrency gates and
the final branch, not for every fixture or mechanical caller conversion.

## Goal, architecture and constraints

Deliver exact cited C# method reads without weakening credential protection, and
complete scoped semantic candidate selection beyond the former 10,000-vector
cutoff. Add an optional canonical-text syntax proof; read SQL vectors in bounded
pages under the existing generation lease. Preserve SQL Server, native .NET,
USearch, existing Roslyn and the shared MCP/REST/CLI engine.

- Baseline main is `4f9393ef`. Continue in the existing dedicated task worktree
  on `codex/retrieval-live-recovery`; inspect its state first.
- Preserve the single Git-discovered source, automatic additions/edits/renames/
  deletions, exclusions, C# facts, all currently supported formats and citations.
- Preserve all design safety/parser/transport budgets and deployed 20-second GPU/
  25-second outer deadlines. A 256-vector page is batching, never a total cap.
- No model acquisition, ranking/profile change, ANN rebuild, source-policy edit,
  Publish replay, production mutation or worktree/stash deletion during local
  implementation. Ordinary tests use synthetic providers and disposable SQL.
- Keep existing dirty corpus-retrieval work and ignored incident evidence intact.
  AGENTS and dashboard manuals are outside the edit set.

## Review focus

| Risk | Owning verification |
| --- | --- |
| JSON or credential fragments mistaken for code, including inside comments/raw strings | Task 1 adversarial disclosure and transport tests |
| Stale/partial/forged proof, CRLF offsets and backfill races | Task 1 canonical identity and atomic projection tests |
| An out-of-scope or corrupt late vector entering results | Task 2 scope, integrity and exact-oracle tests |
| Mixed generations, abandoned SQL/native leases or cancellation after caller timeout | Task 2 publication barriers and ownership tests |
| Freshness/performance claims based on old receipts or lexical fallback | Task 3 current acceptance with explicit retrieval mode/stamp |

## Task 1: Safe cited C# context through canonical syntax proof

Deliverable: a disposable source's supported late-file method is searchable and
readable with exact citation binding; adversarial content remains withheld.

**Files and interfaces**

- Add `src/FluxKnowledge.Application/Visibility/CsharpDisclosureProofBuilder.cs`
  and proof/window value types beside the existing disclosure interfaces.
  The pure builder consumes canonical text/hash and produces a versioned ready,
  unsupported or invalid result plus ordered structural opening-brace offsets
  and merged protected/ambiguous literal/comment ranges.
- Add `src/FluxKnowledge.Application/Ports/ICodeDisclosureProofStore.cs` for bounded
  eligible-artifact discovery and idempotent, identity-fenced proof persistence.
- Extend `Indexing/CanonicalIndexStageWorker.cs` and `Pipeline/StageTransitionRequest.cs`
  with optional application-owned proof in `IndexingStageOutput`. Canonical text,
  artifact hash, chunk bodies and search-input hashes stay unchanged.
- Add `Persistence/Entities/CanonicalCodeDisclosureEntities.cs`,
  `Persistence/Configurations/CanonicalCodeDisclosureConfigurations.cs` and one
  additive EF migration in the SQL Server project; extend
  `Persistence/SqlStageTransitionStore.cs` to commit new proof with its artifact.
  Add `Persistence/SqlCodeDisclosureProofStore.cs` and
  `Workers/CodeDisclosureProofRecoveryService.cs` in that project, using existing
  hosting/retry conventions. Register in `ServiceCollectionExtensions.cs` and
  `FluxKnowledge.Web/WebHostComposition.cs` as appropriate to current lifetimes.
  Keep parser work outside SQL transactions and respect deployment holds.
- Extend `Ports/ICorpusRetrievalReader.cs` candidate/context internal values with
  optional window proof. Update SQL reader/search partials to seek only intersecting
  proof spans. Never append a complete span array to citation metadata.
- Extend `Visibility/ILocalPrivateContentDisclosure.cs` with named `EvaluateCode`
  and `EvaluateDecodedText` methods whose default/no-proof behaviour is the
  existing conservative evaluation. Keep the original `Evaluate` signature for
  reflection-based callers.
  Implement it in `LocalPrivateContentDisclosure.cs`; apply it in
  `CorpusRetrievalService.cs` and `HybridPassageRetrievalEngine.cs` for body,
  header-plus-body and complete-line context checks. Wire contracts stay unchanged.
- Add `tests/FluxKnowledge.Domain.Tests/Visibility/CsharpDisclosureProofBuilderTests.cs`
  and `tests/FluxKnowledge.Integration.Tests/Visibility/CodeDisclosureProofIntegrationTests.cs`.
  Extend the existing
  `LocalPrivateContentDisclosureTests.cs`, `ScopedCorpusRetrievalTests.cs`,
  `NativeV1EnvelopeProtectorTests.cs` and `CorpusRetrievalEndToEndTests.cs`.

**Steps**

- [x] Freeze the current code-read reproduction in red tests: safe whole method,
  brace-leading body and the larger complete-line guard. Include a method beyond
  16 Ki units in a complete canonical file. Assert actual returned text and offsets,
  not merely HTTP success or a method name found at a call site.
- [x] Add adversarial tests before changing disclosure. Cover pure JSON/top-level
  blocks; malformed/recovered C#; initialisers and collection/indexer delimiters;
  raw/verbatim/interpolated strings; comments and disabled preprocessor text;
  credential fragments without an opening brace; raw/escaped/Unicode property
  names; ambiguous/split keys; encoded content; long credential lines; all existing
  candidate/depth/count/scan bounds; and ordinary harmless code literals.
- [x] Test decoded credentials and ambiguous encoded constructs whose enclosing
  method brace or literal beginning is outside the fetched window. Persisted
  protected ranges must withhold intersecting search/read guards; clean distant
  passages in the same artifact must retain their previous eligibility. Unsupported
  expressions are never executed or presumed safe. Test per-row integrity
  checksums and missing/partial protected ranges together with structural spans.
  Move a structural offset onto an actual brace inside a string and verify refusal.
  Read header/count/window coherently; a server-side full span count must match the
  immutable ready header without hydrating unrelated rows.
- [x] Implement the builder using complete canonical C# 14 text and the existing
  parser limits. Mark only the whitelist in the design. Include classifier/parser
  identity, canonical SHA-256/length, deterministic span count/checksum and
  checked UTF-16 offsets. Unsupported/error/resource-limited input grants no proof.
- [x] Add the derived tables and transactional writer. Test rollback after partial
  insert, cancellation, duplicate/concurrent same-policy writes, changed policy,
  source replacement/deletion during parsing, mismatched canonical hash, tampered
  offsets/count/version and artifact cleanup. An incomplete proof must never be
  readable as ready. Include CRLF normalisation and supplementary Unicode.
- [x] Add model-free backfill using the same builder/writer, one bounded artifact
  at a time. Test restart/idempotency, terminal unsupported/invalid states,
  transient SQL failure/backoff, host shutdown and concurrent normal publication.
  Include an active deployment hold and a hold/admission race; no writes may pass
  the existing operational mutation gate.
  Prove unchanged artifact/text/chunk/search-input/vector hashes and no pipeline
  job replay, model call or ANN rebuild.
- [x] Implement bounded proof reads and disclosure use. Preserve raw whole-text
  credential checks and add the full-window quoted-property precheck before
  excluding proved structural starts. Keep the existing guard, JSON budgets and
  final envelope check. No trusted proof may originate from a request or metadata.
- [x] Run the first end-to-end fixture through normal source discovery, durable
  publication, search and cited read; verify the full supported method beyond
  16 Ki units. Repeat with an embedded synthetic credential and expect refusal.
  Use the existing Web/native adapter test infrastructure for response parity.
- [x] Review this complete security/migration capability independently. Correct
  blocking findings before exposing the proof-aware path. Missing/old proof stays
  conservative; it must not create an availability or startup failure.

Focused commands, after building the changed projects:

    dotnet test tests/FluxKnowledge.Integration.Tests/FluxKnowledge.Integration.Tests.csproj -c Release --filter "FullyQualifiedName~CodeDisclosure|FullyQualifiedName~LocalPrivateContentDisclosureTests|FullyQualifiedName~ScopedCorpusRetrievalTests|FullyQualifiedName~NativeV1EnvelopeProtectorTests"
    dotnet test tests/FluxKnowledge.Web.Tests/FluxKnowledge.Web.Tests.csproj -c Release --filter "FullyQualifiedName~CorpusRetrievalEndToEndTests"

Add the builder's domain tests to the focused run. All applicable assertions must
pass with zero new warnings; an unavailable SQL fixture is an unverified gate.
Use `NativeSqlServerFixture` and its approved disposable connection resolution,
never the installed application's production connection for tests.

## Task 2: Complete scoped dense retrieval with bounded memory

Deliverable: a scope above 10,000 published vectors returns its exact top candidates
and citations under a current lease without loading the global ANN generation.

**Files and interfaces**

- `src/FluxKnowledge.Application/Ports/IHybridPassageRetrieval.cs`:
  `ReadDenseCandidatesAsync(ICorpusGenerationLease lease, ResolvedCorpusScope scope,
  IReadOnlyList<float> query, CancellationToken cancellationToken)`.
  `ICorpusAnnLease` already inherits that base; no new lease abstraction is needed.
- `src/FluxKnowledge.Application/Search/HybridPassageRetrievalEngine.cs`:
  select one callback-owned SQL or native lease by scope.
- `src/FluxKnowledge.Infrastructure.SqlServer/Search/SqlCorpusRetrievalReader.Dense.cs`:
  scoped keyset pages, bounded top-100 accumulation, final hydration/fences.
- `HybridPassageRetrievalTests.cs` and
  `HybridPassageRetrievalIntegrationTests.cs`; add focused helpers only where
  existing SQL interceptors/barriers and fixtures are insufficient.

**Steps**

- [x] Replace the 10,001-vector refusal expectation with red exact-result tests at
  10,000, 10,001 and multiple full/partial pages. Put the winning vector after the
  former cutoff; compare IDs/order against an independent brute-force oracle.
- [x] Add ties across pages, empty scope, exactly-full final page, sparse IDs,
  nested workspace and sibling-prefix exclusions, stronger out-of-scope candidates,
  multi-root containment and a corrupt low-score vector on a later page.
- [x] Change the port and mechanical test callers together. Root/workspace uses
  the SQL lease directly; all scope still requires native ANN. Preserve acquisition
  inside the scheduled callback and OpenAsync's ownership transfer on failure.
  Assert no ANN open/global-vector read for scoped requests, and exactly-once
  release on success, embedding/open failure, cancellation and delayed completion.
- [x] Replace the total-count refusal with 256-row keyset pages. Reuse every
  existing SQL publication/scope/hash/profile/stamp predicate and validate all
  vector rows. Keep only distance/vector/chunk IDs in the 100-item accumulator.
  Retain double-precision distance and the existing vector-ID tie break.
- [x] Add deterministic barriers for a version change, generation retirement,
  source suppression/deletion and lost lease between pages and before hydration.
  Check cancellation within the scoring loop and during later-page I/O. Assert
  no partial semantic-ready result and no cleanup while a callback still uses
  its lease. Preserve existing all-scope native disposal ordering.
- [x] Prove per-page payload bounds with a SQL command observer and accumulator
  bounds with observable diagnostics/test seams; also measure allocations/peak
  live memory at growing scopes. Do not mistake cumulative allocation for retained
  memory or count EF buffering as streaming.
- [x] Run disposable API search/read above 10,000 vectors with synthetic
  embedding/reranking providers. Confirm scope, semantic status, exact citation,
  source deletion invalidation and current-generation binding.
- [x] Reassess after Tasks 1–2. If exact paging cannot fit the accepted workload
  within existing deadlines, present measured SQL/scoring/inference costs and
  revise only that bottleneck. Do not add another total cap or widen deadlines.

Focused commands:

    dotnet test tests/FluxKnowledge.Domain.Tests/FluxKnowledge.Domain.Tests.csproj -c Release --filter "FullyQualifiedName~HybridPassageRetrievalTests"
    dotnet test tests/FluxKnowledge.Integration.Tests/FluxKnowledge.Integration.Tests.csproj -c Release --filter "FullyQualifiedName~HybridPassageRetrievalIntegrationTests|FullyQualifiedName~SqlToUsearchRebuildTests"

Reuse the existing publication-selection performance test style. Required races
must actually execute; a skipped integration test does not satisfy its invariant.

## Task 3: Acceptance, integration and concrete release preparation

Deliverable: reviewed implementation with truthful current acceptance and a
recoverable incremental release, executed only when authorised.

Local evidence and the supported migration/recovery path are recorded in
[the 4 October validation note](../operations/2026-10-04-repository-retrieval-local-validation.md).
The 4 October read-only baseline has 1,047 tracked main paths, installed payload
`1b5bd5f6`, healthy live/ready/index probes and repository root configuration
revision 11. It does not prove today's complete published inventory or proof
coverage. The incremental updater now has an exclusive
`-ApplyCodeDisclosureProofMigration` option for
`20261003220411_AddCanonicalCodeDisclosureProof`, with pinned SQL and complete
schema verification. Fault tests retain the hold for uncertain schema or failed
prior-payload probes; compatible rollback retains additive tables. Its read-only
PlanOnly verified the prior production baseline before authorised release
`a248811a`; its Apply and automatic backfill completed. The correction release
still requires fresh exact-payload operational review under deployment authority.

- [ ] Reconcile the existing 1 October publication/recovery/freshness receipts.
  Preserve completed evidence and remaining restrictions. Record fresh main,
  installed payload, health, Git/source inventory, publication/index stamps and
  proof status when preparing current acceptance. Do not assume 1,044 stays exact.
- [ ] Run affected combined tests and the native repository contract. Update the
  affected architecture, roadmap and coverage acceptance entries only; no manual
  regeneration or AGENTS rewrite. Record the additive proof schema/backfill and
  supersession of the historical 10,000-vector design rule.
- [x] Obtain independent review of the full final diff, proof authority,
  migration/backfill idempotency, scoped lease ownership and test evidence.
  Resolve blocking findings without unrelated refactors.
- [x] Use `scripts/dev/complete-feature.ps1` with `-KeepWorktree` for the initial authorised
  integration/closeout. Do not use `-GoLive`. If it fails, report its `failed_step`
  and `log_path`, repair within scope and rerun affected checks.
- [x] Prepare `scripts/deploy/update-native-iis-incremental.ps1 -PlanOnly` for the initial release with
  the exact supported parameters. Include additive migration and automatic
  derived-proof backfill explicitly in the release target/rollback review.
  If the updater cannot cover the migration, explain that gap before any alternate
  operational action. Obtain user approval and immediate independent operational
  review before Apply; historical recovery approval is not this new release.
- [ ] After an authorised release, prove unchanged canonical/vector identities,
  completed proof for the accepted late-file methods, current source membership,
  automatic watcher/rescan convergence and current docs after integration.
  Verify the method's declaration/body, not an invocation, through REST/CLI/MCP.
- [ ] Freeze 20 representative code/docs queries before measurement; run them
  twice across two concurrent callers for 40 observations, covering root and
  nested scopes including the full repository above the former cap. On a
  quiescent current generation require semantic-ready results, exact citations,
  correct containment and p95 below 20 seconds. Record fallback/update/timeout
  events and current index stamps separately. Preserve the outer 25-second
  deadline and existing scheduler/OCR ownership checks.
- [ ] Exercise stale citations after a normal changed/deleted source in a disposable
  environment; use real naturally changed tracked docs for live freshness where
  available. Do not introduce production test files without explicit authority.
- [ ] Recheck health and worker continuity over the same acceptance workload.
  Record any new shutdown/debugger/crash or SQL timeout with its consequence;
  do not process a failed/dead-letter job as part of validation.
- [ ] Roll back only through the reviewed compatible payload recovery if needed.
  Leave additive proof data/schema intact; previous binaries ignore them.
  No source downgrade, destructive SQL reversal, cache deletion or model changes.
- [ ] Mark the coverage gate complete only after its stated supported retrieval
  cases pass. Preserve evidence/worktrees until all authorised closeout steps
  succeed. Keep genuine withheld content and operational/resource limits explicit.

## Live acceptance correction and checkpoint recovery

- [x] Identify the measured SQL bottleneck without changing deadlines, profiles or
  memory bounds. Apply scoped-only serial ordered membership seek/loop hints; verify
  complete identical IDs for full, nested, sparse and empty scopes and retain
  actual plans, elapsed time and logical reads. Repeat existing oracle, corruption,
  containment, cancellation and publication/lease-race tests after the query change.
  Under disposable optimiser stress, assert bounded per-page chunk access rather
  than a timing threshold; retain current parallel and serial actual-plan evidence.
- [x] Reproduce the actual complete source's declaration/body and cited method read.
  Keep the original guard and complete proof validation. Apply Protected refusal
  to actual output; distinguish a bounded closed JSON candidate from unrelated
  trailing guard text only for validated code. Verify raw/escaped/encoded/composite
  credentials, invalid intervals/proofs and unfinished/oversized candidates.
- [x] Add `Persistence/SqlEmbeddingRetry.cs` and wire `embedding_retry` into existing
  native corpus preview/commit, confirmation, audit, idempotency and outbox wake.
  Reuse checkpoint validators without fabricated worker/GPU ownership. Validate
  current lifecycle, canonical inputs, draft/profile/epoch, vectors and exact
  settled GPU input/result/cleanup/dispatch/slot bindings. Preserve draft and job/
  delivery identity while advancing both lease generations atomically.
- [x] Prove zero/partial/complete checkpoint continuation through normal publication
  and cited readback with synthetic providers/disposable SQL. Verify changed inputs,
  GPU contradictions, stale confirmations, concurrent commits, deployment hold,
  cancellation/rollback, lost responses and old delivery callbacks. Cover REST,
  MCP and CLI through their existing native routes.
- [ ] Obtain independent review of the complete correction, affected invariants and
  fresh combined evidence; integrate using `complete-feature.ps1 -KeepWorktree`.
- [ ] Prepare/review and apply the exact incremental correction release under
  current deployment authority. Keep the existing additive schema and compatible
  prior payload for rollback; no further migration, profile or source-policy change.
- [ ] Reconcile fresh Git/source publication and freeze only still-current eligible
  terminal Embed jobs, binding source bytes, versions and settled checkpoints.
  Obtain independent recovery review and explicit human approval for this exact
  cohort before confirmed commit processing. Refuse drift rather than widening it.
- [ ] Complete the already frozen 20-query, two-round/two-caller live workload,
  method/docs readback, automatic freshness and health/worker continuity gates.
  Report terminal recovery and retrieval acceptance separately from deployment.

## Side notes retained for later

- The dirty `codex/corpus-retrieval-implementation` worktree contains an XLSX
  self-closing-cell correction/test and older retrieval documents. Compare the
  correction against current helpers with a meaningful regression before porting;
  reconcile superseded documents separately. No blanket merge or discard.
- OCR quality/retry work, Outlook desktop acceptance and broader source lifecycle
  acceptance remain deferred. They are not prerequisites for this increment.

## Handoff

The user approved implementation, deployment and live validation. Initial release
`4f9393ef` is installed with the compatible additive schema, full-source disclosure
correction and explicit Embed recovery action. Current exact cited code/docs reads
pass; scoped semantic acceptance still times out under the parallel page plan.
The same managed task worktree holds its focused serial correction. Source configuration
and model cache remain unchanged. Terminal processing requires separate explicit
approval of a fresh exact cohort. Preserve the worktree, prior branch/stash and
ignored evidence until all required release/acceptance steps succeed.
