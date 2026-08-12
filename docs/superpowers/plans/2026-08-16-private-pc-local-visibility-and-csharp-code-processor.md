# Private-PC local visibility and retained C# code processor implementation plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use `superpowers:subagent-driven-development` task-by-task. Each task requires a fresh implementer and a read-only independent reviewer before the next task begins.

**Goal:** Widen every trusted local application contract to expose useful retained-derived detail without exposing secrets or externalising private data, and deliver deterministic retained C# syntax facts with Roslyn.

**Architecture:** Local output is an explicit read-model class, not a weakened shared/export projection. Python Flux surfaces and the native .NET replacement receive local-only detail adapters with their own secret boundary. The C# processor uses only verified retained bytes, commits a fenced document/fact set atomically, and feeds those local read models.

**Tech stack:** Python/Flask/React legacy local app; .NET 10, C#, Roslyn 5.0.0, EF Core, SQL Server, Blazor/ASP.NET Core; xUnit, pytest and synthetic-browser tests.

## Global constraints

- Apply [the private-PC local visibility policy](../specs/2026-08-16-private-pc-local-visibility-policy-design.md) and its all-app surface ledger to local UI, direct-loopback REST/SignalR, CLI, legacy local-process MCP, native HTTP `/mcp`/SSE, diagnostics, audit, search and code output; preserve a distinct external/public/export DTO.
- Never emit or persist passwords, tokens, OAuth/client secrets, cookies, connection strings, private keys, credential headers or detected secret literals. Scan every persisted symbol, signature, reference text and diagnostic before write; withhold a detected fact, block an unscannable completion and use synthetic secret sentinels only.
- Local raw content is read through a verified retained/private store only. Do not reopen a source original, activate Outlook, enable an Outlook profile, use Office automation, call a cloud/network parser, download/activate a model, deploy, merge, push, apply a production migration or run live validation.
- Use generated/disposable SQL and synthetic browser infrastructure. Provision the safe fixture when absent; do not use a non-disposable or externally shared database and do not call a skipped matrix GREEN. A missing local SQL prerequisite is a failing infrastructure result, never a skip or fallback to another database.
- Browser provision is equally mandatory: a disposable-browser helper must discover and canonicalise only a pre-existing local Playwright/Chromium executable, validate its file identity and local `--version`, and pass that executable to the synthetic launch. It may not install/download Chromium, run `playwright install`, restore packages from a network source or use a fallback browser. Set `FLUXKNOWLEDGE_BROWSER_TESTS=1` only in the child environment of a synthetic disposable browser command; missing/non-launchable Chromium is a failed infrastructure result, never a skip.
- Keep normal mutation authorities unchanged: direct-loopback, forwarded/proxy rejection, same-origin/antiforgery and durable lease/fence/idempotency behaviour remain mandatory.
- C# parser package is `Microsoft.CodeAnalysis.CSharp` 5.0.0, licence MIT; no workspace/analyser/source-generator/project loading is permitted. The descriptor is `retained-csharp-code` / `08dd66fb-6502-4b31-a4a5-51e8cc66f916` / `retained-csharp-roslyn-syntax-v1`.

## Current file map

| Area | Files and responsibility |
| --- | --- |
| Python local contracts | `src/flux_llm_kb/service.py`, `rest_api.py`, `mcp_server.py`, `cli.py`, `code_diagnostics.py`, `result_details.py`, `database.py` |
| Python UI | `src/App.tsx` and the existing dashboard/retrieval models/tests |
| Native retained processing | `src/FluxKnowledge.Application/Sources/SourceClassifier.cs`, `SourceScanWorker.cs`, C#-aware planner/replanner and `RetainedProcessorActivationService.cs`, `OoxmlStructuralTextProcessor.cs`, `Ports/IRetainedProcessorBranchStore.cs` |
| Native persistence | `src/FluxKnowledge.Infrastructure.SqlServer/Persistence/SqlRetainedProcessorBranchStore.cs`, `Entities` (code document/symbol/reference/document diagnostics, completion receipt and branch/attempt-owned blocked diagnostics), `Configurations/CanonicalSchemaConfigurations.cs`, migration/designer/snapshot, `Migrations`, `Workers/OutboxWorkerRegistration.cs` |
| Native local views | `src/FluxKnowledge.Infrastructure.SqlServer/Persistence/SqlCorpusProjectionReader.cs`, `src/FluxKnowledge.Web/Components`, `Program.cs` and direct-loopback gate/services |
| Native CLI | `src/FluxKnowledge.Cli` read-only local code/detail, diagnostics and retained-provenance projections; it has no listener or mutation expansion |
| Disposable validation | `scripts/dev/ensure-disposable-sql.ps1`, `scripts/dev/ensure-disposable-browser.ps1` and native browser-test fixture/launch options |
| Verification | `tests/FluxKnowledge.Domain.Tests/Sources`, `tests/FluxKnowledge.Integration.Tests/Sources`, `tests/FluxKnowledge.Web.Tests`, Python `tests/test_code_diagnostics.py`, `test_result_details.py`, `test_rest_api_crawl.py`, `test_cli.py`, `test_corpus_search.py` |

### Task 1: Local disclosure contract and safe disposable validation foundation

**Files:**
- Create: `src/FluxKnowledge.Application/Visibility/LocalDisclosureResult.cs`
- Create: `src/FluxKnowledge.Application/Visibility/ILocalPrivateContentDisclosure.cs`
- Create: `src/FluxKnowledge.Infrastructure.SqlServer/Visibility/LocalPrivateContentDisclosure.cs`
- Create: `scripts/dev/ensure-disposable-browser.ps1` and a browser-test launch/options helper that consumes only its validated executable path
- Modify: `src/FluxKnowledge.Web/Program.cs`, native HTTP `/mcp`/SSE authority middleware, `src/FluxKnowledge.Web/OutlookOperatorLoopbackGate.cs`, Python `src/flux_llm_kb/rest_api.py`, `mcp_server.py`, `cli.py`
- Modify: generated-disposable fixture/configuration under `tests/FluxKnowledge.Integration.Tests`
- Test: native Web/Integration authority tests and Python REST/CLI/MCP tests

**Interfaces:**

```csharp
public sealed record LocalDisclosureResult(string? Value, bool Withheld, string? ReasonCode);
public interface ILocalPrivateContentDisclosure
{
    LocalDisclosureResult Evaluate(string value, LocalDisclosureKind kind);
}
```

- [x] Write native and Python failing authority tests: direct loopback/local process can request a local detail field; native anonymous `/mcp` request and SSE reconnect are allowed only by direct-loopback authority; forwarded/proxy, direct remote, public/export and a synthetic `secret-content-sentinel` cannot. Assert no Windows/Negotiate/cookie authenticated-user requirement is introduced.
- [x] Run those tests RED. Expected: no shared local disclosure contract and inconsistent local transport guarding.
- [x] Implement the shared local result class, bounded secret detector, fixed `secret-content-withheld` response, direct-loopback/local-process authority hooks and native anonymous `/mcp`/SSE direct-loopback plus forwarded/proxy-denial middleware. Keep existing mutation gates unchanged.
- [x] Create `scripts/dev/ensure-disposable-sql.ps1` and make `NativeSqlServerFixture` invoke it before SQL tests. It must first validate a server-level connection has no catalogue/attach/user-instance fields and targets only `(localdb)\\MSSQLLocalDB`, `localhost`, `127.0.0.1`, `::1` or a hostname resolving exclusively to loopback; then create/start LocalDB when that is the selected target or validate the supplied local SQL Server. It exports only the dedicated test server connection, creates only `FluxKnowledge_Phase1Tests_<guid>` catalogues, verifies their files, and always disposes them. No configured application/production connection may be read, no non-local fallback is permitted, and missing LocalDB/local SQL Server fails the matrix explicitly rather than skipping it.
- [x] Create `scripts/dev/ensure-disposable-browser.ps1`. It accepts only an explicitly supplied local executable path or a path discovered from the locally restored Playwright browser cache, resolves the full path, verifies it is an existing executable under that approved local source, runs only its local `--version` probe, and returns the canonical executable path to the browser launch helper. The helper supplies it as Playwright `ExecutablePath`; it does not invoke any Playwright installer/download. The synthetic test wrapper sets `FLUXKNOWLEDGE_BROWSER_TESTS=1` only for its child process and fails before test discovery when this helper fails.
- [x] Run focused native/Python GREEN, generated SQL create/migrate/dispose probe, validated-local-Chromium synthetic browser authority test, Release build and `git diff --check`; commit `feat: add private local disclosure foundation`.
- [x] Obtain a fresh independent Task 1 review.

### Task 2: Widen legacy local Sources, Corpus, search and code contracts

**Files:**
- Modify: `src/flux_llm_kb/service.py`, `code_diagnostics.py`, `result_details.py`, `database.py`, `rest_api.py`, `mcp_server.py`, `cli.py`
- Modify: `src/App.tsx` and its retrieval/detail models
- Test: `tests/test_code_diagnostics.py`, `tests/test_result_details.py`, `tests/test_corpus_search.py`, `tests/test_rest_api_crawl.py`, `tests/test_cli.py`, dashboard tests

**Interfaces:** Create named local-only projections/adapters such as `LocalSourceDetailProjection`, `LocalCorpusDetailProjection`, `LocalCodeSearchProjection` and their REST/CLI/MCP/UI adapters after authority has been established. They are distinct types and methods, not a `local_detail=True` flag on a shared reader. A successful detail contains `source_path`, `content_hash`, `symbols`, `signatures`, `relationships`, `parser_diagnostics` and bounded `excerpt`; a withheld excerpt contains `excerpt: null` and `reason_code: "secret-content-withheld"`.

- [x] Add failing cross-surface tests that assert identical raw local code/path/signature/relationship/parser-diagnostic fields in the named local service projection, REST, native `/mcp`/SSE or legacy MCP as applicable, CLI and dashboard detail; assert raw hydration is reachable only from the named local-search adapter.
- [x] Add synthetic-secret and public/export DTO tests for symbol names/signatures, reference text and diagnostics. Expected RED: leaf-path sanitisation, missing signatures/diagnostics, a detected fact persisted, or raw fields reaching an external DTO.
- [x] Extend only named local projections/adapters and dashboard renderers; do not relax `SqlCorpusProjectionReader`, add a flag to a shared reader, or alter an export serializer. Preserve local file action authority.
- [x] Run focused Python/browser GREEN plus static/type checks, then commit `feat: widen local corpus and code detail`.
- [x] Obtain a fresh independent Task 2 review.

### Task 3: Widen legacy diagnostics, status and audit views without widening mutations

**Files:**
- Modify: `src/flux_llm_kb/database.py`, `service.py`, `rest_api.py`, `mcp_server.py`, `cli.py`, `code_diagnostics.py`, `src/App.tsx`
- Test: audit/diagnostic/dashboard REST, CLI, MCP and UI tests

**Interfaces:** Local diagnostic/audit entries may add bounded `path`, `hash`, `runtime_detail`, `parser_diagnostic` and `retained_provenance` fields. Export/public entries retain the existing aggregate/sanitised shape. Mutation endpoints and their request DTOs are unchanged.

- [x] Write failing tests for exact local diagnostic/audit fields, bounded payload behaviour, secret withholding and unchanged mutation route authority.
- [x] Run focused RED. Expected: existing sanitiser deletes local details or a local field leaks to export/status feed shape.
- [x] Implement separate local detail readers and UI renderers; preserve existing export projections and audit immutability.
- [x] Run focused GREEN, synthetic browser diagnostic/audit checks, Python regression suite subset and `git diff --check`; commit `feat: widen local diagnostics and audit detail`.
- [x] Obtain a fresh independent Task 3 review.

### Task 4: Native retained-branch local detail/read model

**Files:**
- Create: `src/FluxKnowledge.Application/Sources/LocalRetainedDetailProjection.cs`
- Create: `src/FluxKnowledge.Application/Ports/ILocalRetainedDetailReader.cs`
- Create: `src/FluxKnowledge.Infrastructure.SqlServer/Persistence/SqlLocalRetainedDetailReader.cs`
- Modify: `src/FluxKnowledge.Web/Program.cs`, `Components/Sources`, `Components/Events`, direct-loopback/status services
- Test: native Web component/endpoint tests and generated SQL integration tests

**Interfaces:** `ReadAsync(Guid branchId, CancellationToken)` returns immutable branch/revision identifiers, local path, artifact hash/binding, member/child facts, bounded attempt diagnostics and content handles. `ReadExcerptAsync` reads verified retained bytes and returns `LocalDisclosureResult`; it never returns a synthetic locator or source-original path.

- [x] Write generated-SQL and Web RED tests for local retained path/hash/member/provenance detail, malformed binding failure, secret sentinel withholding, remote/proxy denial and separate public/export projection exclusion.
- [x] Implement the read port/SQL reader and local direct-loopback endpoint/page detail. Do not change the existing shared corpus/public reader or branch mutation store.
- [x] Run generated SQL migration/upgrade and race/recovery GREEN, focused Web/browser detail GREEN, Release solution build and model snapshot check; commit `feat: add local retained detail projection`.
- [x] Obtain a fresh independent Task 4 review.

### Task 5: C# descriptor, strict classification and Roslyn parser core

**Files:**
- Create: `src/FluxKnowledge.Application/Sources/RetainedCsharpCodeProcessor.cs`
- Create: `src/FluxKnowledge.Application/Sources/RetainedCsharpCodeCapabilityHandler.cs`
- Modify: `src/FluxKnowledge.Domain/Sources/RetainedProcessorBranch.cs` to add the immutable `RetainedCsharpCodeClaim` domain model; `SourceClassifier.cs`, `SourceScanWorker.cs`, C#-aware activity-planner contracts and package lock/project files. Do not register, activate, promote or default-enable the handler in this task.
- Create: `tests/FluxKnowledge.Domain.Tests/Sources/RetainedCsharpCodeTestData.cs`
- Test: `tests/FluxKnowledge.Domain.Tests/Sources/RetainedCsharpCodeProcessorTests.cs`, `SourceClassifierTests.cs`

**Interfaces:**

```csharp
public static readonly SourceCapabilityDescriptor Capability;
public ValueTask<RetainedCsharpCodeCompletion> ProcessAsync(
    RetainedCsharpCodeClaim claim, CancellationToken cancellationToken);
```

`RetainedCsharpCodeClaim` is a new immutable domain model, materialised from the same persisted `SourceProcessorAttempt` transaction as the claim, that carries the existing branch/source/input/owner/lease-generation/expiry fence values plus the non-empty claimed `AttemptId`; `RetainedProcessorClaim` is not widened or inferred. `RetainedCsharpCodeCompletion` copies that branch/attempt/lease identity, processor version, descriptor fingerprint, immutable parser fingerprint, success-or-blocked outcome, all symbol/reference/diagnostic and withheld counts, the canonical ordered receipt diagnostic-code list, and ordered commands; it contains no source-original locator. A successful completion alone carries document and success `CompletionFingerprint`. A `csharp-code-syntax-invalid` no-document completion instead carries the framed `BlockedCompletionFingerprint` and only bounded blocked-diagnostic commands, each owned by the claimed `AttemptId`; it must not claim a successful document completion. The descriptor is exactly `CodeParsing (5)`/`InProcess`/`AcceptedUtf8Text` and uses the canonical framed SHA-256 fingerprints and UTF-16 fact grammar in the C# design.

- [x] Add failing tests for current `.cs` `DeferredPolicy`/`DocumentParsing` migration and new bounded-text/non-binary `.cs` promotion to `AcceptedUtf8Text` with exclusive `CodeParsing (5)` (never `TextExtraction`), binary-like `.cs` deferral, every other language staying deferred, exact descriptor/parser/document/symbol/reference/diagnostic/completion wire fields, field order, lower-case field names, UTF-8 byte lengths, decimal/null/list encoding and golden vectors, including explicit blocked-diagnostic and no-document blocked-completion wire-record golden vectors; pinned declaration/reference kind codes and every qualified-name form; claim-to-completion propagation of the persisted non-empty `AttemptId`; preflight version/handler mismatch, UTF-16 spans/null-parent encoding, valid UTF-8/BOM facts, syntax failure with bounded blocked diagnostic commands and each numerical limit/precedence pair.
- [x] Run the focused Domain RED. Expected: no descriptor/handler or C# parser behaviour.
- [x] Implement syntax-only Roslyn parsing with `CSharp14`, fixed canonical descriptor/parser fingerprints, ordered fact production, per-fact secret scan/withhold/block outcomes, cancellation checks and the exact outcomes from the design. Before Task 6's migration/writer readiness, the C# classifier/planner creates only the inert `DocumentParsing`/`DeferredUnsupported` C# holding route (never `TextExtraction`); Task 6 replan creates the descriptor-bearing `CodeParsing (5)` row and only then may it be claimable. Do not add workspace/analyser/source-generator/project APIs or a runnable registration seam.
- [x] Run Domain GREEN and package-lock/licence/preflight assertions, then Release build and diff check; commit `feat: add retained Csharp parser core`.
- [x] Obtain a fresh independent Task 5 review.

### Task 6: C# durable facts, hard denials and fenced hosted lifecycle

**Files:**
- Create: code-document/symbol/reference entities and configurations under `src/FluxKnowledge.Infrastructure.SqlServer/Persistence`, immutable C# completion-receipt entity/configuration, plus a complete fenced C# completion writer/registration store
- Create: bounded document-code-diagnostic and branch/attempt-owned blocked-code-diagnostic entities/configurations/tables under `src/FluxKnowledge.Infrastructure.SqlServer/Persistence`
- Modify: `FluxKnowledgeDbContext`, `CanonicalSchemaConfigurations.cs`, `IRetainedProcessorBranchStore.cs`, `SqlRetainedProcessorBranchStore.cs` (C# claim materialisation from the persisted attempt and fenced completion/replay), `SourceScanWorker` C# planner/replanner, the `SourceActivity` domain/entity identity, Operator Action hard-denial migration data, `RetainedProcessorActivationService.cs`, and C# claim/completion plus SQL test fixtures (`SqlTestData.cs`, `NativeSqlServerFixture.cs`)
- Create: additive EF migration, designer and snapshot
- Test: `tests/FluxKnowledge.Integration.Tests/Sources/RetainedCsharpCodeReplayIntegrationTests.cs`, `OoxmlForceRequestFoundationIntegrationTests.cs`

**Interfaces:** `ClaimCsharpCodeAsync(...)` returns only `RetainedCsharpCodeClaim` values materialised with the newly inserted persisted `AttemptId`; it never fabricates one from a branch or lease generation. `CompleteRetainedCsharpCodeAsync(RetainedCsharpCodeClaim claim, RetainedCsharpCodeCompletion completion, CancellationToken)` first does an immutable receipt/replay lookup, then only for a missing receipt validates the serialisable branch/claimed-attempt/owner/generation/unexpired predicate. A successful completion atomically inserts document/symbol/reference/document-diagnostic facts and branch completion. A `csharp-code-syntax-invalid` completion atomically inserts only its nullable-document blocked receipt, framed `BlockedCompletionFingerprint`, and bounded branch/claimed-attempt-owned blocked diagnostic rows; it must not insert a code document, a success child or a successful completion fingerprint. Exact replay returns the original receipt without moving its blocked diagnostics to a newer claim; it matches outcome, source revision, `CodeParsing (5)`, processor version, descriptor fingerprint, parser fingerprint, retained SHA-256, the applicable success-or-blocked completion fingerprint, ordered fact/diagnostic fingerprints, all three withheld counts and exact ordered receipt diagnostic codes. An existing non-exact receipt is `csharp-code-completion-conflict` with no write. The migration replaces the current activity unique identity with `(SourceRevisionId, ActivityKind, ProcessorVersion, DescriptorFingerprint, InputFingerprint)` and gives existing rows the documented legacy fingerprint sentinel.

- [x] Write generated-SQL RED tests for migration schema/FKs/unique/indexed symbols and references; the bounded `(DocumentId, Ordinal)` success diagnostics contract; the separate bounded `(BranchId, AttemptId, Ordinal)` syntax-invalid blocked diagnostics contract with restrictive branch/composite-attempt FKs and explicitly no document FK; C# claim materialisation returning the persisted `AttemptId`; persisted blocked diagnostics referencing exactly that claimed attempt; activity identity migration; all C# hard-denial codes; retained-only success after original removal; corrupt/rebound artifact; stale fence; cancellation/supersession; duplicate/concurrent claim; receipt lookup before expired-lease validation; exact success and syntax-invalid replay versus fixed conflict field-by-field (including the applicable success-or-blocked completion fingerprint, blocked-diagnostic and no-document blocked-completion wire records, three withheld counts and ordered receipt diagnostic codes), including replay retaining the original blocked-diagnostic attempt ownership; restart; no partial document/facts for syntax-invalid; and per-fact secret clean/withheld/scan-failure atomicity.
- [x] Run RED against a generated SQL catalogue. Expected: missing tables/transactional completion and no C# hard-denial rows.
- [x] Implement the activity-identity migration; the C# claim-store path that atomically creates/materialises a persisted `AttemptId`; success/document diagnostics and no-document blocked-diagnostics tables; restrictive constraints; receipt fields for all three withheld counts, blocked-diagnostic count, the applicable success-or-blocked completion fingerprint and canonical ordered diagnostic-code wire value; receipt-first fenced serialisable success-or-blocked completion writer whose missing-receipt predicate includes the claimed attempt and whose replay preserves original blocked-diagnostic ownership; C# replan service and handler registration first. Replan only exact retained-bound `.cs` revisions and fence any legacy active text route before C# creation; leave unresolved legacy route conflicts inert. Only after generated-SQL migration/upgrade proves the writer can persist both complete document facts and no-document syntax-invalid summaries may the default-enabled hosted activation register, promote and claim the C# descriptor; otherwise it must be inert and fail closed. Generate migration/designer/snapshot; do not add a C# Operator Actions capability.
- [x] Run generated-DB migration upgrade, full focused SQL GREEN, EF pending-model check, Gmail/Outlook guard, Release build and diff check; commit `feat: persist retained Csharp code facts`.
- [x] Obtain a fresh independent Task 6 review.

### Task 7: Expose retained C# facts across trusted local transports

**Files:**
- Modify: native local retained detail reader/endpoints/pages from Task 4; Python local search/service/REST/MCP/CLI adapters only if the native hand-off contracts require them
- Test: native Web, Python transport, synthetic browser, privacy/secret-boundary and cross-transport parity tests

**Interfaces:** Local code detail returns `path`, `artifact_hash`, `symbols[]`, `signature`, `references[]`, spans and bounded diagnostics. A code excerpt is a verified-retained read through `ILocalPrivateContentDisclosure`. Export/public DTOs do not contain those members.

- [x] Write cross-transport RED tests for C# symbol/signature/reference/local path/parser diagnostic details, fact-level secret withholding and no source-original reread; include anonymous direct-loopback native `/mcp`/SSE, forwarded/proxy and direct-remote authority, plus `FluxKnowledge.Cli` read-only local detail parity and no mutation command.
- [x] Implement named local-only adapters and UI search/detail links, plus read-only `FluxKnowledge.Cli` local code/detail/diagnostic commands. Keep all mutation routes unchanged and add no MCP/CLI mutation.
- [x] Run generated SQL, focused Web, Python transport and synthetic browser GREEN; run a source-original deletion proof and static no-secret/public-DTO sentinel scans; commit `feat: expose retained Csharp code facts locally`.
- [x] Obtain a fresh independent Task 7 review.

### Task 8: Whole-slice validation, documentation and approval

**Files:**
- Modify: `docs/architecture.md`, `docs/roadmap.md`, `docs/safety.md`, this plan and the two 2026-08-16 designs only if evidence changes them
- Test: combined affected native/Python suites, generated database and synthetic browser matrix

- [x] Run the combined local-detail, per-fact secret-boundary, C# parser/classifier/replanner, blocked-diagnostic and no-document blocked-completion wire-record golden-vector, claimed-attempt ownership, diagnostic migration/upgrade, receipt-first race/recovery, REST/native-CLI/native-`/mcp`/SSE/UI and anonymous direct-loopback matrix against infrastructure provisioned by `ensure-disposable-sql.ps1`; record an explicit failure if LocalDB/local SQL Server cannot be provisioned, never a skipped SQL result. The 2026-08-20 loopback-only matrix passed Domain 73/73, generated-SQL Integration 136/136 and Web 33/33 without skips. The unfiltered legacy Python group failed one already-isolated Windows stdio MCP harness case (`stderr.fileno()` unavailable); the remaining 142 collected cases passed after only that case was deselected.
- [x] Run the disposable-browser helper before every browser slice: it must validate the already-local Playwright/Chromium executable and launch synthetic Chromium with `FLUXKNOWLEDGE_BROWSER_TESTS=1` scoped to that child command. Record missing browser as failed infrastructure and do not install/download or skip. The pre-existing cached Chromium executable passed its local version probe and the child-scoped retained-detail/browser suite passed 4/4, skipped 0.
- [x] Run Release builds with zero warnings, EF pending-model checks for the native schema, package-lock/diff checks, and explicit source-original/Outlook/network/model guard tests. Release `--warnaserror` built with 0 warnings/0 errors; EF found no pending model changes; all 15 lock files parsed; diff checks were clean; source-original deletion, static Outlook and no-deep-model-probe guards passed.
- [x] Record exact passed, failed and unrun evidence in `docs/roadmap.md`; do not call any skipped test passed.
- [x] Obtain one final independent whole-slice review covering policy compliance, secrets, anonymous direct-loopback transport authority, parser preflight, C# planner/replan, diagnostic migration, retained binding, receipt-first fencing/replay, native CLI read-only scope and browser provision. The final independent reviewer approved after the shared 16-claim and C#-specific 8-claim correction; deployment and live validation remain separately prohibited.

## Plan self-review

- **Spec coverage:** Tasks 1–4 deliver all-app local contract widening, including native CLI; Tasks 5–7 deliver the C# descriptor, exclusive planner/replan, parser, diagnostics persistence/lifecycle and output; Task 8 records the required combined evidence and review.
- **No placeholders:** all capability identifiers, `CodeParsing (5)`, package version, limits, new claim/completion interfaces, claimed-attempt materialisation, outcome boundaries, fingerprint encodings, fact grammar, diagnostic contract, activity migration and task gates are specified above.
- **Consistency:** Task 5 produces only an inert C# `DeferredUnsupported` parser completion and never schedules text extraction for `.cs`; it carries the materialised claimed attempt into every no-document syntax-invalid completion with a blocked, never successful, completion fingerprint. Task 6 first delivers activity identity, claim materialisation, diagnostic schema, complete receipt-first writer/registration and retained-bound replan, then can run default-enabled activation. Its atomic persistence is the sole source for Task 7 local projections. No task creates a remote reader, authenticated-user requirement or mutation authority.
